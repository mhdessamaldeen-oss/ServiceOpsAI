namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Internal;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Retrieval;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>
/// The new core LLM step: takes a natural-language question, retrieves the top-K relevant
/// tables, and asks the LLM to fill a <see cref="QuerySpec"/> (form-filling, not free SQL).
/// Replaces <c>LlmPlanner</c>'s big monolithic prompt with a small focused one suitable for
/// local 7B-class models.
///
/// <para>The output is a <see cref="QuerySpec"/> the existing <c>SqlCompiler</c> can turn into
/// safe SQL. The extractor never emits SQL directly — that's the compiler's job.</para>
/// </summary>
public interface ISpecExtractor
{
    Task<SpecExtractionResult> ExtractAsync(string question, CancellationToken cancellationToken = default);

    /// <summary>Retry extraction with feedback about why the previous spec failed (intent-guard
    /// reason, SQL execution error, etc.). The LLM gets the previous spec + error and emits a
    /// corrected spec. Caller decides whether to retry compile / execute with the new spec.</summary>
    Task<SpecExtractionResult> RetryWithErrorAsync(string question, QuerySpec previousSpec, string error, CancellationToken cancellationToken = default);

    /// <summary>Phase 2.3 multi-turn refinement: given the previous turn's spec, ask the LLM to
    /// MODIFY it according to the follow-up question (e.g. "actually only the open ones"). The
    /// returned spec is the refined version. Caller stores it for the NEXT turn's refinement.</summary>
    Task<SpecExtractionResult> RefineAsync(string question, QuerySpec previousSpec, CancellationToken cancellationToken = default);
}

public sealed class SpecExtractionResult
{
    public QuerySpec? Spec { get; init; }
    public IReadOnlyList<string> CandidateTables { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public string? RawLlmOutput { get; init; }    // for trace / diagnosis
    /// <summary>Full prompt sent to the LLM. Populated for traceability — when output is wrong
    /// the developer can inspect exactly what context the LLM had.</summary>
    public string? Prompt { get; init; }
}

internal sealed class SpecExtractor : ISpecExtractor
{
    private readonly ISchemaSemanticRetriever _retriever;
    private readonly ISchemaKnowledge _knowledge;
    private readonly ILlmClient _llm;
    private readonly IPastQuestionStore _pastQuestions;
    private readonly IVerifiedQueryMatcher _vqMatcher;
    private readonly ITemporalParser _temporalParser;
    private readonly IOptions<CopilotOptions> _options;
    private readonly IOptionsMonitor<FkRoleOptions> _fkRoleOptions;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<SpecExtractor> _logger;

