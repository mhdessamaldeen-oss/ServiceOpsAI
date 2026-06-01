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
    /// <summary>
    /// SpecRepair phase diagnostics — one entry per phase that mutated the spec. Threaded
    /// through to the orchestrator's step recorder so the investigation page can show "what
    /// auto-fixes ran" alongside the LLM call. Empty when SpecRepair didn't run or made no
    /// changes.
    /// </summary>
    public IReadOnlyList<Pipeline.SpecRepair.SpecRepairDiagnostic> RepairDiagnostics { get; init; }
        = Array.Empty<Pipeline.SpecRepair.SpecRepairDiagnostic>();
}

internal sealed class SpecExtractor : ISpecExtractor
{
    private readonly ISchemaSemanticRetriever _retriever;
    private readonly IColumnSemanticRetriever _columnRetriever;
    private readonly IEntitySemanticRetriever _entityRetriever;
    private readonly ISchemaKnowledge _knowledge;
    private readonly ILlmClient _llm;
    private readonly IPastQuestionStore _pastQuestions;
    private readonly IVerifiedQueryMatcher _vqMatcher;
    private readonly ITemporalParser _temporalParser;
    private readonly IOptions<CopilotOptions> _options;
    private readonly IOptionsMonitor<FkRoleOptions> _fkRoleOptions;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly Pipeline.SpecRepair.ISpecRepair _specRepair;
    private readonly Pipeline.Prompts.IPromptShapeClassifier _promptShapeClassifier;
    private readonly ISemanticLayer _semanticLayer;
    private readonly SuperAdminCopilot.Grounding.IQuestionGrounder _grounder;
    private readonly ILogger<SpecExtractor> _logger;

