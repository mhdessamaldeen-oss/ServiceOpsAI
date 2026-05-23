namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuzzierSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;
using SuperAdminCopilot.Tools;

/// <summary>
/// Pre-planner short-circuit for "external tool" questions — weather, currency, country
/// profile, etc. The host has a <c>CopilotToolDefinitions</c> table admins use to register
/// HTTP endpoints with keyword hints. This stage:
///
/// <list type="number">
///   <item>Scores every enabled tool against the user's question (stopword-filtered token overlap
///   + keyword + title-contains + test-prompt similarity). Confidence-thresholded.</item>
///   <item>When the top hit is unambiguous (high score + clear gap to second), dispatches it.</item>
///   <item>When ambiguous (low gap), asks the LLM to pick among the top 4 candidates.</item>
///   <item>Extracts parameters from the question text against the tool's <c>{name}</c>
///   placeholders. When a required parameter is missing, returns a clarification question
///   instead of dispatching.</item>
///   <item>Substitutes the resolved params into the URL, GETs the endpoint, projects the JSON
///   payload into a tabular result. SubSteps capture URL + status + bytes for the trace.</item>
/// </list>
///
/// <para><b>F3 upgrade</b>: legacy port of <c>CopilotToolIntentResolver</c>. The original new
/// copilot only had keyword-only matching with no LLM fallback or param extraction.</para>
///
/// <para>Returns null when no tool is enabled OR no keyword/LLM match cleared the threshold —
/// caller falls through to the next stage.</para>
/// </summary>
public interface IToolHandler
{
    Task<ToolHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default);
}

public sealed record ToolHandlerResult(
    string Sql,                                  // synthetic "-- Tool: <key>" comment for the trace
    ExecutionResult Result,                      // tabular projection of the API payload (or one error row)
    string ToolKey,
    string ToolTitle,
    string? ClarificationQuestion = null,        // F3.c — set when params are missing
    IReadOnlyList<PipelineStep>? SubSteps = null); // F-Trace — substep capture (resolution / dispatch / formatter)

internal sealed class ToolHandler : IToolHandler
{
    private const int HighConfidenceScoreThreshold = 8;
    private const int ScoreGapThreshold = 3;
    /// <summary>
    /// Minimum score a candidate must reach before we even consider an LLM-fallback resolution.
    /// Below this we treat the question as "not a tool question" and fall through. Without this
    /// gate, two tools each with a single weak keyword overlap would burn an LLM call to pick
    /// between them — and then probably get refused by the planner anyway.
    /// </summary>
    private const int LlmFallbackMinTopScore = 4;

    /// <summary>
    /// Score gate when the question shows DB-shape markers (ticket/user/role/etc). External tools
    /// are designed for general-knowledge questions (weather, FX, holidays) — they have no
    /// business answering "how many tickets are open". So when DB markers are present we require
    /// a substantially stronger tool match before considering dispatch. Below this gate, even an
    /// LLM-resolved tool is suppressed: the planner is the right path.
    /// </summary>
    private const int DbShapedQuestionToolGate = 12;

    private readonly IToolRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILlmClient _llm;
    private readonly Pipeline.IRetryBudget _budget;
    private readonly ISemanticLayer _semanticLayer;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotTextCatalog> _textMonitor;
    private readonly ILogger<ToolHandler> _logger;

    /// <summary>
    /// Dynamically-built regex of tokens that indicate the user is asking about application
    /// data. When the question matches any of these, we raise the bar for tool dispatch (see
    /// <see cref="DbShapedQuestionToolGate"/>). This catches the "ticket counts accidentally
    /// route to weather tool" failure mode.
    /// <para>Composed from two sources:
    ///   1. <see cref="CopilotTextCatalog.DbShapeMarkers"/> — admin-editable list of generic
    ///      SQL verbs ("count", "how many", "show", …) and host-specific keywords.
    ///   2. Every <see cref="EntityDefinition.Synonyms"/> in the semantic layer — automatic
    ///      coverage of the configured domain vocabulary without manual upkeep.
    /// Recomputed lazily on first use; restart picks up catalog edits. Live hot-reload of the
    /// catalog updates the stopword list (which IS re-read per call) but not these markers —
    /// markers change rarely so cold-start cost is acceptable.</para>
    /// </summary>
    private readonly Lazy<Regex> _dbShapeMarkers;