    public SpecExtractor(
        ISchemaSemanticRetriever retriever,
        ISchemaKnowledge knowledge,
        ILlmClient llm,
        IPastQuestionStore pastQuestions,
        IVerifiedQueryMatcher vqMatcher,
        ITemporalParser temporalParser,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<FkRoleOptions> fkRoleOptions,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<SpecExtractor> logger)
    {
        _retriever = retriever;
        _knowledge = knowledge;
        _llm = llm;
        _pastQuestions = pastQuestions;
        _vqMatcher = vqMatcher;
        _temporalParser = temporalParser;
        _options = options;
        _fkRoleOptions = fkRoleOptions;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    public Task<SpecExtractionResult> ExtractAsync(string question, CancellationToken cancellationToken = default) =>
        ExtractInternalAsync(question, previousSpec: null, previousError: null, refinement: false, cancellationToken);

    public Task<SpecExtractionResult> RetryWithErrorAsync(
        string question, QuerySpec previousSpec, string error, CancellationToken cancellationToken = default) =>
        ExtractInternalAsync(question, previousSpec, error, refinement: false, cancellationToken);

    public Task<SpecExtractionResult> RefineAsync(
        string question, QuerySpec previousSpec, CancellationToken cancellationToken = default) =>
        ExtractInternalAsync(question, previousSpec, previousError: null, refinement: true, cancellationToken);

    private async Task<SpecExtractionResult> ExtractInternalAsync(
        string question, QuerySpec? previousSpec, string? previousError, bool refinement, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new SpecExtractionResult { Error = "empty question" };

        try
        {
            // Sanity check: retriever needs the embedder + schema knowledge. If either is
            // missing the user's question can't be answered at all — give them a precise
            // error rather than the generic "I couldn't understand".
            if (!_retriever.IsAvailable)
            {
                return new SpecExtractionResult
                {
                    Error = !_knowledge.IsAvailable
                        ? "schema-knowledge not loaded — generate it via Settings → Copilot → Schema Knowledge → Generate"
                        : "embedder unavailable — check Ollama or the configured embedding provider is running",
                };
            }

            var retrieval = await _retriever.RetrieveAsync(question, topK: 3, cancellationToken);
            var tables = retrieval.Tables.Select(t => t.Table).ToList();
            if (tables.Count == 0)
            {
                // Retriever returned nothing despite being "available" — embedder hiccup or
                // genuinely no match. Surface the top schema tables so the user has a hint.
                var topTables = _knowledge.AllTables
                    .Where(t => !t.Flags.IsBridge && !t.Flags.IsLookup)
                    .Take(5)
                    .Select(t => t.Name)
                    .ToList();
                return new SpecExtractionResult
                {
                    Error = topTables.Count > 0
                        ? $"no candidate tables matched. Available: {string.Join(", ", topTables)}"
                        : "no candidate tables",
                };
            }

            // Expand to include neighbor tables the candidates reference / are referenced by,
            // so the LLM has enough context to choose joins. Capped at 6 total to keep the
            // prompt small for local models.
            tables = ExpandWithNeighbors(tables, maxTotal: 6);

            // Fetch top similar past questions (persistent learning) — adds them as worked
            // examples in the prompt. Helps the LLM recognise question shapes it's already seen
            // succeed on. Min similarity from CopilotOptions (default 0.82) keeps only close
            // matches; below that they're noise.
            IReadOnlyList<PastQuestionMatch> pastExamples = Array.Empty<PastQuestionMatch>();
            if (_options.Value.UsePastQuestionRag && previousError is null)
            {
                try
                {
                    pastExamples = await _pastQuestions.FindSimilarAsync(
                        question, _options.Value.FewShotTopK,
                        (float)_options.Value.PastQuestionRagMinSimilarity, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SpecExtractor] past-question lookup failed (non-fatal).");
                }
            }

            // ADDITIONAL FEW-SHOT SOURCE: the hand-curated VerifiedQueries catalog. Past-question
            // RAG only fires when a similar question has actually been asked before (cold-start
            // problem). The VQ catalog is rich (>125 entries) and gold-quality — using it as a
            // few-shot safety net helps the planner on questions that have no past-trace match.
            // Lower threshold (0.65) than MatchAsync's strict trust threshold — these aren't run
            // AS the answer, they're shown to the LLM as worked examples of similar shapes.
            IReadOnlyList<VerifiedMatch> vqExamples = Array.Empty<VerifiedMatch>();
            if (previousError is null)
            {
                try
                {
                    vqExamples = await _vqMatcher.FindTopAsync(question, topK: 3, minSimilarity: 0.65f, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SpecExtractor] VQ few-shot lookup failed (non-fatal).");
                }
            }

            var prompt = BuildUserPrompt(question, tables, previousSpec, previousError, refinement, pastExamples, vqExamples);

            using var hint = Abstractions.LlmCallStageHint.Use(refinement ? "SpecRefine" : "SpecExtractor");
            var raw = await _llm.GenerateJsonAsync(_textCatalog.CurrentValue.SpecExtractorSystemPrompt, prompt, cancellationToken);
            var spec = ParseSpec(raw);
            if (spec is null)
            {
                return new SpecExtractionResult
                {
                    Error = "LLM produced unparseable JSON",
                    CandidateTables = tables.Select(t => t.Name).ToList(),
                    RawLlmOutput = raw,
                    Prompt = prompt,
                };
            }

            NormalizeSpec(spec, question);
            return new SpecExtractionResult
            {
                Spec = spec,
                CandidateTables = tables.Select(t => t.Name).ToList(),
                RawLlmOutput = raw,
                Prompt = prompt,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SpecExtractor] extract failed for '{Q}'.", question);
            return new SpecExtractionResult { Error = ex.Message };
        }
    }

    private List<InferredTable> ExpandWithNeighbors(List<InferredTable> seed, int maxTotal)
    {
        var picked = new List<InferredTable>(seed);
        var seen = new HashSet<string>(seed.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var t in seed)
        {
            if (picked.Count >= maxTotal) break;
            // Outbound FKs — important for "tickets and their status" type joins.
            foreach (var fk in t.ForeignKeysOut)
            {
                if (picked.Count >= maxTotal) break;
                if (!seen.Add(fk.Table)) continue;
                var neighbor = _knowledge.GetTable(fk.Table);
                if (neighbor is not null) picked.Add(neighbor);
            }
            // I8 — inbound FKs (tables that point AT this one). Needed for the "open tickets"
            // class of question where the retriever surfaces only the lookup table (TicketStatuses)
            // and the fact table (Tickets) is missing from the LLM's context, leaving it to
            // hallucinate filters from the Values: line. Walking both directions keeps the
            // join graph visible regardless of which side the retriever picked.
            foreach (var refBack in t.ReferencedBy)
            {
                if (picked.Count >= maxTotal) break;
                if (!seen.Add(refBack.FromTable)) continue;
                var neighbor = _knowledge.GetTable(refBack.FromTable);
                if (neighbor is not null) picked.Add(neighbor);
            }
        }
        return picked;
    }

    // SystemPrompt moved to CopilotTextCatalog.SpecExtractorSystemPrompt (B2 — 2026-05-19).
    // Read from _textCatalog.CurrentValue.SpecExtractorSystemPrompt at the call site so admins
    // can tune planner framing without recompiling — and so the prompt baked into a LoRA adapter
    // is the same one running in production.

    private string BuildUserPrompt(string question, IReadOnlyList<InferredTable> tables,
        QuerySpec? previousSpec = null, string? previousError = null, bool refinement = false,
        IReadOnlyList<PastQuestionMatch>? pastExamples = null,
        IReadOnlyList<VerifiedMatch>? vqExamples = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Question: \"{question}\"");
        sb.AppendLine();

        // Detected-shape hints — surfaced at the top of the prompt before any examples so the
        // LLM picks the right pattern. Comparison hint text comes from CopilotTextCatalog so
        // operators can tune it per deployment / locale without recompiling.
        if (LooksLikeComparison(question))
        {
            sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorComparisonHint);
            sb.AppendLine();
        }

        // Gold-catalog few-shot — show the LLM the closest hand-curated (question, SQL) pairs.
        // Gold quality (every entry signed off by a human), so emit these BEFORE past-trace
        // examples so the LLM weights them highest. Capped at 3 to keep the prompt small.
        if (vqExamples is { Count: > 0 })
        {
            sb.AppendLine("Verified-query examples (gold-quality, prefer these patterns):");
            foreach (var ex in vqExamples.Take(3))
            {
                sb.Append("- Q: \"").Append(ex.Query.Question).AppendLine("\"");
                sb.Append("  SQL: ").AppendLine(CopilotStrings.Truncate(ex.Query.Sql, 240).Replace('\n', ' '));
            }
            sb.AppendLine();
        }