    public SpecExtractor(
        ISchemaSemanticRetriever retriever,
        IColumnSemanticRetriever columnRetriever,
        IEntitySemanticRetriever entityRetriever,
        ISchemaKnowledge knowledge,
        ServiceOpsAI.Services.AI.Providers.Roles.IRoleBoundLlmClientFactory llmFactory,
        IPastQuestionStore pastQuestions,
        IVerifiedQueryMatcher vqMatcher,
        ITemporalParser temporalParser,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<FkRoleOptions> fkRoleOptions,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        Pipeline.SpecRepair.ISpecRepair specRepair,
        Pipeline.Prompts.IPromptShapeClassifier promptShapeClassifier,
        ISemanticLayer semanticLayer,
        SuperAdminCopilot.Grounding.IQuestionGrounder grounder,
        ILogger<SpecExtractor> logger)
    {
        _retriever = retriever;
        _columnRetriever = columnRetriever;
        _entityRetriever = entityRetriever;
        _knowledge = knowledge;
        // Use the QuerySpecComposer role binding so the SpecExtractor can be pointed at a
        // bigger / code-specialized model (e.g. qwen2.5-coder:7b) independently of the rest
        // of the pipeline. Binding lives in appsettings.json under Ai:RoleBindings.QuerySpecComposer.
        // Empty binding falls back to the Copilot workload's default model.
        _llm = llmFactory.For(ServiceOpsAI.Services.AI.Providers.Roles.AiRole.QuerySpecComposer);
        _pastQuestions = pastQuestions;
        _vqMatcher = vqMatcher;
        _temporalParser = temporalParser;
        _options = options;
        _fkRoleOptions = fkRoleOptions;
        _textCatalog = textCatalog;
        _specRepair = specRepair;
        _promptShapeClassifier = promptShapeClassifier;
        _semanticLayer = semanticLayer;
        _grounder = grounder;
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

        // Phase 7a — classify the question's prompt shape (COUNT/TOPN/JOIN/...). Today this
        // is observational only: we log the classification so trace analysis can correlate
        // shape with success rate. Phase 7c-full will use the shape to route to per-shape
        // example banks in the prompt assembler; until then the existing god-prompt path
        // remains unchanged.
        var promptShape = _promptShapeClassifier.Classify(question);
        _logger.LogInformation("[SpecExtractor] promptShape={Shape} for '{Question}'", promptShape, question);

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

            // Keyword-fallback: scan semantic-layer synonyms against the question text and add any
            // matching entity to the candidate list. The retriever embeds table name + description
            // + label/content columns, but NOT the entity's synonym list — so questions phrased
            // using a synonym ("meter readings" for MeterReadings, "outages" for Outages) often
            // fall below the retriever's similarity threshold. Keyword matching is exact; combined
            // with the retriever's fuzzy match, recall is dramatically higher. Cheap O(N synonyms).
            AddKeywordMatchedTables(question, tables);

            // Slice 2 (2026-05-30 close-out) — entity-level SEMANTIC match. Embeds the
            // domain description + synonyms from semantic-layer.json, so paraphrases that
            // miss the keyword scan AND the schema-shape retriever ("complaints" → Tickets,
            // Arabic "تذاكر" → Tickets) still surface the right root entity. Fail-open:
            // empty result on embedder failure → tables stay as-is.
            try
            {
                if (_entityRetriever.IsAvailable)
                {
                    var entityRetrieval = await _entityRetriever.RetrieveAsync(
                        question, topK: 5, minSimilarity: 0.45f, cancellationToken);
                    foreach (var em in entityRetrieval.Entities)
                    {
                        if (string.IsNullOrEmpty(em.Table)) continue;
                        if (IsHiddenFromRetriever(em.Table)) continue;
                        if (tables.Any(t => string.Equals(t.Name, em.Table, StringComparison.OrdinalIgnoreCase))) continue;
                        var schemaTable = _knowledge.AllTables.FirstOrDefault(t =>
                            string.Equals(t.Name, em.Table, StringComparison.OrdinalIgnoreCase));
                        if (schemaTable is null) continue;
                        tables.Add(schemaTable);
                        _logger.LogDebug("[SpecExtractor] entity-semantic added '{Table}' (score={Score:F2}).",
                            em.Table, em.Score);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SpecExtractor] entity semantic retrieval failed; continuing with existing candidates.");
            }

            if (tables.Count == 0)
            {
                // Retriever AND keyword fallback both returned nothing — surface ALL non-hidden
                // domain tables so the orchestrator's escape-valve recovery path can fire and let
                // the direct-SQL emitter take a fresh shot at the question.
                var domainTables = _knowledge.AllTables
                    .Where(t => !t.Flags.IsBridge && !t.Flags.IsLookup)
                    .Where(t => !IsHiddenFromRetriever(t.Name))
                    .Select(t => t.Name)
                    .ToList();
                return new SpecExtractionResult
                {
                    Error = domainTables.Count > 0
                        ? $"no candidate tables matched. Available: {string.Join(", ", domainTables.Take(8))}"
                        : "no candidate tables",
                    CandidateTables = domainTables,
                };
            }

            // Expand to include neighbor tables the candidates reference / are referenced by,
            // so the LLM has enough context to choose joins. Cap tunable via
            // CopilotOptions.SpecPromptMaxTables (default 6 — small enough for local models).
            tables = ExpandWithNeighbors(tables, maxTotal: _options.Value.SpecPromptMaxTables);

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
            // Top-K and minimum-similarity floor tunable via CopilotOptions.VqFewShotTopK /
            // VqFewShotMinSimilarity (defaults 3 / 0.65 — lower threshold than VerifiedQueryMinSimilarity
            // because these aren't run AS the answer, only shown as worked examples).
            IReadOnlyList<VerifiedMatch> vqExamples = Array.Empty<VerifiedMatch>();
            if (previousError is null)
            {
                try
                {
                    vqExamples = await _vqMatcher.FindTopAsync(
                        question,
                        topK: _options.Value.VqFewShotTopK,
                        minSimilarity: (float)_options.Value.VqFewShotMinSimilarity,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SpecExtractor] VQ few-shot lookup failed (non-fatal).");
                }
            }

            // Stage-1 grounding — resolve schema/value/temporal/natural-key/intent BEFORE the LLM.
            // The LLM is then told the answers as ground truth instead of guessing them. See
            // Areas/SuperAdminCopilot/Grounding/QuestionGrounder.cs and the principled redesign
            // notes in the project memory. Falls back to QuestionGroundingContext.Empty on
            // failure — SpecRepair phases remain as a backstop.
            SuperAdminCopilot.Grounding.QuestionGroundingContext grounding;
            try
            {
                grounding = await _grounder.GroundAsync(question, tables, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SpecExtractor] grounding failed; falling back to ungrounded prompt.");
                grounding = SuperAdminCopilot.Grounding.QuestionGroundingContext.Empty;
            }

            // Slice 1 — column-level semantic matching. Pre-compute the top-K columns whose
            // embedding cosine-matches the question, restricted to the candidate tables already
            // chosen by the schema retriever. The result is injected into the prompt as a
            // "Semantically relevant columns" block — gives the LLM explicit, ranked hints
            // instead of a wall of column names to guess from. Fail-open: empty result on any
            // embedder/retrieval failure → prompt assembly continues without the block.
            IReadOnlyList<ColumnMatch> columnHints = Array.Empty<ColumnMatch>();
            try
            {
                if (_columnRetriever.IsAvailable)
                {
                    var tableNames = tables.Select(t => t.Name).ToList();
                    var columnRetrieval = await _columnRetriever.RetrieveAsync(
                        question, tableNames, topK: 8, minSimilarity: 0.50f, cancellationToken);
                    columnHints = columnRetrieval.Columns;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SpecExtractor] column semantic retrieval failed; continuing without column hints.");
            }

            var prompt = BuildUserPrompt(question, tables, previousSpec, previousError, refinement, pastExamples, vqExamples, grounding, columnHints);

            using var hint = Abstractions.LlmCallStageHint.Use(refinement ? "SpecRefine" : "SpecExtractor");
            var raw = await _llm.GenerateJsonAsync(_textCatalog.CurrentValue.SpecExtractorSystemPrompt, prompt, cancellationToken);
            var spec = ParseSpec(raw, question, tables);
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

            // Never-refuse policy: when the LLM emits intent=clarification, promote to data_query
            // and let SpecRepair fill the missing fields (root inference, smart defaults). The
            // clarification text is preserved in the spec so the orchestrator can surface it as
            // a hint alongside the best-guess answer.
            if (!string.IsNullOrEmpty(spec.Intent)
                && !string.Equals(spec.Intent, "data_query", StringComparison.OrdinalIgnoreCase))
            {
                spec.Intent = "data_query";
            }

            NormalizeSpec(spec, question);
            // Consolidated LLM-output mutation pipeline. Owns column auto-qualification,
            // function-name normalization, root inference, etc. The compiler trusts the spec
            // that comes out of this. See Areas/SuperAdminCopilot/Pipeline/SpecRepair/README.md.
            var repairDiagnostics = _specRepair.Apply(spec, question, tables, promptShape.ToString());
            return new SpecExtractionResult
            {
                Spec = spec,
                CandidateTables = tables.Select(t => t.Name).ToList(),
                RawLlmOutput = raw,
                Prompt = prompt,
                RepairDiagnostics = repairDiagnostics,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SpecExtractor] extract failed for '{Q}'.", question);
            return new SpecExtractionResult { Error = ex.Message };
        }
    }