    public ToolHandler(
        IToolRegistry registry,
        IHttpClientFactory httpFactory,
        ILlmClient llm,
        Pipeline.IRetryBudget budget,
        ISemanticLayer semanticLayer,
        Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotTextCatalog> textMonitor,
        ILogger<ToolHandler> logger)
    {
        _registry = registry;
        _httpFactory = httpFactory;
        _llm = llm;
        _budget = budget;
        _semanticLayer = semanticLayer;
        _textMonitor = textMonitor;
        _logger = logger;
        _dbShapeMarkers = new Lazy<Regex>(BuildDbShapeMarkers);
    }

    private Regex BuildDbShapeMarkers()
    {
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _textMonitor.CurrentValue.DbShapeMarkers ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(m)) markers.Add(m.Trim());
        foreach (var e in _semanticLayer.Config.Entities)
            foreach (var s in e.Synonyms)
                if (!string.IsNullOrWhiteSpace(s)) markers.Add(s.Trim());

        if (markers.Count == 0)
        {
            // Empty catalog AND empty semantic layer — match nothing, let the resolver use the
            // default thresholds without raising the gate.
            return new Regex(@"(?!)", RegexOptions.Compiled);
        }

        var alt = string.Join("|", markers.OrderByDescending(s => s.Length).Select(Regex.Escape));
        return new Regex($@"\b(?:{alt})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Stopwords sourced from <see cref="Configuration.CopilotTextCatalog.ToolRoutingStopwords"/>
    /// so admins can override or extend (multi-language) without recompile. Reads
    /// <c>.CurrentValue</c> per call to honor hot-reload.</summary>
    private HashSet<string> Stopwords =>
        new(_textMonitor.CurrentValue.ToolRoutingStopwords ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

    public async Task<ToolHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (!_registry.IsAvailable) return null;

        var tools = await _registry.GetEnabledAsync(cancellationToken);
        if (tools.Count == 0) return null;

        var subSteps = new List<PipelineStep>();
        var resolveStart = DateTime.UtcNow;
        var (matchedTool, resolveDetail) = await ResolveToolAsync(question, tools, cancellationToken);
        subSteps.Add(new PipelineStep(
            "Tool resolution", matchedTool is null ? StageNames.StatusSkipped : StageNames.StatusOk,
            ElapsedMs: (long)(DateTime.UtcNow - resolveStart).TotalMilliseconds, StartedAt: resolveStart,
            Detail: resolveDetail, Kind: "function-call"));

        if (matchedTool is null) return null;
        if (string.IsNullOrWhiteSpace(matchedTool.EndpointUrl))
        {
            _logger.LogDebug("[ToolHandler] tool '{Key}' matched but EndpointUrl is empty — skipping.", matchedTool.ToolKey);
            return null;
        }

        // F3.c — Parameter extraction. Extract {placeholder} names from the URL, try to fill
        // them from the question text, return a clarification when required params are missing.
        var paramExtraction = ExtractParameters(question, matchedTool);
        if (paramExtraction.MissingRequired.Count > 0)
        {
            var clarification = BuildClarificationQuestion(matchedTool, paramExtraction.MissingRequired);
            subSteps.Add(new PipelineStep(
                "Parameter extraction", StageNames.StatusFailed, ElapsedMs: 0, StartedAt: DateTime.UtcNow,
                Detail: $"missing required: {string.Join(", ", paramExtraction.MissingRequired)}"));
            return new ToolHandlerResult(
                Sql: $"-- Tool '{matchedTool.ToolKey}' needs more info — clarification requested",
                Result: new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero),
                ToolKey: matchedTool.ToolKey,
                ToolTitle: matchedTool.Title,
                ClarificationQuestion: clarification,
                SubSteps: subSteps);
        }
        subSteps.Add(new PipelineStep(
            "Parameter extraction", StageNames.StatusOk, ElapsedMs: 0, StartedAt: DateTime.UtcNow,
            Detail: $"resolved {paramExtraction.Resolved.Count} parameter(s)"));

        var url = SubstituteParameters(matchedTool.EndpointUrl, paramExtraction.Resolved, question);
        var dispatchStart = DateTime.UtcNow;
        try
        {
            using var client = _httpFactory.CreateClient();
            // 60s — public APIs (Frankfurter / REST Countries / Open-Meteo / Nager.Date) routinely
            // take 10-30s to respond on free tiers, and the previous 15s budget killed legitimate
            // dispatches mid-flight. Bumped 2026-05-20 after observing R6-TOOL-holiday-001 fail
            // here. 60s is still tight enough to fail-fast on dead APIs.
            client.Timeout = TimeSpan.FromSeconds(60);
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            // F-Trace — typed function-call payload so the JS renderer shows Input (URL) and
            // Output (payload) panels with byte counts, not just a single raw text blob.
            var dispatchPayload = JsonSerializer.Serialize(new
            {
                kind = "function-call",
                function = matchedTool.ToolKey,
                description = $"HTTP GET {matchedTool.Title}",
                input = url,
                output = Truncate(payload, 1500),
                inputLength = url.Length,
                outputLength = payload.Length,
            });
            subSteps.Add(new PipelineStep(
                "HTTP dispatch", StageNames.StatusOk,
                ElapsedMs: (long)(DateTime.UtcNow - dispatchStart).TotalMilliseconds,
                StartedAt: dispatchStart,
                Detail: $"GET {Truncate(url, 120)} → {(int)response.StatusCode}, {payload.Length} bytes",
                TechnicalData: dispatchPayload,
                Kind: "tool-dispatch"));

            // F3.d — Try typed formatters first, then generic JSON projection.
            var formatStart = DateTime.UtcNow;
            var formatted = TryFormatPayload(matchedTool, payload, out var formatterName);
            var rows = formatted ?? ProjectJsonToRows(payload);
            subSteps.Add(new PipelineStep(
                "Result formatter", StageNames.StatusOk,
                ElapsedMs: (long)(DateTime.UtcNow - formatStart).TotalMilliseconds,
                StartedAt: formatStart,
                Detail: $"{formatterName}: {rows.Count} row(s)"));

            var result = new ExecutionResult(rows, rows.Count, TimeSpan.Zero);
            return new ToolHandlerResult(
                Sql: $"-- Tool dispatched: {matchedTool.ToolKey} ({matchedTool.Title}) → {url}",
                Result: result,
                ToolKey: matchedTool.ToolKey,
                ToolTitle: matchedTool.Title,
                SubSteps: subSteps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ToolHandler] dispatch failed for tool '{Key}'.", matchedTool.ToolKey);
            subSteps.Add(new PipelineStep(
                "HTTP dispatch", StageNames.StatusFailed,
                ElapsedMs: (long)(DateTime.UtcNow - dispatchStart).TotalMilliseconds,
                StartedAt: dispatchStart,
                Detail: $"failed: {ex.Message}"));
            var errorRow = new Dictionary<string, object?>
            {
                ["Tool"] = matchedTool.Title, ["Status"] = "Failed", ["Error"] = ex.Message,
            };
            var result = new ExecutionResult(new[] { (IReadOnlyDictionary<string, object?>)errorRow }, 1, TimeSpan.Zero, Error: ex.Message);
            return new ToolHandlerResult(
                Sql: $"-- Tool dispatched: {matchedTool.ToolKey} (failed: {ex.Message})",
                Result: result,
                ToolKey: matchedTool.ToolKey,
                ToolTitle: matchedTool.Title,
                SubSteps: subSteps);
        }
    }