        // Persistent learning: surface previously-successful similar Q/SQL pairs as worked
        // examples. The LLM imitates these — same shape ≈ same spec.
        if (pastExamples is { Count: > 0 })
        {
            sb.AppendLine("Similar previously-answered questions (use as guides):");
            foreach (var ex in pastExamples.Take(3))
            {
                sb.Append("- Q: \"").Append(ex.Question).AppendLine("\"");
                sb.Append("  SQL: ").AppendLine(CopilotStrings.Truncate(ex.GeneratedScript, 220).Replace('\n', ' '));
            }
            sb.AppendLine();
        }
        // Retry context: when we're re-trying after a guard / executor rejection, lead with the
        // failure but DON'T echo the previous spec back. Showing the wrong spec tends to anchor
        // the LLM on its own mistake — it tweaks the broken shape instead of generating a fresh
        // correct one. Just surface the error and let the LLM reason from the question alone.
        if (previousSpec is not null && !string.IsNullOrEmpty(previousError))
        {
            sb.AppendLine(string.Format(_textCatalog.CurrentValue.SpecExtractorRetryPreamble, previousError));
            sb.AppendLine();
        }
        // Multi-turn refinement context: the user is following up on a prior query. The new
        // question is a MODIFICATION of the previous spec, not an independent query. Emit the
        // refined spec — typically the same root + most fields, with one or two changes
        // (extra filter, removed filter, new aggregation, etc.).
        else if (previousSpec is not null && refinement)
        {
            sb.AppendLine(string.Format(_textCatalog.CurrentValue.SpecExtractorRefinementPreamble,
                JsonSerializer.Serialize(previousSpec, CopilotJson.CompactWrite)));
            sb.AppendLine();
        }
        sb.AppendLine("Available tables:");
        foreach (var t in tables)
        {
            AppendTable(sb, t);
            sb.AppendLine();
        }
        sb.AppendLine("QuerySpec shape (return EXACTLY this JSON structure, fill in values):");
        sb.AppendLine("{");
        sb.AppendLine("  \"intent\": \"data_query\",");
        sb.AppendLine("  \"root\": \"<one of the tables above>\",");
        sb.AppendLine("  \"select\": [\"Table.Column\"],");
        sb.AppendLine("  \"filters\": [{\"column\":\"Table.Column\",\"op\":\"eq|neq|gt|gte|lt|lte|like|in|is_null|not_null\",\"value\":\"...\"}],");
        sb.AppendLine("  \"aggregations\": [{\"function\":\"COUNT|SUM|AVG|MIN|MAX\",\"column\":\"*|Table.Column\",\"alias\":\"Count\"}],");
        sb.AppendLine("  \"groupBy\": [\"Table.Column\"],");
        sb.AppendLine("  \"orderBy\": [{\"column\":\"Table.Column\",\"direction\":\"asc|desc\"}],");
        sb.AppendLine("  \"limit\": null,");
        sb.AppendLine("  \"distinct\": false,");
        sb.AppendLine("  \"joins\": [{\"table\":\"OtherTable\",\"kind\":\"inner|left|anti\"}],");
        sb.AppendLine("  \"computed\": [{\"alias\":\"Age\",\"expression\":\"DATEDIFF(day, Tickets.CreatedAt, GETDATE())\"}],");
        sb.AppendLine("  \"clarificationQuestion\": \"\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ALWAYS qualify columns as \"Table.Column\" (never bare).");
        sb.AppendLine("- ★ STRUCTURAL: \"single-row aggregate\" (COUNT/SUM/AVG/MIN/MAX of all rows, optionally filtered) → select MUST be []; aggregations[] MUST have the metric; groupBy MUST be []. Empty select is REQUIRED for a pure single-row aggregate. Adding a filter does NOT change this — keep select EMPTY even when filtering.");
        sb.AppendLine("- ★ \"how many X\" / \"count of X\" / \"number of X\" / \"total X\" → COUNT(*) aggregation; if user said \"distinct\" or \"unique\" → COUNT(DISTINCT <col>) via Distinct:true.");
        sb.AppendLine("- ★ \"average X\" / \"avg X\" / \"mean X\" / \"X time\" (when X is a duration) → AVG aggregation. For time durations, the column expression is DATEDIFF(hour|day|minute, <start>, <end>). Never use COUNT for an average. Never omit the AVG aggregation. \"average X by Y\" → AVG + groupBy [Y].");
        sb.AppendLine("- ★ \"max X\" / \"highest X\" / \"largest X\" / \"latest X\" (when X is a date) → MAX aggregation on the column. \"max X by Y\" → MAX + groupBy [Y].");
        sb.AppendLine("- ★ \"min X\" / \"lowest X\" / \"smallest X\" / \"earliest X\" (when X is a date) → MIN aggregation on the column. \"min X by Y\" → MIN + groupBy [Y].");
        sb.AppendLine("- ★ \"sum X\" / \"total X\" (when X is a numeric metric, not just a row count) → SUM aggregation on the column.");
        sb.AppendLine("- ★ \"distinct X\" / \"unique X\" / \"how many different X\" → COUNT with Distinct:true on the column (NOT the * column).");
        sb.AppendLine("- \"list X\" / \"show X\" / \"give X\" → select with the label column, no aggregations.");
        sb.AppendLine("- \"top N X\" or \"first N X\" → set limit: N. \"top N X by Y\" → ALSO orderBy Y desc.");
        sb.AppendLine("- \"X by <lookup-dimension>\" (status / category / priority / region / type / etc.) → filter on the LOOKUP table's label column (e.g. \"<LookupTable>.Name\" eq \"<value>\"), and groupBy the same column when aggregating.");
        sb.AppendLine("- \"X with no Y\" / \"X without any Y\" → joins:[{table:Y, kind:\"anti\"}].");
        sb.AppendLine("- ★ COMPARISON (\"X vs Y\" / \"compare X and Y\" / \"this X vs last X\" / \"YoY\" / \"MoM\" / \"WoW\" / \"today vs yesterday\" / \"2024 vs 2025\"): emit ONE SUM(CASE WHEN <per-bucket predicate> THEN 1 ELSE 0 END) aggregation PER BUCKET. Keep filters[] EMPTY — the per-bucket predicates live INSIDE the aggregation column expressions. NEVER duplicate COUNT(*) under different aliases with the same WHERE — that yields the same number twice.");
        sb.AppendLine("- ★ NO SUBQUERIES in aggregation columns or computed expressions. To compare against a lookup value (\"= 'Critical'\"), reference the JOINED lookup table directly (e.g. CASE WHEN TicketPriorities.Name = 'Critical' ...) and add the lookup table to joins[]. NEVER write \"(SELECT Id FROM ...)\" inside an aggregation column — the validator rejects it.");
        sb.AppendLine(BuildFkRolePromptRule());
        sb.AppendLine("- Reference unknown columns via computed expressions if derivable (e.g. \"age\" → DATEDIFF from CreatedAt).");
        sb.AppendLine("- Use ISO dates or \"today\"/\"last_7_days\"/\"last_30_days\"/\"this_month\"/\"last_month\" for date filters.");
        sb.AppendLine();
        sb.AppendLine("Worked examples (mimic the SHAPE; column names will differ for your schema):");
        sb.AppendLine();
        sb.AppendLine("Q: \"how many tickets\"   <-- pure COUNT — empty select, aggregations only");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"how many open tickets\"   <-- COUNT + lookup filter. CRITICAL: keep select EMPTY even when adding a filter.");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Open\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"how many rejected tickets\"   <-- same shape; the value is the LOOKUP NAME, not an ID");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Rejected\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"users with no tickets\"   <-- ANTI-JOIN: \"with no / without / having no\" → kind:\"anti\", NO subquery, NO filter");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\",\"AspNetUsers.Email\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Tickets\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets without any comments\"   <-- another anti-join example");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"TicketComments\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"open tickets\"   <-- lookup VALUE \"Open\" → filter on lookup table's NAME column");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Open\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"closed tickets\"");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Closed\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"top 5 users by ticket count\"");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"AspNetUsers.UserName\"],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":5,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"show users and their roles\"   <-- many-to-many through a bridge; the compiler walks the FK graph");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\",\"AspNetRoles.Name\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"running total of tickets created over time\"   <-- WINDOW FUNCTION — use computed expression");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.CreatedAt\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"Tickets.CreatedAt\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"RunningCount\",\"expression\":\"COUNT(*) OVER (ORDER BY Tickets.CreatedAt)\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"rank users by ticket count\"   <-- ROW_NUMBER / RANK via window function");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"AspNetUsers.UserName\"],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Rank\",\"expression\":\"ROW_NUMBER() OVER (ORDER BY COUNT(Tickets.Id) DESC)\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"monthly ticket volume for the last 3 months\"   <-- DATE BUCKETING — use computed alias + raw expression in groupBy");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"last_90_days\"}],\"groupBy\":[\"FORMAT(Tickets.CreatedAt, 'yyyy-MM')\"],\"orderBy\":[{\"column\":\"Month\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Month\",\"expression\":\"FORMAT(Tickets.CreatedAt, 'yyyy-MM')\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets per priority and status\"   <-- MULTI-DIMENSION GROUP BY — both dimensions must appear in groupBy");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\",\"TicketStatuses.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[\"TicketPriorities.Name\",\"TicketStatuses.Name\"],\"orderBy\":[{\"column\":\"Count\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"ticket count by category and source\"   <-- same shape, different dimensions — \"by X and Y\" and \"per X and Y\" both mean two groupBy entries");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\",\"TicketSources.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[\"TicketCategories.Name\",\"TicketSources.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"daily ticket count for the last 14 days\"   <-- DAILY BUCKETING");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"last_14_days\"}],\"groupBy\":[\"CAST(Tickets.CreatedAt AS DATE)\"],\"orderBy\":[{\"column\":\"Day\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Day\",\"expression\":\"CAST(Tickets.CreatedAt AS DATE)\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"average resolution time in hours\"   <-- DATEDIFF AS A METRIC — use aggregation with column as expression");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.ResolvedAt)\",\"alias\":\"AvgResolutionHours\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"average resolution time in hours by priority\"   <-- DATEDIFF + GROUP BY LOOKUP");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.ResolvedAt)\",\"alias\":\"AvgResolutionHours\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[\"TicketPriorities.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"average time to first response in hours, by priority\"   <-- AVG of DATEDIFF + GROUP BY — exact same shape as above");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.FirstRespondedAt)\",\"alias\":\"AvgFirstResponseHours\"}],\"filters\":[{\"column\":\"Tickets.FirstRespondedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[\"TicketPriorities.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"latest ticket creation date\" / \"most recent ticket\"   <-- MAX on a date column — single-row aggregate, empty select");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"MAX\",\"column\":\"Tickets.CreatedAt\",\"alias\":\"LatestCreated\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"earliest ticket creation date\" / \"oldest ticket created\"   <-- MIN on a date column — single-row aggregate, empty select");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"MIN\",\"column\":\"Tickets.CreatedAt\",\"alias\":\"EarliestCreated\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"highest priority value\" / \"top priority weight\"   <-- MAX on a numeric column from a lookup table — single-row aggregate");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"TicketPriorities\",\"select\":[],\"aggregations\":[{\"function\":\"MAX\",\"column\":\"TicketPriorities.SortOrder\",\"alias\":\"MaxPriorityWeight\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"how many distinct users created tickets\" / \"number of unique creators\"   <-- COUNT DISTINCT — Distinct:true on the column, NOT *");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.CreatedByUserId\",\"alias\":\"DistinctCreators\",\"distinct\":true}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"how many different categories have tickets\"   <-- COUNT DISTINCT through a join");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.CategoryId\",\"alias\":\"DistinctCategories\",\"distinct\":true}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"show me all ticket information\" / \"show full details of ticket TCK-001\" / \"everything we know about this user\"   <-- ALL-COLUMNS — use the sentinel select:[\"*\"] and the enricher expands it to the entity's full content columns");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"*\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"show ticket age in days\"   <-- DATEDIFF COMPUTED COLUMN (per-row, not aggregate)");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"AgeInDays\",\"expression\":\"DATEDIFF(day, Tickets.CreatedAt, GETDATE())\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"compare tickets this month vs last month\"   <-- PERIOD COMPARISON via conditional aggregation");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"ThisMonth\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEFROMPARTS(YEAR(DATEADD(month,-1,GETDATE())),MONTH(DATEADD(month,-1,GETDATE())),1) AND Tickets.CreatedAt < DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"LastMonth\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets created today vs yesterday\"   <-- TODAY/YESTERDAY via conditional aggregation");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN CAST(Tickets.CreatedAt AS DATE) = CAST(GETDATE() AS DATE) THEN 1 ELSE 0 END\",\"alias\":\"Today\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN CAST(Tickets.CreatedAt AS DATE) = CAST(DATEADD(day,-1,GETDATE()) AS DATE) THEN 1 ELSE 0 END\",\"alias\":\"Yesterday\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets resolved this year vs last year\"   <-- YEAR-OVER-YEAR via conditional aggregation");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.ResolvedAt) = YEAR(GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"ThisYear\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.ResolvedAt) = YEAR(GETDATE()) - 1 THEN 1 ELSE 0 END\",\"alias\":\"LastYear\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets created in 2025 vs 2024\"   <-- EXPLICIT YEARS via conditional aggregation");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.CreatedAt) = 2025 THEN 1 ELSE 0 END\",\"alias\":\"Y2025\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.CreatedAt) = 2024 THEN 1 ELSE 0 END\",\"alias\":\"Y2024\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"last 7 days vs the previous 7 days\"   <-- ROLLING-WINDOW COMPARISON via conditional aggregation");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEADD(day,-7,GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"Last7Days\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEADD(day,-14,GETDATE()) AND Tickets.CreatedAt < DATEADD(day,-7,GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"Previous7Days\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"what percentage of tickets are resolved or closed\"   <-- RATIO / PERCENTAGE via SUM(CASE) / COUNT(*)");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Total\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN TicketStatuses.Name IN ('Resolved','Closed') THEN 1 ELSE 0 END\",\"alias\":\"ResolvedOrClosed\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"PercentResolved\",\"expression\":\"100.0 * SUM(CASE WHEN TicketStatuses.Name IN ('Resolved','Closed') THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0)\"}],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets created in April 2026\"   <-- SPECIFIC MONTH — use ISO date range filter");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"2026-04-01\"},{\"column\":\"Tickets.CreatedAt\",\"op\":\"lt\",\"value\":\"2026-05-01\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"categories with more than 15 tickets\"   <-- HAVING — post-aggregation filter on a COUNT/SUM");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"TicketCategories.Name\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"*\",\"op\":\"gt\",\"value\":15}],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"agents with more than 5 unresolved tickets\"   <-- HAVING combined with a filter");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"Unresolved\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"is_null\",\"value\":null}],\"groupBy\":[\"AspNetUsers.UserName\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"op\":\"gt\",\"value\":5}],\"orderBy\":[{\"column\":\"Unresolved\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"distinct categories that have tickets\"   <-- DISTINCT — use distinct:true, omit aggregations");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"TicketCategories.Name\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"distinct\":true,\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"unassigned tickets\" / \"tickets without an owner\" / \"items with no assignee\"   <-- ABSENCE on an FK column → filter op:\"is_null\" on the FK column itself, no anti-join needed");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.AssignedToUserId\",\"op\":\"is_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"child tickets with their parent ticket number\"   <-- SELF-REFERENCE: filter where ParentTicketId IS NOT NULL and SELECT both the child's TicketNumber AND the parent FK column. Full parent details (TicketNumber, Title) via self-join aren't supported yet — the user can resolve the parent in a follow-up question.");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\",\"Tickets.ParentTicketId\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.ParentTicketId\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"tickets matching 'login' in title or description\"   <-- CROSS-COLUMN TEXT SEARCH — use op:\"text_search\" so the compiler ORs over the entity's searchable columns");
        sb.AppendLine("→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets\",\"op\":\"text_search\",\"value\":\"login\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"top tickets\"   <-- AMBIGUOUS: top by what? Ask before guessing.");
        sb.AppendLine("→ {\"intent\":\"clarification\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"Top tickets by what? Options: created date (newest), priority, status, or assignee load.\"}");
        sb.AppendLine();
        sb.AppendLine("Q: \"recent stuff\"   <-- AMBIGUOUS: which entity? Ask.");
        sb.AppendLine("→ {\"intent\":\"clarification\",\"root\":\"\",\"select\":[],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"Which data? Recent tickets, users, comments, or something else?\"}");
        sb.AppendLine();
        // Reminders block — text comes from CopilotTextCatalog so operators can tune wording
        // per deployment. The relative-date keyword line is data-driven and stays inline so it
        // always reflects the live RelativeDateKeywords config.
        sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorReminders);
        sb.Append("  Relative-date keywords: ");
        var relDateKeywords = _textCatalog.CurrentValue?.RelativeDateKeywords;
        if (relDateKeywords is { Count: > 0 })
            sb.AppendLine(string.Join(", ", relDateKeywords.Select(k => $"\"{k}\"")) + ".");
        else
            sb.AppendLine("\"today\", \"yesterday\", \"last_7_days\", \"last_30_days\", \"this_week\", \"this_month\", \"this_year\", \"last_week\", \"last_month\", \"last_year\".");
        sb.AppendLine();
        // Recency-weighted comparison reminder. Models attend more to tokens near the end of the
        // prompt — repeating the rule here (after the examples + reminders, right before the
        // "output JSON now" trigger) significantly improves rule adherence on small models.
        if (LooksLikeComparison(question))
        {
            sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorComparisonFinalCheck);
            sb.AppendLine();
        }
        sb.AppendLine("Return ONLY the QuerySpec JSON for the question above, nothing else.");
        return sb.ToString();
    }

    /// <summary>
    /// Build the FK-role prompt rule from the SAME <see cref="FkRoleOptions"/> the schema
    /// inference uses. Adding a role to the JSON automatically extends both the inference
    /// AND the LLM teaching — there is no second place to update. Returns a single line
    /// suitable for appending to the prompt's "Rules:" section.
    /// </summary>
    /// <remarks>
    /// Falls back to a minimal default sentence when the options are empty or absent (e.g.
    /// a fresh deployment with no JSON file yet); the rule degrades gracefully rather than
    /// emitting a broken or empty rule.
    /// </remarks>
    private string BuildFkRolePromptRule()
    {
        var patterns = _fkRoleOptions.CurrentValue?.Patterns;
        if (patterns is not { Count: > 0 })
        {
            return "- ★ FK ROLES: when a column carries a role= tag, map the question's verb to that role (e.g. \"created by\" → creator FK).";
        }
        // Render: "verb-A / verb-B → role-X FK; verb-C → role-Y FK; …"
        var verbToRole = new StringBuilder();
        foreach (var role in patterns)
        {
            if (role.QuestionVerbs is not { Count: > 0 }) continue;
            if (verbToRole.Length > 0) verbToRole.Append("; ");
            verbToRole.Append(string.Join(" / ", role.QuestionVerbs.Select(v => $"\"{v}\"")));
            verbToRole.Append(" → ").Append(role.Role).Append(" FK");
        }
        // The DEFAULT-when-ambiguous role is the FIRST entry in the list — operators control
        // this by ordering their config (creator first by convention).
        var defaultRole = patterns[0].Role;
        return $"- ★ FK ROLES: when a table has multiple FKs to the same target, each FK column carries a role= tag. MAP THE QUESTION'S VERB to the matching role: {verbToRole}. The DEFAULT when the question says just \"by X\" without a verb is the {defaultRole} FK. Pick the right FK column when joining — do NOT default to alphabetical order.";
    }

    private static void AppendTable(StringBuilder sb, InferredTable t)
    {
        var kind = t.Flags.IsLookup ? " [lookup]"
                : t.Flags.IsBridge ? " [bridge]"
                : t.Flags.IsPerson ? " [person]"
                : "";
        sb.Append("- ").Append(t.Name).Append(kind);
        if (!string.IsNullOrEmpty(t.Description)) sb.Append(" — ").Append(t.Description);
        sb.AppendLine();
        if (t.PrimaryKey.Count > 0) sb.Append("  PK: ").AppendLine(string.Join(", ", t.PrimaryKey));
        if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) sb.Append("  Label: ").AppendLine(t.Roles.LabelColumn);
        if (!string.IsNullOrEmpty(t.Roles.SoftDeleteColumn))
            sb.Append("  Soft-delete: ").Append(t.Roles.SoftDeleteColumn).AppendLine(" (compiler auto-filters)");
        if (!string.IsNullOrEmpty(t.Roles.NaturalKey)) sb.Append("  Natural key: ").AppendLine(t.Roles.NaturalKey);
        // Real values for lookup tables — let the LLM use these literally instead of guessing.
        if (t.SampleValues is { Count: > 0 })
            sb.Append("  Values: ").AppendLine(string.Join(", ", t.SampleValues.Take(10)));

        sb.AppendLine("  Columns:");
        foreach (var c in t.Columns)
        {
            sb.Append("    ").Append(t.Name).Append('.').Append(c.Name);
            sb.Append(" (").Append(c.Type);
            if (c.Nullable) sb.Append(", nullable");
            if (!string.IsNullOrEmpty(c.Role)) sb.Append(", ").Append(c.Role);
            if (!string.IsNullOrEmpty(c.DateRole)) sb.Append(", ").Append(c.DateRole);
            // FK verb-role — let the LLM disambiguate "created by" vs "assigned to" by READING
            // this tag, not by guessing which FK to join. Inferred from the column-name suffix
            // by SchemaInferenceGenerator. Entity-agnostic: works for any *By/*To FK on any table.
            if (!string.IsNullOrEmpty(c.FkRole)) sb.Append(", role=").Append(c.FkRole);
            if (!string.IsNullOrEmpty(c.References)) sb.Append(" → ").Append(c.References);
            sb.AppendLine(")");
        }
    }

    /// <summary>Deterministic post-processing of the LLM's spec — fixes common LLM mistakes
    /// before the spec hits the compiler / intent guard. Cheaper than retrying the LLM.</summary>
    private void NormalizeSpec(QuerySpec spec, string question)
    {
        if (spec is null) return;
        NormalizeDateFilters(spec);
        NormalizeNameLikeFilters(spec);
        NormalizeCountAndAntiJoinFilters(spec);
        NormalizeLeftJoinPhrases(spec, question);
    }

    /// <summary>Promotes non-anti joins to LEFT when the question contains any of the catalog's
    /// LeftJoinPhrases (e.g. "with their", "and their", "alongside"). Universal — no entity
    /// references; the entire decision is driven by configurable phrase strings. Without this,
    /// the LLM often leaves the join kind unset and the compiler defaults to INNER for projection
    /// joins on non-nullable FKs (dropping unrelated root rows the user expected to see).</summary>
    private void NormalizeLeftJoinPhrases(QuerySpec spec, string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        var phrases = _textCatalog.CurrentValue?.LeftJoinPhrases;
        if (phrases is not { Count: > 0 }) return;
        var hit = false;
        foreach (var p in phrases)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (question.Contains(p, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
        }
        if (!hit) return;
        foreach (var j in spec.Joins)
        {
            if (string.Equals(j.Kind, SpecConstants.JoinKinds.Anti, StringComparison.OrdinalIgnoreCase))
                continue;
            j.Kind = SpecConstants.JoinKinds.Left;
        }
    }

    /// <summary>Extracted from the original NormalizeSpec block. Cleans up two LLM patterns:
    /// (a) COUNT(*) with non-empty SELECT (clear SELECT for pure count);
    /// (b) anti-join + redundant null-checks/NOT IN filters (drop the redundant filters).</summary>
    private static void NormalizeCountAndAntiJoinFilters(QuerySpec spec)
    {
        // Bug pattern: LLM emits COUNT(*) AND populates SELECT with content columns. The
        // compiler honors both → degenerate GROUP BY + COUNT producing one row per record.
        // For a pure-count question (no GroupBy declared), clear the SELECT list.
        var hasCountStar = spec.Aggregations.Any(a =>
            a.Function.Equals(SpecConstants.Aggregates.Count, StringComparison.OrdinalIgnoreCase)
            && a.Column == SpecConstants.Aggregates.Star);
        if (hasCountStar && spec.GroupBy.Count == 0 && spec.Select.Count > 0)
        {
            spec.Select.Clear();
        }
        // Bug pattern: LLM emits an anti-join AND redundant NOT IN filters. The anti-join
        // alone produces the right SQL. Drop filters that target the anti-join's table column.
        var antiTables = spec.Joins
            .Where(j => string.Equals(j.Kind, SpecConstants.JoinKinds.Anti, StringComparison.OrdinalIgnoreCase))
            .Select(j => j.Table)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (antiTables.Count > 0)
        {
            // When an anti-join is declared the LEFT JOIN + IS NULL on the target's FK already
            // expresses "no matching row". The LLM frequently adds REDUNDANT filters that mangle
            // the SQL: `is_null` on the root PK, `not_in` with a subquery in the value, or
            // hallucinated FK columns on the target. Strip all of these.
            spec.Filters.RemoveAll(f =>
            {
                var col = f.Column ?? "";
                var dot = col.IndexOf('.');
                var table = dot > 0 ? col[..dot] : col;
                // a) Any filter touching the anti-join target table.
                if (antiTables.Contains(table)) return true;
                // b) is_null / not_null filters on the ROOT table — the LLM emits these to "say
                //    no match" but the anti-join already does that.
                if (string.Equals(table, spec.Root, StringComparison.OrdinalIgnoreCase)
                    && IsNullCheckOp(f.Op)) return true;
                // c) not_in filters whose value text mentions the anti-join target (the LLM
                //    sometimes stuffs a subquery into `value`).
                if (IsNotInOp(f.Op)
                    && antiTables.Any(t => (f.Value?.ToString() ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return true;
                return false;
            });
        }
    }

    // Comparison detection — schema-agnostic. Phrases live in CopilotTextCatalog.ComparisonPhrases
    // so per-deployment / per-locale extensions don't require recompiling. Match is case-insensitive
    // substring; that's enough for "vs", "compared to", "year over year", Arabic "مقارنة", etc.
    private bool LooksLikeComparison(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var phrases = _textCatalog.CurrentValue?.ComparisonPhrases;
        if (phrases is null || phrases.Count == 0) return false;
        var q = question.ToLowerInvariant();
        foreach (var p in phrases)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (q.Contains(p.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Small op-classifier helpers — single source of truth for the op-name spellings the
    // LLM emits. Using these instead of inline string.Equals chains keeps the literals
    // confined to SpecConstants.
    private static bool IsNullCheckOp(string op) =>
        op.Equals(SpecConstants.FilterOps.IsNull, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.IsNullAlt, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.NotNull, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.NotNullAlt, StringComparison.OrdinalIgnoreCase);

    private static bool IsNotInOp(string op) =>
        op.Equals(SpecConstants.FilterOps.NotIn, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.NotInAlt, StringComparison.OrdinalIgnoreCase);

    // SQL pseudo-arithmetic the LLM sometimes emits as a literal value — not natural language,
    // so the temporal recognizer doesn't see it. Two patterns:
    //   "today - 7 days"            → today minus N
    //   "DATEADD(day, -7, GETDATE())" → today plus N (signed)
    // These survive as cheap regexes; the rest is handled by the multilingual recognizer.
    private static readonly System.Text.RegularExpressions.Regex RelativeDateRegex =
        new(@"^(today|now)\s*[-]\s*(\d{1,4})\s*(day|days|d|week|weeks|w|month|months|m|year|years|y)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DateAddRegex =
        new(@"DATEADD\s*\(\s*(day|days|month|months|year|years|week|weeks)\s*,\s*(-?\d+)\s*,\s*GET(?:UTC)?DATE\s*\(\s*\)\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Rewrites filter values that look like date expressions into actual DateTime values.
    /// Uses <see cref="ITemporalParser"/> (Microsoft.Recognizers.Text.DateTime under the hood) to
    /// handle the multilingual natural-language cases — "today", "last 7 days", "April 2026",
    /// "this month", "next quarter", "two weeks ago", etc. SQL pseudo-arithmetic ("today - 7 days",
    /// "DATEADD(...)") stays on cheap regexes because it isn't natural language.
    /// Forces <c>gte</c> for past-relative bounds so the SQL is "from X onwards" even when the LLM
    /// picks the wrong op for a relative-time phrase.</summary>
    private void NormalizeDateFilters(QuerySpec spec)
    {
        var today = DateTime.UtcNow.Date;
        var rangeUpperBoundsToAdd = new List<FilterSpec>();
        foreach (var f in spec.Filters)
        {
            var raw = (f.Value?.ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            // Skip filters on non-date columns. Without this gate the TemporalParser parses values
            // like "TCK-2026-00121" (a natural-key identifier on a string column) as "year 2026",
            // overwrites the filter with a DateTime, and the compiler emits a string-vs-date
            // comparison that SQL Server rejects with "Conversion failed when converting date and/or
            // time from character string". Schema-driven: only normalize values targeting columns
            // whose inferred Role is `date` (set by SchemaInferenceGenerator from sql_type +
            // column-name patterns). Universal — works on any DB whose date columns are typed correctly.
            if (!IsDateColumn(f.Column)) continue;

            // 1. "today - N units" — pseudo-arithmetic.
            var m = RelativeDateRegex.Match(raw);
            if (m.Success)
            {
                f.Value = ShiftDate(today, -int.Parse(m.Groups[2].Value), m.Groups[3].Value.ToLowerInvariant());
                f.Op = SpecConstants.FilterOps.Gte;
                continue;
            }

            // 2. "DATEADD(unit, N, GETDATE())" — SQL expression literal.
            m = DateAddRegex.Match(raw);
            if (m.Success)
            {
                f.Value = ShiftDate(today, int.Parse(m.Groups[2].Value), m.Groups[1].Value.ToLowerInvariant());
                // Keep the LLM's op — DATEADD with negative N + `<` means "older than", a valid intent.
                continue;
            }

            // 3. Natural language (multilingual via Microsoft.Recognizers.Text.DateTime).
            // Compact tokens like "last_7_days" / "this-month" are normalized to spaced phrasing
            // before parsing so the recognizer sees them as "last 7 days" / "this month".
            var natural = raw.Replace('_', ' ').Replace('-', ' ');
            var scope = _temporalParser.Parse(natural);
            if (scope is null) continue;

            if (scope.Kind == TemporalScopeKind.SpecificRange
                && scope.Start.HasValue && scope.End.HasValue
                && scope.End.Value.Date > scope.Start.Value.Date)
            {
                // True range (e.g. "April 2026") — set the lower bound here, queue the upper.
                f.Value = scope.Start.Value.Date;
                f.Op = SpecConstants.FilterOps.Gte;
                rangeUpperBoundsToAdd.Add(new FilterSpec
                {
                    Column = f.Column,
                    Op = SpecConstants.FilterOps.Lt,
                    Value = scope.End.Value.Date.AddDays(1),
                });
            }
            else if (scope.Start.HasValue)
            {
                f.Value = scope.Start.Value.Date;
                f.Op = SpecConstants.FilterOps.Gte;
            }
        }
        spec.Filters.AddRange(rangeUpperBoundsToAdd);
    }

    /// <summary>Phase 2.1 value fuzzy match: when the LLM emits `eq` on a name-like string column
    /// (label, natural key, columns containing Name/Title/Email/UserName), convert it to
    /// `like %value%` so partial matches work — e.g. "ahmed" finds "Ahmed Hassan".
    ///
    /// <para>Skipped when the value already contains wildcards (LLM used like), is numeric, is a
    /// DateTime, or the column isn't text-shaped. Skipped for FK / PK columns where exact-match
    /// is correct semantics.</para></summary>
    private void NormalizeNameLikeFilters(QuerySpec spec)
    {
        if (!_knowledge.IsAvailable) return;
        foreach (var f in spec.Filters)
        {
            if (!string.Equals(f.Op, SpecConstants.FilterOps.Eq, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Value is null) continue;
            if (f.Value is DateTime) continue;
            var valueStr = f.Value.ToString();
            if (string.IsNullOrWhiteSpace(valueStr)) continue;
            if (valueStr.Contains('%')) continue;
            if (decimal.TryParse(valueStr, out _)) continue;            // numeric stays exact
            if (bool.TryParse(valueStr, out _)) continue;               // boolean stays exact

            // Resolve the column → table + role.
            var col = f.Column ?? "";
            var dot = col.IndexOf('.');
            if (dot <= 0) continue;
            var tableName = col[..dot];
            var colName = col[(dot + 1)..];
            var table = _knowledge.GetTable(tableName);
            if (table is null) continue;
            var column = table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;
            // Exact match required for keys.
            if (column.Role is SpecConstants.ColumnRoles.PrimaryKey
                              or SpecConstants.ColumnRoles.ForeignKey) continue;
            if (!IsTextType(column.Type)) continue;

            // Only fuzz on columns the schema inferrer marked as label / natural-key. No
            // English-column-name fallback — every signal here comes from schema metadata,
            // so this works the same way on a Transactions app, an Arabic app, anywhere. If
            // the inferrer mislabels a column, fix it via the schema-overrides file, not here.
            var isNameLike =
                column.Role is SpecConstants.ColumnRoles.Label
                              or SpecConstants.ColumnRoles.NaturalKey;
            if (!isNameLike) continue;
            // Lookup tables hold short controlled vocabularies — exact match is the right
            // semantic for their label column.
            if (table.Flags.IsLookup) continue;

            f.Op = SpecConstants.FilterOps.Like;
            f.Value = "%" + valueStr + "%";
        }
    }

    private static bool IsTextType(string dataType) =>
        dataType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("nchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("char", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("ntext", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the column reference resolves to a date / datetime / time column in the
    /// inferred schema. Used by <see cref="NormalizeDateFilters"/> to skip temporal parsing on
    /// non-date columns (e.g. natural-key strings like "TCK-2026-00121"). Two signal sources:
    ///   (a) the inferred Role tag (`date` — set by SchemaInferenceGenerator)
    ///   (b) the raw SQL data type (datetime/datetime2/date/time/datetimeoffset/smalldatetime)
    /// Either signal is sufficient — operators may override Role via schema-overrides.json.
    /// Returns false (skip normalization) when knowledge is unavailable, the column ref can't be
    /// resolved, or the type isn't date-shaped — safer to over-skip than to corrupt a string filter.
    /// </summary>
    private bool IsDateColumn(string? columnRef)
    {
        if (string.IsNullOrWhiteSpace(columnRef)) return false;
        if (!_knowledge.IsAvailable) return false;
        var dot = columnRef.IndexOf('.');
        if (dot <= 0) return false;
        var tableName = columnRef[..dot];
        var colName = columnRef[(dot + 1)..];
        var table = _knowledge.GetTable(tableName);
        if (table is null) return false;
        var column = table.Columns.FirstOrDefault(c =>
            string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
        if (column is null) return false;
        if (string.Equals(column.Role, SpecConstants.ColumnRoles.Date, StringComparison.OrdinalIgnoreCase))
            return true;
        var t = column.Type ?? "";
        return t.StartsWith("date", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("datetime", StringComparison.OrdinalIgnoreCase)
            || t.Equals("time", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("smalldatetime", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ShiftDate(DateTime baseDate, int amount, string unit) => unit switch
    {
        "day" or "days" or "d" => baseDate.AddDays(amount),
        "week" or "weeks" or "w" => baseDate.AddDays(amount * 7),
        "month" or "months" or "m" => baseDate.AddMonths(amount),
        "year" or "years" or "y" => baseDate.AddYears(amount),
        _ => baseDate.AddDays(amount),
    };

    private QuerySpec? ParseSpec(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJsonObject(raw);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<QuerySpec>(json, CopilotJson.Lenient);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SpecExtractor] JSON parse failed. Raw: {Raw}", raw);
            return null;
        }
    }

    // Local models sometimes wrap JSON in ```json fences or add prose. Pluck the first {...} block.
    // Brace-balance scan is string-aware: braces inside JSON string literals are ignored, and
    // \" inside a string doesn't close it. Without this, a value like
    //   "clarificationQuestion": "Use {curly} as placeholder"
    // would close the object early and the parser would fail.
    private static string? ExtractJsonObject(string raw)
    {
        var s = raw.AsSpan();
        int start = -1, depth = 0;
        bool inString = false, escape = false;
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0 && start >= 0) return raw.Substring(start, i - start + 1);
            }
        }
        return null;
    }

}