    // True when table name matches either the exact-hidden list or any wildcard pattern.
    private bool IsHiddenFromRetriever(string tableName)
    {
        var opts = _options.Value;
        if (opts.RetrieverHiddenTables.Any(n => string.Equals(n, tableName, StringComparison.OrdinalIgnoreCase)))
            return true;
        foreach (var pattern in opts.RetrieverHiddenTablePatterns)
            if (WildcardMatch(pattern, tableName)) return true;
        return false;
    }

    /// <summary>
    /// Scan semantic-layer entity synonyms against the question text. For each entity whose
    /// table is hidden-from-retriever-safe, exists in schema knowledge, and is NOT already in
    /// the candidate list, append it if any synonym (or the canonical Name) appears as a
    /// whole-word match in the question. Mutates <paramref name="tables"/> in place.
    /// </summary>
    private void AddKeywordMatchedTables(string question, List<InferredTable> tables)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        var entities = _semanticLayer.Config.Entities;
        if (entities is null || entities.Count == 0) return;

        // Lowercase + whitespace-padded question so whole-word boundary checks work without
        // a full regex (cheaper). " how many meter readings do we have " — substring search
        // for " meter reading " finds whole-word match without misfiring on "thermometer".
        var q = " " + question.ToLowerInvariant().Replace('\n', ' ').Replace('\r', ' ') + " ";

        // Strip punctuation that touches word boundaries (e.g. "outages," → "outages ").
        // INCLUDES Arabic-specific punctuation:
        //   ؟ U+061F Arabic question mark
        //   ، U+060C Arabic comma
        //   ؛ U+061B Arabic semicolon
        //   ! U+FE57 Arabic exclamation (presentation form)
        // Without these, "كم عدد أوامر العمل؟" keeps the ؟ attached to "العمل" and the
        // whole-word match for synonym " العمل " fails. This was the Arabic-resolution bug.
        var sb = new System.Text.StringBuilder(q.Length);
        foreach (var ch in q)
        {
            if (ch == ',' || ch == '.' || ch == '!' || ch == '?' || ch == ';' || ch == ':' || ch == '"' || ch == '\''
                || ch == '؟' || ch == '،' || ch == '؛' || ch == '﹗')
                sb.Append(' ');
            else sb.Append(ch);
        }
        q = sb.ToString();