    /// <summary>
    /// F3.a + F3.b — Score every enabled tool against the question. Pick the top hit when
    /// confidence is high enough; ask the LLM to pick when it's ambiguous.
    /// <para><b>Budget discipline</b>: the LLM-fallback path is the only branch that consumes
    /// a budget slot. Single-candidate, no-candidate, and high-confidence paths all return
    /// immediately without burning the budget, so a "this isn't a tool question" verdict is
    /// always free.</para>
    /// </summary>
    private async Task<(ToolDefinition? Tool, string Detail)> ResolveToolAsync(
        string question, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        var deterministic = ResolveDirectRegistryMatch(question, tools);
        if (deterministic is not null)
            return (deterministic, $"direct registry match: {deterministic.ToolKey}");

        var candidates = ScoreCandidates(question, tools)
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ToList();

        // DB-shape detection moved above the zero-candidates branch so we can decide whether to
        // give the LLM a shot when keyword scoring finds nothing.
        var dbShaped = _dbShapeMarkers.Value.IsMatch(question);

        // F3.f — Zero keyword matches doesn't mean "not a tool question". Proper nouns the
        // scorer doesn't know about (a country name, a city, a currency code) routinely match
        // semantically. For non-DB-shaped questions, ask the LLM to evaluate the full tool set
        // before falling through. Budget-gated inside ResolveByLlmAsync, so a "no fit" answer
        // is at most one cheap call. DB-shaped questions still skip — those belong to the planner.
        if (candidates.Count == 0)
        {
            if (dbShaped)
                return (null, "no keyword match (DB-shaped — planner takes it)");
            return await ResolveByLlmAsync(question, tools.Take(8).ToList(), cancellationToken);
        }

        var top = candidates[0];
        var second = candidates.Count > 1 ? candidates[1].Score : 0;
        var candidateSummary = FormatToolCandidates(candidates);

        // DB-shape veto: when the question contains support/database vocabulary, raise the bar
        // and skip LLM-fallback entirely. Tickets/users/roles questions are the planner's job;
        // they only reach a tool through a scoring fluke and the wrong answer is worse than a
        // refusal here. We accept a tool only when keyword evidence is overwhelming
        // (>= DbShapedQuestionToolGate, e.g. user explicitly named the tool / used its test prompt).
        if (dbShaped)
        {
            if (top.Score >= DbShapedQuestionToolGate && top.Score - second >= ScoreGapThreshold)
                return (top.Tool, $"keyword-overwhelming despite DB markers ({top.Score} pts); candidates: {candidateSummary}");
            return (null, $"DB-shape markers present; tool dispatch suppressed (top score {top.Score} < gate {DbShapedQuestionToolGate}); candidates: {candidateSummary}");
        }

        if (top.Score >= HighConfidenceScoreThreshold && top.Score - second >= ScoreGapThreshold)
            return (top.Tool, $"keyword-confident ({top.Score} pts, gap {top.Score - second}); candidates: {candidateSummary}");

        // Single candidate: accept only when the package-backed lexical score is strong enough.
        // A lone weak overlap should fall through to the database/chat pipeline, not dispatch
        // an arbitrary external tool.
        if (candidates.Count == 1)
        {
            if (top.Score >= LlmFallbackMinTopScore)
                return (top.Tool, $"single tool candidate ({top.Score} pts); candidates: {candidateSummary}");
            return (null, $"single tool candidate below gate ({top.Score} < {LlmFallbackMinTopScore}); candidates: {candidateSummary}");
        }

        // Multi-candidate AND ambiguous. Gate the LLM call by minimum top score — without this,
        // two tools each scraping a +3 keyword overlap from a question that isn't really tool-shaped
        // ("what about countries that use the dollar in this database") would each cost a budget slot.
        if (top.Score < LlmFallbackMinTopScore)
            return (null, $"top score {top.Score} below LLM-fallback gate {LlmFallbackMinTopScore}; candidates: {candidateSummary}");

        // Only NOW do we pay an LLM call. The budget gate inside ResolveByLlmAsync ensures we
        // degrade to the keyword winner if budget is exhausted.
        return await ResolveByLlmAsync(question, candidates.Take(4).Select(c => c.Tool).ToList(), cancellationToken);
    }