        foreach (var entity in entities)
        {
            if (string.IsNullOrEmpty(entity.Table)) continue;
            if (IsHiddenFromRetriever(entity.Table)) continue;
            if (tables.Any(t => string.Equals(t.Name, entity.Table, StringComparison.OrdinalIgnoreCase))) continue;
            var schemaTable = _knowledge.AllTables.FirstOrDefault(t =>
                string.Equals(t.Name, entity.Table, StringComparison.OrdinalIgnoreCase));
            if (schemaTable is null) continue;

            // Build the term list: canonical Name + Synonyms. Lowercase + whole-word wrapped.
            var terms = new List<string>(1 + (entity.Synonyms?.Count ?? 0));
            if (!string.IsNullOrEmpty(entity.Name)) terms.Add(entity.Name);
            if (entity.Synonyms is not null) terms.AddRange(entity.Synonyms);

            foreach (var term in terms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                var needle = " " + term.ToLowerInvariant() + " ";
                if (q.Contains(needle, StringComparison.Ordinal))
                {
                    tables.Add(schemaTable);
                    _logger.LogDebug("[SpecExtractor] keyword-match added '{Table}' via synonym '{Term}'",
                        entity.Table, term);
                    break;
                }
            }
        }
    }

    private static bool WildcardMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // Translate * → .*  and anchor the regex; case-insensitive.
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
        IReadOnlyList<VerifiedMatch>? vqExamples = null,
        SuperAdminCopilot.Grounding.QuestionGroundingContext? grounding = null,
        IReadOnlyList<ColumnMatch>? columnHints = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Question: \"{question}\"");
        sb.AppendLine();

        // Stage-1 grounded context — these are pre-resolved facts the LLM MUST use as ground
        // truth. Putting them at the top of the prompt (before examples / rules / schema) makes
        // them the dominant signal. The LLM is told: don't guess these; use them verbatim.
        AppendGroundedContext(sb, grounding);
        AppendSemanticColumnHints(sb, columnHints);

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
        sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorAggregationGuidance);
        sb.AppendLine("- \"top N X\" or \"first N X\" → set limit: N. \"top N X by Y\" → ALSO orderBy Y desc.");
        sb.AppendLine("- \"X by <lookup-dimension>\" (status / category / priority / region / type / etc.) → filter on the LOOKUP table's label column (e.g. \"<LookupTable>.Name\" eq \"<value>\"), and groupBy the same column when aggregating.");
        sb.AppendLine("- \"X with no Y\" / \"X without any Y\" → joins:[{table:Y, kind:\"anti\"}].");
        sb.AppendLine("- ★ COMPARISON (\"X vs Y\" / \"compare X and Y\" / \"this X vs last X\" / \"YoY\" / \"MoM\" / \"WoW\" / \"today vs yesterday\" / \"2024 vs 2025\"): emit ONE SUM(CASE WHEN <per-bucket predicate> THEN 1 ELSE 0 END) aggregation PER BUCKET. Keep filters[] EMPTY — the per-bucket predicates live INSIDE the aggregation column expressions. NEVER duplicate COUNT(*) under different aliases with the same WHERE — that yields the same number twice.");
        sb.AppendLine("- ★ NO SUBQUERIES in aggregation columns or computed expressions. To compare against a lookup value (\"= 'Critical'\"), reference the JOINED lookup table directly (e.g. CASE WHEN TicketPriorities.Name = 'Critical' ...) and add the lookup table to joins[]. NEVER write \"(SELECT Id FROM ...)\" inside an aggregation column — the validator rejects it.");
        sb.AppendLine(BuildFkRolePromptRule());
        sb.AppendLine("- Reference unknown columns via computed expressions if derivable (e.g. \"age\" → DATEDIFF from CreatedAt).");
        sb.AppendLine("- Use ISO dates or \"today\"/\"last_7_days\"/\"last_30_days\"/\"this_month\"/\"last_month\" for date filters.");
        sb.AppendLine();
        sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorWorkedExamples);
        sb.AppendLine();
        // Reminders block — text comes from CopilotTextCatalog so operators can tune wording
        // per deployment. The relative-date keyword line is data-driven and stays inline so it
        // always reflects the live RelativeDateKeywords config.
        sb.AppendLine(_textCatalog.CurrentValue.SpecExtractorReminders);
        // Iteration-loop extra guidance — populated from copilot-text.json so prompt tweaks
        // during the quality loop don't need a code change or restart. Empty by default.
        var extra = _textCatalog.CurrentValue.SpecExtractorExtraGuidance;
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine(extra);
        }
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

    /// <summary>
    /// Emit the Stage-1 grounded context as a "Resolved context" block at the top of the prompt.
    /// The LLM is told: these are pre-resolved facts. Use them verbatim. Don't guess values, don't
    /// substitute different columns, don't omit them from the spec. Skips emitting anything when
    /// the context is empty (legacy ungrounded flow). All sections are conditional — only included
    /// when populated, to keep the prompt short for simple questions.
    /// </summary>
    private static void AppendGroundedContext(StringBuilder sb, SuperAdminCopilot.Grounding.QuestionGroundingContext? grounding)
    {
        if (grounding is null) return;
        var hasAny = grounding.LinkedValues.Count > 0
                     || grounding.LinkedNaturalKeys.Count > 0
                     || grounding.LinkedTemporal.Count > 0
                     || grounding.IsAllTimeIntent
                     || grounding.IsDistinctCountIntent
                     || !string.IsNullOrEmpty(grounding.DateRoleHint)
                     || !string.IsNullOrEmpty(grounding.PromptShape);
        if (!hasAny) return;

        sb.AppendLine("Resolved context (pre-grounded facts — USE THESE VERBATIM, do NOT guess):");

        if (!string.IsNullOrEmpty(grounding.PromptShape))
            sb.Append("- Shape: ").AppendLine(grounding.PromptShape);

        if (grounding.LinkedValues.Count > 0)
        {
            sb.AppendLine("- Required filter values (every one must appear in the WHERE clause):");
            foreach (var v in grounding.LinkedValues)
                sb.Append("    • ").Append(v.Table).Append('.').Append(v.Column).Append(" = '").Append(v.Value).AppendLine("'");
        }

        if (grounding.LinkedNaturalKeys.Count > 0)
        {
            sb.AppendLine("- Natural-key references (root MUST be the table below; filter MUST be the column):");
            foreach (var k in grounding.LinkedNaturalKeys)
                sb.Append("    • ").Append(k.Table).Append('.').Append(k.Column).Append(" = '").Append(k.Value).AppendLine("' (entity: ").Append(k.Entity).AppendLine(")");
        }

        if (grounding.LinkedTemporal.Count == 1)
        {
            var t = grounding.LinkedTemporal[0];
            sb.Append("- Temporal slot: ");
            if (t.EndToken is null)
                sb.Append("date column ").Append(t.Op).Append(' ').AppendLine(t.StartToken);
            else
                sb.Append("date column in [").Append(t.StartToken).Append(", ").Append(t.EndToken).AppendLine(")");
            sb.AppendLine("  Inject these as filters on the root entity's default date column.");
        }
        else if (grounding.LinkedTemporal.Count > 1)
        {
            sb.AppendLine("- Multi-period comparison (emit as periodComparisons array, NOT a single filter):");
            foreach (var t in grounding.LinkedTemporal)
            {
                sb.Append("    • ").Append(t.Label).Append(" → ");
                if (t.EndToken is null) sb.Append(t.Op).AppendLine(t.StartToken);
                else sb.Append('[').Append(t.StartToken).Append(", ").Append(t.EndToken).AppendLine(")");
            }
        }

        if (grounding.IsAllTimeIntent)
            sb.AppendLine("- All-time intent: DO NOT add any default date filter; the user wants the full history.");

        if (grounding.IsDistinctCountIntent)
            sb.AppendLine("- Distinct-count intent: emit aggregation as COUNT with distinct=true on the natural-key column; do NOT use GROUP BY for this.");

        if (grounding.DerivedMetricHints.Count > 0)
        {
            sb.AppendLine("- Derived-metric column hints (the LLM must AGGREGATE OVER these expressions, NOT raw columns like AffectedUsersCount):");
            foreach (var m in grounding.DerivedMetricHints)
                sb.Append("    • ").Append(m.MetricKeyword).Append(" → use ").Append(m.PreferredFunction).Append('(').Append(m.Expression).AppendLine(") as the aggregation");
        }

        if (!string.IsNullOrEmpty(grounding.DateRoleHint))
            sb.Append("- Date-role hint: filter / order by the date column with role '").Append(grounding.DateRoleHint).AppendLine("' (from semantic-layer dateRoles).");

        sb.AppendLine();
    }

    /// <summary>
    /// Slice 1 — embedding-driven column hints. Emit a "Semantically relevant columns" block
    /// listing the top-K (Table.Column, description) pairs whose embedding cosine-matches the
    /// user's question. Gives the LLM a ranked, semantically-anchored set of columns instead
    /// of having to guess from column NAMES alone. The LLM still chooses; this is a hint, not
    /// a hard constraint — the 22 typed repair rules remain the deterministic safety net.
    /// </summary>
    private static void AppendSemanticColumnHints(StringBuilder sb, IReadOnlyList<ColumnMatch>? columnHints)
    {
        if (columnHints is null || columnHints.Count == 0) return;
        sb.AppendLine("Semantically relevant columns for this question (use these as your primary candidates; pick the best fit):");
        foreach (var c in columnHints)
        {
            sb.Append("  • ").Append(c.TableDotColumn);
            if (!string.IsNullOrEmpty(c.SqlType)) sb.Append(" (").Append(c.SqlType).Append(')');
            if (!string.IsNullOrEmpty(c.Description)) sb.Append(" — ").Append(c.Description);
            sb.AppendLine();
        }
        sb.AppendLine();
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
            // by SchemaInferenceGenerator. Department-agnostic: works for any *By/*To FK on any table.
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

    private QuerySpec? ParseSpec(string raw, string? question = null, IReadOnlyList<InferredTable>? candidateTables = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJsonObject(raw) ?? RepairTruncatedJson(raw);
        if (json is null) return null;
        try
        {
            var normalized = NormalizeSpecShape(json, question, candidateTables);
            return JsonSerializer.Deserialize<QuerySpec>(normalized, CopilotJson.Lenient);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SpecExtractor] JSON parse failed. Raw: {Raw}", raw);
            return null;
        }
    }

    // Shape-tolerant normalizer for common LLM output drifts that confuse the strict QuerySpec deserializer.
    // Covers (a) snake_case keys (group_by → groupBy), (b) Select/GroupBy items as {table, column}-objects flattened to "Table.Column" strings, (c) Having as a single object wrapped into an array.
    private static string NormalizeSpecShape(string json, string? question = null, IReadOnlyList<InferredTable>? candidateTables = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return json;

        var output = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
        {
            var key = NormalizeKey(prop.Name);
            // Special: when LLM emits `"limit": {"offset":20, "count":20}`, split into the two QuerySpec ints.
            if (string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (prop.Value.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number)
                    output["limit"] = cnt.GetInt32();
                else if (prop.Value.TryGetProperty("rows", out var rws) && rws.ValueKind == JsonValueKind.Number)
                    output["limit"] = rws.GetInt32();
                if (prop.Value.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number)
                    output["offset"] = off.GetInt32();
                else if (prop.Value.TryGetProperty("skip", out var skp) && skp.ValueKind == JsonValueKind.Number)
                    output["offset"] = skp.GetInt32();
                continue;
            }
            output[key] = NormalizeValue(key, prop.Value);
        }
        // Post-pass: hoist any inline-aggregate strings ("SUM(x) AS alias") that live in
        // `select` into the `aggregations` list, and strip trailing " AS alias" from bare
        // column refs so the compiler's TryFormatColumn doesn't reject them. Without this
        // hoist, the LLM's `select:["Customers.FullNameEn AS customer","SUM(Bills.TotalAmount) AS total_billed"]`
        // pattern (common when the LLM mimics SQL syntax instead of the QuerySpec form)
        // leaves Aggregations.Count == 0, so SpecEnricher's "is aggregate?" guards all
        // miss and add filter / orderBy columns to SELECT — producing SQL with a column
        // not in GROUP BY that SQL Server rejects.
        HoistInlineAggregatesFromSelect(output);
        // Prefer a question-text match for root inference — if the user said "tickets" then
        // root should be Tickets even when the LLM's column refs are all on the joined lookup.
        EnsureRootFromQuestion(output, question, candidateTables);
        // Infer missing root from the first qualified column reference we can find.
        // The LLM sometimes omits root entirely when the spec is dominated by aggregations
        // ("compare open vs closed tickets" → aggregations referencing TicketStatuses but
        // no root field). Without this, the deserialized spec has Root="" and the compiler
        // refuses with "unknown root table". Picking a referenced table is usually right;
        // when the LLM picked a lookup-table column (e.g. TicketStatuses), the join graph
        // resolver will pull in the actual root anyway via FK walk.
        EnsureRootFromReferencedColumns(output);
        return JsonSerializer.Serialize(output);
    }

    private static void EnsureRootFromQuestion(System.Collections.Generic.Dictionary<string, object?> output, string? question, IReadOnlyList<InferredTable>? candidateTables)
    {
        if (output.TryGetValue("root", out var rootObj) && rootObj is string rs && !string.IsNullOrWhiteSpace(rs))
            return;
        if (string.IsNullOrWhiteSpace(question) || candidateTables is null || candidateTables.Count == 0) return;
        // Strip annotation lines (the entity-resolution + requested-columns hints appended after
        // "\n-- "). Those mention table/lookup names that aren't part of the user's actual question
        // and would bias the inference toward lookup tables (e.g. "TicketStatuses" in a resolved hint).
        var qClean = question!;
        var dashIdx = qClean.IndexOf("\n--", StringComparison.Ordinal);
        if (dashIdx >= 0) qClean = qClean.Substring(0, dashIdx);
        var qLower = qClean.ToLowerInvariant();
        // Score each candidate by lowercase singular/plural match against the question.
        // Pick the highest-scoring table whose name appears in the question.
        string? best = null;
        int bestScore = 0;
        foreach (var t in candidateTables)
        {
            var name = t.Name;
            if (string.IsNullOrEmpty(name)) continue;
            var lower = name.ToLowerInvariant();
            int score = 0;
            if (qLower.Contains(lower)) score = lower.Length + 1;
            else if (lower.EndsWith("s") && qLower.Contains(lower.Substring(0, lower.Length - 1)))
                score = lower.Length; // singular form match
            if (score > bestScore) { bestScore = score; best = name; }
        }
        if (best is not null) output["root"] = best;
    }

    private static void EnsureRootFromReferencedColumns(System.Collections.Generic.Dictionary<string, object?> output)
    {
        if (output.TryGetValue("root", out var rootObj) && rootObj is string rs && !string.IsNullOrWhiteSpace(rs))
            return;
        string? inferred = null;
        // Aggregations first (CASE expressions name the dimension table) → select → groupBy → filters.
        if (output.TryGetValue("aggregations", out var aggObj) && aggObj is System.Collections.Generic.IEnumerable<object?> aggList)
        {
            foreach (var a in aggList)
            {
                if (a is System.Collections.Generic.IDictionary<string, object?> ad && ad.TryGetValue("column", out var c) && c is string cs)
                {
                    inferred ??= ExtractTableHint(cs);
                    if (inferred is not null) break;
                }
            }
        }
        foreach (var key in new[] { "select", "groupBy", "filters" })
        {
            if (inferred is not null) break;
            if (!output.TryGetValue(key, out var v) || v is not System.Collections.Generic.IEnumerable<object?> list) continue;
            foreach (var item in list)
            {
                string? colRef = item switch
                {
                    string s => s,
                    System.Collections.Generic.IDictionary<string, object?> d when d.TryGetValue("column", out var col) && col is string cs => cs,
                    _ => null,
                };
                inferred ??= ExtractTableHint(colRef);
                if (inferred is not null) break;
            }
        }
        if (inferred is not null) output["root"] = inferred;
    }

    // Pull a table hint from a "Table.Column" string or a CASE WHEN expression. For an
    // expression, prefer the first "Word.Word" token we can find.
    private static string? ExtractTableHint(string? colRef)
    {
        if (string.IsNullOrWhiteSpace(colRef)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(colRef, @"\b([A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z_]");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly System.Text.RegularExpressions.Regex InlineAggregateRegex =
        new(@"^\s*(COUNT|SUM|AVG|MIN|MAX|COUNT_BIG)\s*\(\s*(DISTINCT\s+)?(.+?)\s*\)(?:\s+AS\s+\[?([A-Za-z_][A-Za-z0-9_]*)\]?)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TrailingAliasRegex =
        new(@"^\s*(.+?)\s+AS\s+\[?([A-Za-z_][A-Za-z0-9_]*)\]?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void HoistInlineAggregatesFromSelect(System.Collections.Generic.Dictionary<string, object?> output)
    {
        if (!output.TryGetValue("select", out var selObj) || selObj is not System.Collections.Generic.IEnumerable<object?> selList) return;
        var keptSelect = new System.Collections.Generic.List<object?>();
        var newAggregations = new System.Collections.Generic.List<object?>();
        foreach (var item in selList)
        {
            if (item is not string s || string.IsNullOrWhiteSpace(s)) { keptSelect.Add(item); continue; }
            var m = InlineAggregateRegex.Match(s);
            if (m.Success)
            {
                var fn = m.Groups[1].Value.ToUpperInvariant();
                var distinct = m.Groups[2].Success;
                var col = m.Groups[3].Value.Trim();
                var alias = m.Groups[4].Success ? m.Groups[4].Value : null;
                var agg = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["function"] = fn,
                    ["column"] = col,
                };
                if (!string.IsNullOrEmpty(alias)) agg["alias"] = alias;
                if (distinct) agg["distinct"] = true;
                newAggregations.Add(agg);
                continue;
            }
            // Strip a trailing " AS alias" on a bare-column ref so TryFormatColumn accepts it.
            var am = TrailingAliasRegex.Match(s);
            if (am.Success && !am.Groups[1].Value.Contains('('))
            {
                keptSelect.Add(am.Groups[1].Value.Trim());
                continue;
            }
            keptSelect.Add(s);
        }
        if (newAggregations.Count == 0) return;
        output["select"] = keptSelect;
        // Merge into any existing aggregations rather than overwrite.
        if (output.TryGetValue("aggregations", out var existing) && existing is System.Collections.Generic.IEnumerable<object?> existingList)
        {
            var merged = new System.Collections.Generic.List<object?>(existingList);
            merged.AddRange(newAggregations);
            output["aggregations"] = merged;
        }
        else
        {
            output["aggregations"] = newAggregations;
        }
    }

    // snake_case → camelCase for known top-level keys; other keys pass through. Also: a few
    // common alternate names the LLM occasionally emits (where→filters, group→groupBy, order→orderBy).
    private static string NormalizeKey(string k) => k.ToLowerInvariant() switch
    {
        "group_by" => "groupBy",
        "order_by" => "orderBy",
        "clarification_question" => "clarificationQuestion",
        "period_comparisons" => "periodComparisons",
        "where" => "filters",
        "group" => "groupBy",
        "order" => "orderBy",
        _ => k,
    };

    private static object? NormalizeValue(string key, JsonElement el)
    {
        switch (key.ToLowerInvariant())
        {
            case "select":
            case "groupby":
                // Accept either ["Table.Col", ...] or [{table:..,column:..}, ...] or even
                // the object-form {"expr": "alias", ...} that some retries emit — last
                // form gets flattened to its keys (compiler treats expressions as columns).
                if (el.ValueKind == JsonValueKind.Object)
                    return el.EnumerateObject().Select(p => (object?)p.Name).ToList();
                if (el.ValueKind != JsonValueKind.Array) return ExtractRaw(el);
                return el.EnumerateArray().Select(e =>
                {
                    if (e.ValueKind == JsonValueKind.String) return (object?)e.GetString();
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        var t = TryGetString(e, "table") ?? TryGetString(e, "Table");
                        var c = TryGetString(e, "column") ?? TryGetString(e, "Column");
                        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(c)) return $"{t}.{c}";
                        var expr = TryGetString(e, "expression") ?? TryGetString(e, "Expression");
                        return expr ?? (object?)ExtractRaw(e);
                    }
                    return ExtractRaw(e);
                }).ToList();
            case "aggregations":
                // Normalize each entry's function name: "COUNT(DISTINCT)", "COUNT_DISTINCT",
                // "COUNTDISTINCT", "COUNT DISTINCT" all become function="COUNT" + distinct=true.
                // Without this, the compiler's strict NormalizeAggFn returns null and the
                // aggregation is silently dropped, leaving a bare SELECT that fails the
                // SqlIntentGuard's "aggregations + select but no GROUP BY" check.
                if (el.ValueKind != JsonValueKind.Array) return ExtractRaw(el);
                return el.EnumerateArray().Select(NormalizeAggregationEntry).ToList();
            case "filters":
                // Accept either [{...}, ...] or {col: val, ...} (object-form where-clause).
                if (el.ValueKind == JsonValueKind.Object)
                {
                    return el.EnumerateObject().Select(p =>
                    {
                        var f = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        f["column"] = p.Name;
                        f["op"] = "eq";
                        f["value"] = ExtractRaw(p.Value);
                        return (object?)f;
                    }).ToList();
                }
                if (el.ValueKind != JsonValueKind.Array) return ExtractRaw(el);
                // Each filter entry: normalize {operator: "="} → {op: "eq"} and similar
                // SQL-operator-in-the-op-field drift so the compiler's op lookup hits.
                return el.EnumerateArray().Select(NormalizeFilterEntry).ToList();
            case "having":
                // Accept either [{...}, ...] or a single {...} → wrap to array.
                if (el.ValueKind == JsonValueKind.Object) return new[] { ExtractRaw(el) };
                return ExtractRaw(el);
            default:
                return ExtractRaw(el);
        }
    }

    // Normalize a single filter entry: accept {operator: "="} as a synonym for {op: "eq"},
    // and translate raw SQL comparison operators ("=", ">", "<", ">=", "<=", "!=", "<>")
    // to the QuerySpec's symbolic ops. Universal: covers the LLM's tendency to emit SQL-
    // shaped filters when the prompt is heavy on SQL examples.
    private static object? NormalizeFilterEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return ExtractRaw(el);
        var dict = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
        {
            var name = p.Name.ToLowerInvariant();
            if (name == "operator")
                dict["op"] = ExtractRaw(p.Value);
            else
                dict[p.Name] = ExtractRaw(p.Value);
        }
        if (dict.TryGetValue("op", out var opObj) && opObj is string op)
        {
            var mapped = op.Trim().ToLowerInvariant() switch
            {
                "=" or "==" => "eq",
                "!=" or "<>" => "neq",
                ">" => "gt",
                ">=" => "gte",
                "<" => "lt",
                "<=" => "lte",
                _ => op,
            };
            dict["op"] = mapped;
        }
        return dict;
    }

    // Normalize a single aggregation entry: extract distinct from variants like
    // "COUNT(DISTINCT)" / "COUNT_DISTINCT" / "COUNTDISTINCT" / "COUNT DISTINCT" and
    // peel "DISTINCT col" out of the column field too. Also handles distinct flag
    // emitted as "distinct":true vs "isDistinct":true.
    private static object? NormalizeAggregationEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return ExtractRaw(el);
        var dict = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        bool distinct = false;
        foreach (var p in el.EnumerateObject())
        {
            var name = p.Name.ToLowerInvariant();
            if (name is "distinct" or "isdistinct")
            {
                if (p.Value.ValueKind == JsonValueKind.True) distinct = true;
                continue;
            }
            dict[p.Name] = ExtractRaw(p.Value);
        }
        if (dict.TryGetValue("function", out var fnObj) && fnObj is string fn && !string.IsNullOrWhiteSpace(fn))
        {
            var upper = fn.Trim().ToUpperInvariant();
            if (upper.Contains("DISTINCT"))
            {
                distinct = true;
                upper = upper.Replace("DISTINCT", "")
                             .Replace("(", "").Replace(")", "")
                             .Replace("_", "").Replace(" ", "");
            }
            dict["function"] = upper;
        }
        if (dict.TryGetValue("column", out var colObj) && colObj is string col && !string.IsNullOrWhiteSpace(col))
        {
            var trimmed = col.Trim();
            if (trimmed.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase))
            {
                distinct = true;
                dict["column"] = trimmed.Substring("DISTINCT ".Length).Trim();
            }
        }
        if (distinct) dict["distinct"] = true;
        return dict;
    }

    private static string? TryGetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Re-serialize an arbitrary JsonElement so it round-trips through the outer Dictionary cleanly.
    private static object? ExtractRaw(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object>(el.GetRawText()),
        };

    // Strict pass: pluck the first complete {...} block (string-aware brace balance). Returns null when EOF reached before the root object closed — caller falls back to RepairTruncatedJson.
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

    // Repair pass for LLM output that ran out of tokens. Tracks the open structure (string, key-after-colon, object/array depth) and synthesizes a minimal valid tail: close any open string, drop a trailing comma/colon, append a null/empty-array placeholder, then emit balanced } and ] characters. The result may have less data than the LLM intended, but it deserializes and lets the downstream retry-with-error path produce a useful refinement instead of failing with "unparseable JSON".
    private static string? RepairTruncatedJson(string raw)
    {
        var s = raw.AsSpan();
        int start = -1, depth = 0;
        var openStack = new System.Collections.Generic.Stack<char>();
        bool inString = false, escape = false;
        bool sawColonAwaitingValue = false;
        int lastSignificantIdx = -1;
        char lastSignificantCh = '\0';
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
            if (ch == '"') { inString = true; sawColonAwaitingValue = false; continue; }
            if (char.IsWhiteSpace(ch)) continue;
            lastSignificantIdx = i; lastSignificantCh = ch;
            if (ch == '{')
            {
                if (depth == 0) start = i;
                openStack.Push('{'); depth++; sawColonAwaitingValue = false;
            }
            else if (ch == '[')
            {
                if (depth == 0) start = i;
                openStack.Push('['); depth++; sawColonAwaitingValue = false;
            }
            else if (ch == '}' || ch == ']')
            {
                if (openStack.Count > 0) openStack.Pop();
                depth--;
                sawColonAwaitingValue = false;
            }
            else if (ch == ':') sawColonAwaitingValue = true;
            else if (ch == ',') sawColonAwaitingValue = false;
        }

        if (start < 0 || openStack.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append(raw.AsSpan(start, lastSignificantIdx - start + 1));
        // Close an unterminated string literal.
        if (inString) sb.Append('"');
        // Trailing comma or colon at end → add an empty placeholder and remove trailing comma.
        if (lastSignificantCh == ',') { sb.Length--; }
        else if (lastSignificantCh == ':' || sawColonAwaitingValue) sb.Append("null");
        // Balance the stack from top down.
        while (openStack.Count > 0)
        {
            var open = openStack.Pop();
            sb.Append(open == '{' ? '}' : ']');
        }
        return sb.ToString();
    }

}