    private static string FormatToolCandidates(IReadOnlyList<ToolMatchCandidate> candidates)
    {
        return string.Join("; ", candidates.Take(4).Select(c =>
        {
            var terms = c.MatchedTerms.Count == 0
                ? ""
                : " [" + string.Join("|", c.MatchedTerms.Take(3)) + "]";
            return $"{c.Tool.ToolKey}={c.Score}{terms}";
        }));
    }

    private static ToolDefinition? ResolveDirectRegistryMatch(string question, IReadOnlyList<ToolDefinition> tools)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var q = Normalize(question);
        if (string.IsNullOrWhiteSpace(q)) return null;

        foreach (var tool in tools)
        {
            var title = Normalize(tool.Title);
            var key = Normalize(tool.ToolKey.Replace('_', ' '));
            var testPrompt = Normalize(tool.TestPrompt ?? "");

            if (!string.IsNullOrWhiteSpace(title) && q.Contains(title, StringComparison.OrdinalIgnoreCase))
                return tool;
            if (!string.IsNullOrWhiteSpace(key) && q.Contains(key, StringComparison.OrdinalIgnoreCase))
                return tool;
            if (!string.IsNullOrWhiteSpace(testPrompt) && string.Equals(q, testPrompt, StringComparison.OrdinalIgnoreCase))
                return tool;
        }

        return null;
    }

    private async Task<(ToolDefinition? Tool, string Detail)> ResolveByLlmAsync(
        string question, IReadOnlyList<ToolDefinition> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return (null, "no candidates");
        var candidateKeys = string.Join(", ", candidates.Select(c => c.ToolKey));

        // Budget gate — LLM-fallback resolution costs one LLM call. When budget is exhausted,
        // return the highest-scoring candidate (best-effort) rather than refusing entirely.
        if (!_budget.TryConsumeLlmCall("ToolResolver"))
            return (candidates[0], $"LLM-fallback budget exhausted; using top keyword candidate; candidates: {candidateKeys}");

        var systemPrompt = "You are a tool router. Given a user question and a list of candidate tools, pick the ONE tool whose purpose best matches the question, or return \"none\" if no tool fits. Reply ONLY with valid JSON: { \"toolKey\": \"<the matching tool's toolKey or 'none'>\", \"reason\": \"<one sentence>\" }.";
        var sb = new StringBuilder();
        sb.AppendLine($"Question: {question}").AppendLine().AppendLine("Candidate tools:");
        foreach (var t in candidates)
        {
            sb.Append("- toolKey=").Append(t.ToolKey).Append(", title=").Append(t.Title);
            if (!string.IsNullOrWhiteSpace(t.Description)) sb.Append(", description=").Append(t.Description);
            if (!string.IsNullOrWhiteSpace(t.KeywordHints)) sb.Append(", keywords=").Append(t.KeywordHints);
            sb.AppendLine();
        }
        try
        {
            using var hint = SuperAdminCopilot.Abstractions.LlmCallStageHint.Use("ToolSelector");
            var raw = await _llm.GenerateJsonAsync(systemPrompt, sb.ToString(), cancellationToken);
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (doc.RootElement.TryGetProperty("toolKey", out var keyEl)
                && keyEl.GetString() is string key && !string.IsNullOrWhiteSpace(key)
                && !string.Equals(key, "none", StringComparison.OrdinalIgnoreCase))
            {
                var picked = candidates.FirstOrDefault(c => string.Equals(c.ToolKey, key, StringComparison.OrdinalIgnoreCase));
                if (picked is not null) return (picked, $"LLM-resolved: {key}; candidates: {candidateKeys}");
            }
            return (null, $"LLM returned 'none'; candidates: {candidateKeys}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ToolHandler] LLM-fallback resolution failed.");
            return (candidates[0], $"LLM-fallback errored; using top keyword candidate; candidates: {candidateKeys}");
        }
    }

    private sealed class ToolMatchCandidate
    {
        public required ToolDefinition Tool { get; init; }
        public int Score { get; set; }
        public List<string> MatchedTerms { get; } = new();
    }

    /// <summary>
    /// Token-overlap + package-backed fuzzy scorer. Direct substring evidence still wins, while
    /// FuzzierSharp catches volatile user phrasings against title/key/keywords/test prompt.
    /// </summary>
    private List<ToolMatchCandidate> ScoreCandidates(string question, IReadOnlyList<ToolDefinition> tools)
    {
        var stopwords = Stopwords;       // snapshot once per call so all Tokenize uses agree
        var qNorm = Normalize(question);
        var qTokens = Tokenize(qNorm, stopwords);
        var output = new List<ToolMatchCandidate>(tools.Count);

        foreach (var tool in tools)
        {
            var c = new ToolMatchCandidate { Tool = tool };
            var titleNorm = Normalize(tool.Title);
            var testNorm = Normalize(tool.TestPrompt ?? "");

            if (!string.IsNullOrWhiteSpace(titleNorm) && qNorm.Contains(titleNorm, StringComparison.OrdinalIgnoreCase))
            { c.Score += 8; c.MatchedTerms.Add(titleNorm); }

            if (!string.IsNullOrWhiteSpace(testNorm))
            {
                if (string.Equals(qNorm, testNorm, StringComparison.OrdinalIgnoreCase))
                { c.Score += 10; c.MatchedTerms.Add("test-prompt-exact"); }
                else
                    c.Score += CountOverlap(qTokens, Tokenize(tool.TestPrompt!, stopwords), maxWeight: 4);
            }

            foreach (var kw in SplitTerms(tool.KeywordHints))
            {
                if (qNorm.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    c.Score += kw.Contains(' ') ? 7 : 5;
                    c.MatchedTerms.Add(kw);
                }
            }

            foreach (var t in Tokenize(tool.Title, stopwords))
                if (qTokens.Contains(t)) { c.Score += 3; c.MatchedTerms.Add(t); }

            c.Score += CountOverlap(qTokens, Tokenize(tool.Description ?? "", stopwords), maxWeight: 3);

            var fuzzyScore = BestToolFuzzyScore(qNorm, tool, out var fuzzyLabel);
            if (fuzzyScore >= 0.88)
            {
                c.Score += 10;
                c.MatchedTerms.Add($"fuzzy:{fuzzyLabel}:{fuzzyScore:F2}");
            }
            else if (fuzzyScore >= 0.78)
            {
                c.Score += 6;
                c.MatchedTerms.Add($"fuzzy:{fuzzyLabel}:{fuzzyScore:F2}");
            }
            else if (fuzzyScore >= 0.68)
            {
                c.Score += 3;
                c.MatchedTerms.Add($"fuzzy:{fuzzyLabel}:{fuzzyScore:F2}");
            }

            output.Add(c);
        }
        return output;
    }

    private static double BestToolFuzzyScore(string normalizedQuestion, ToolDefinition tool, out string matchedLabel)
    {
        matchedLabel = "";
        if (string.IsNullOrWhiteSpace(normalizedQuestion)) return 0.0;

        var best = 0.0;
        foreach (var label in ToolLabels(tool))
        {
            var normalizedLabel = Normalize(label);
            if (string.IsNullOrWhiteSpace(normalizedLabel)) continue;
            var weighted = Fuzz.WeightedRatio(normalizedQuestion, normalizedLabel) / 100.0;
            var tokenSet = Fuzz.TokenSetRatio(normalizedQuestion, normalizedLabel) / 100.0;
            var score = Math.Max(weighted, tokenSet);
            if (score <= best) continue;
            best = score;
            matchedLabel = normalizedLabel;
        }

        return best;
    }

    private static IEnumerable<string> ToolLabels(ToolDefinition tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Title)) yield return tool.Title;
        if (!string.IsNullOrWhiteSpace(tool.ToolKey)) yield return tool.ToolKey.Replace('_', ' ');
        if (!string.IsNullOrWhiteSpace(tool.TestPrompt)) yield return tool.TestPrompt;
        foreach (var kw in SplitTerms(tool.KeywordHints)) yield return kw;
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();

    private static HashSet<string> Tokenize(string text, HashSet<string> stopwords)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = Regex.Split(text, @"[^\w]+")
            .Where(t => t.Length >= 3 && !stopwords.Contains(t))
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tokens;
    }

    private static IEnumerable<string> SplitTerms(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Enumerable.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(t => t.ToLowerInvariant())
                 .Where(t => t.Length >= 2);

    private static int CountOverlap(HashSet<string> a, HashSet<string> b, int maxWeight)
    {
        var hits = 0;
        foreach (var t in a) if (b.Contains(t)) hits++;
        return Math.Min(hits, maxWeight);
    }

    // ── Parameter extraction (F3.c) ─────────────────────────────────────────

    private sealed record ParamExtractionResult(
        Dictionary<string, string> Resolved,
        List<string> MissingRequired);

    private static readonly Regex PlaceholderRegex = new(@"\{(?<name>\w+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Pull <c>{name}</c> placeholders out of the tool's EndpointUrl. Treat <c>{query}</c> as
    /// the optional default-fill (filled with the question itself). Other placeholders are
    /// "required"; we try to extract them from the question via simple heuristics. Missing
    /// required params trigger a clarification.
    /// </summary>
    private static ParamExtractionResult ExtractParameters(string question, ToolDefinition tool)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        if (string.IsNullOrEmpty(tool.EndpointUrl))
            return new ParamExtractionResult(resolved, missing);

        foreach (Match m in PlaceholderRegex.Matches(tool.EndpointUrl))
        {
            var name = m.Groups["name"].Value;
            if (resolved.ContainsKey(name)) continue;

            // Special-case "query" — fill from the whole question.
            if (string.Equals(name, "query", StringComparison.OrdinalIgnoreCase))
            {
                resolved[name] = ExtractQueryValue(question, tool);
                continue;
            }

            // Heuristic: look for "<name> X" / "in X" / "of X" in the question; capture X.
            var pattern = $@"\b(?:{Regex.Escape(name)}|in|of|for|at)\s+(?<val>[A-Za-z][\w\-' ]+?)(?:[?.!,]|$|\s+(?:and|or|but)\b)";
            var pm = Regex.Match(question, pattern, RegexOptions.IgnoreCase);
            if (pm.Success)
            {
                resolved[name] = pm.Groups["val"].Value.Trim();
                continue;
            }

            // Couldn't extract — required parameter is missing.
            missing.Add(name);
        }

        return new ParamExtractionResult(resolved, missing);
    }

    private static string ExtractQueryValue(string question, ToolDefinition tool)
    {
        var q = question.Trim().TrimEnd('?', '.', '!');
        var afterPreposition = Regex.Match(q, @"\b(?:in|of|for|at|about)\s+(?<value>[A-Za-z0-9][\w\s.'-]+)$", RegexOptions.IgnoreCase);
        if (afterPreposition.Success)
            return afterPreposition.Groups["value"].Value.Trim();

        foreach (var term in RegistryTerms(tool).OrderByDescending(t => t.Length))
        {
            if (term.Length < 3) continue;
            var pattern = $@"\b{Regex.Escape(term)}\b\s*(?:of|for|in|about|:)?\s*(?<value>.+)$";
            var m = Regex.Match(q, pattern, RegexOptions.IgnoreCase);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["value"].Value))
                return m.Groups["value"].Value.Trim();
        }

        var code = Regex.Match(q, @"\b(?<code>[A-Z]{3})\b");
        if (code.Success) return code.Groups["code"].Value.ToUpperInvariant();

        return q;
    }

    private static IEnumerable<string> RegistryTerms(ToolDefinition tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Title)) yield return tool.Title;
        if (!string.IsNullOrWhiteSpace(tool.ToolKey)) yield return tool.ToolKey.Replace('_', ' ');
        if (!string.IsNullOrWhiteSpace(tool.TestPrompt)) yield return tool.TestPrompt;
        foreach (var kw in SplitTerms(tool.KeywordHints)) yield return kw;
    }

    private static string SubstituteParameters(string endpoint, IReadOnlyDictionary<string, string> resolved, string question)
    {
        var url = endpoint;
        foreach (var kv in resolved)
            url = url.Replace($"{{{kv.Key}}}", Uri.EscapeDataString(kv.Value), StringComparison.OrdinalIgnoreCase);
        // Any remaining {name} placeholders that we couldn't resolve get filled with the question
        // (best-effort, won't reach this code if a required param was actually missing).
        url = url.Replace("{query}", Uri.EscapeDataString(question), StringComparison.OrdinalIgnoreCase);
        return url;
    }

    private static string BuildClarificationQuestion(ToolDefinition tool, IReadOnlyList<string> missing)
    {
        var paramList = string.Join(", ", missing.Select(p => $"`{p}`"));
        return $"To use the **{tool.Title}** tool I need: {paramList}. " +
               $"Could you include {(missing.Count == 1 ? "it" : "those")} in your question?";
    }

    // ── JSON formatters (F3.d) ──────────────────────────────────────────────

    /// <summary>
    /// Try the typed formatters first (Country, FX, Holiday, Location, University). When none
    /// match, return null so the caller falls through to the generic table projection. <paramref name="formatterName"/>
    /// names the formatter that fired so the trace shows which path was taken.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>>? TryFormatPayload(
        ToolDefinition tool, string payload, out string formatterName)
    {
        formatterName = "generic";
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (TryFormatCountryProfile(root, out var rows)) { formatterName = "country-profile"; return rows; }
            if (TryFormatCurrencyCountries(root, out rows)) { formatterName = "currency-countries"; return rows; }
            if (TryFormatFx(root, out rows)) { formatterName = "fx"; return rows; }
            if (TryFormatHolidays(root, out rows)) { formatterName = "holidays"; return rows; }
            if (TryFormatLocations(root, out rows)) { formatterName = "locations"; return rows; }
        }
        catch (JsonException) { /* fall through to generic */ }
        return null;
    }

    private static bool TryFormatCountryProfile(JsonElement root, out IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return false;
        var first = root[0];
        if (!first.TryGetProperty("capital", out _) || !first.TryGetProperty("currencies", out _)) return false;

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var country in root.EnumerateArray().Take(10))
        {
            var name = country.TryGetProperty("name", out var n) && n.TryGetProperty("common", out var cn) ? cn.GetString() : null;
            var capital = country.TryGetProperty("capital", out var ca) && ca.ValueKind == JsonValueKind.Array && ca.GetArrayLength() > 0 ? ca[0].GetString() : null;
            var region = country.TryGetProperty("region", out var r) ? r.GetString() : null;
            var population = country.TryGetProperty("population", out var p) && p.TryGetInt64(out var pop) ? pop : (long?)null;
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = name, ["Capital"] = capital, ["Region"] = region, ["Population"] = population,
            });
        }
        rows = list;
        return list.Count > 0;
    }

    private static bool TryFormatFx(JsonElement root, out IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("rates", out var ratesEl) || ratesEl.ValueKind != JsonValueKind.Object) return false;
        var baseCur = root.TryGetProperty("base", out var b) ? b.GetString() : "";
        var date = root.TryGetProperty("date", out var d) ? d.GetString() : "";
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var rate in ratesEl.EnumerateObject().Take(20))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["Base"] = baseCur, ["Currency"] = rate.Name,
                ["Rate"] = rate.Value.ValueKind == JsonValueKind.Number ? (object)rate.Value.GetDouble() : rate.Value.ToString(),
                ["Date"] = date,
            });
        }
        rows = list;
        return list.Count > 0;
    }

    private static bool TryFormatCurrencyCountries(JsonElement root, out IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return false;
        var first = root[0];
        if (!first.TryGetProperty("currencies", out _)) return false;

        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var country in root.EnumerateArray().Take(30))
        {
            var name = country.TryGetProperty("name", out var n) && n.TryGetProperty("common", out var cn) ? cn.GetString() : null;
            var capital = country.TryGetProperty("capital", out var ca) && ca.ValueKind == JsonValueKind.Array && ca.GetArrayLength() > 0 ? ca[0].GetString() : null;
            var region = country.TryGetProperty("region", out var r) ? r.GetString() : null;
            string? currencyCodes = null;
            if (country.TryGetProperty("currencies", out var cur) && cur.ValueKind == JsonValueKind.Object)
                currencyCodes = string.Join(", ", cur.EnumerateObject().Select(p => p.Name));
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = name, ["Capital"] = capital, ["Region"] = region, ["Currencies"] = currencyCodes,
            });
        }
        rows = list;
        return list.Count > 0;
    }

    private static bool TryFormatHolidays(JsonElement root, out IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return false;
        var first = root[0];
        if (!first.TryGetProperty("date", out _) || !first.TryGetProperty("countryCode", out _)) return false;
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var h in root.EnumerateArray().Take(20))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["Date"] = h.TryGetProperty("date", out var d) ? d.GetString() : null,
                ["Name"] = h.TryGetProperty("name", out var n) ? n.GetString() : null,
                ["Country"] = h.TryGetProperty("countryCode", out var cc) ? cc.GetString() : null,
            });
        }
        rows = list;
        return list.Count > 0;
    }

    private static bool TryFormatLocations(JsonElement root, out IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array || resultsEl.GetArrayLength() == 0) return false;
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var loc in resultsEl.EnumerateArray().Take(10))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["Name"] = loc.TryGetProperty("name", out var n) ? n.GetString() : null,
                ["Country"] = loc.TryGetProperty("country", out var c) ? c.GetString() : null,
                ["Latitude"] = loc.TryGetProperty("latitude", out var la) && la.TryGetDouble(out var lav) ? (object)lav : null,
                ["Longitude"] = loc.TryGetProperty("longitude", out var lo) && lo.TryGetDouble(out var lov) ? (object)lov : null,
                ["Timezone"] = loc.TryGetProperty("timezone", out var tz) ? tz.GetString() : null,
            });
        }
        rows = list;
        return list.Count > 0;
    }

    // ── Generic JSON projection (fallback) ─────────────────────────────────

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectJsonToRows(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.Object => ProjectObject(root),
                JsonValueKind.Array  => ProjectArray(root),
                _ => SingleRow("Result", root.ToString()),
            };
        }
        catch (JsonException)
        {
            return SingleRow("Result", Truncate(payload, 1000));
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectObject(JsonElement obj)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject().Take(20))
            row[prop.Name] = ToScalar(prop.Value);
        return new[] { (IReadOnlyDictionary<string, object?>)row };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ProjectArray(JsonElement arr)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in arr.EnumerateArray().Take(20))
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.EnumerateObject().Take(20))
                    row[prop.Name] = ToScalar(prop.Value);
                rows.Add(row);
            }
            else rows.Add(new Dictionary<string, object?> { ["Result"] = ToScalar(item) });
        }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SingleRow(string col, string? value) =>
        new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { [col] = value } };

    private static object? ToScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var i) ? i : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => Truncate(e.ToString(), 250),
    };

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>Strip code fences / leading prose from an LLM JSON response.</summary>
    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "{}";
        var s = raw.Trim();
        if (s.StartsWith("```")) { var i = s.IndexOf('\n'); if (i > 0) s = s[(i + 1)..]; var f = s.LastIndexOf("```"); if (f > 0) s = s[..f]; }
        var open = s.IndexOf('{'); var close = s.LastIndexOf('}');
        if (open >= 0 && close > open) s = s.Substring(open, close - open + 1);
        return s.Trim();
    }
}
