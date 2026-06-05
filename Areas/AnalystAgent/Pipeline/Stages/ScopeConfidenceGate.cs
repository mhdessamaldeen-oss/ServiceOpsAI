namespace AnalystAgent.Pipeline.Stages;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Retrieval;

/// <summary>
/// Positive scope-confidence gate. Refuses a question as out-of-scope ONLY when all fast paths
/// (Conversational, KnowledgeMatch, SemanticSearch, ToolHandler, VerifiedQueryMatcher) have
/// missed AND both of the lightweight scope signals below come back weak:
///
/// <list type="bullet">
///   <item><b>Schema-linker top score</b> — does this question even cosine-match any table
///   summary in <c>schema-inferred.json</c>?</item>
///   <item><b>Verified-query max similarity</b> — is this question loosely close to any
///   curated catalog entry (lower bar than the strict catalog-trust threshold)?</item>
/// </list>
///
/// <para><b>Why positive scope:</b> out-of-scope is an infinite space — every domain a user could
/// ask about that isn't ours. We can only enumerate what IS in scope: the schema we expose, the
/// tools we've registered, the curated catalog, the fast-path patterns. Anything that doesn't
/// link to ANY of those is out-of-scope by definition. This replaces the prior regex-bank
/// <c>OutOfScopeHandler</c>, which couldn't survive being packaged as a DLL into a different
/// application (different schema, different language, different domain).</para>
///
/// <para>Refusal text comes from <see cref="CopilotTextCatalog.PreflightOutOfScope"/>. Floors are
/// configurable per deployment via <see cref="AnalystOptions.OutOfScopeSchemaFloor"/> and
/// <see cref="AnalystOptions.OutOfScopeVerifiedQueryFloor"/>.</para>
/// </summary>
public interface IScopeConfidenceGate
{
    /// <summary>Computes both scope signals and decides refuse-vs-answer. <see cref="ScopeGateOutcome.Refusal"/>
    /// is non-null only when both signals are below their floors; <see cref="ScopeGateOutcome.Signals"/> carries
    /// the actual cosines + floors so the trace can show the "moat" on every gated question (answered or refused).
    /// Callers should run this AFTER the fast-path probes have all missed.</summary>
    Task<ScopeGateOutcome> CheckAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a scope-gate refusal. <see cref="MatchedPattern"/> carries a short
/// machine-readable reason ("low-scope-confidence") so the trace can attribute the refusal.</summary>
public sealed record OutOfScopeResult(string Reason, string MatchedPattern, string Language);

/// <summary>The two scope cosines + their floors + the resolved top table — the refuse-vs-answer "moat",
/// surfaced into the trace so the owner sees WHY a question was (not) refused without reading code.</summary>
public sealed record ScopeSignals(
    double VerifiedQueryMax, double VerifiedQueryFloor,
    double SchemaTop, double SchemaFloor, string TopTable, bool FailedOpen);

/// <summary>Gate result: the optional refusal + the signals that produced it. Signals is null only when the
/// gate is disabled or the question was blank (nothing computed).</summary>
public sealed record ScopeGateOutcome(OutOfScopeResult? Refusal, ScopeSignals? Signals);

internal sealed class ScopeConfidenceGate : IScopeConfidenceGate
{
    private readonly IVerifiedQueryMatcher _verifiedQueryMatcher;
    private readonly ISchemaSemanticRetriever _schemaRetriever;
    private readonly IOptionsMonitor<AnalystOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textMonitor;
    private readonly ILogger<ScopeConfidenceGate> _logger;

    public ScopeConfidenceGate(
        IVerifiedQueryMatcher verifiedQueryMatcher,
        ISchemaSemanticRetriever schemaRetriever,
        IOptionsMonitor<AnalystOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textMonitor,
        ILogger<ScopeConfidenceGate> logger)
    {
        _verifiedQueryMatcher = verifiedQueryMatcher;
        _schemaRetriever = schemaRetriever;
        _options = options;
        _textMonitor = textMonitor;
        _logger = logger;
    }

    public async Task<ScopeGateOutcome> CheckAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return new ScopeGateOutcome(null, null);
        var opts = _options.CurrentValue;
        if (!opts.EnableScopeConfidenceGate) return new ScopeGateOutcome(null, null);

        // Always compute BOTH signals up-front and log them — even on a pass — so trace
        // history captures the actual scores. Critical for floor-calibration tuning later.
        var vqMaxSimilarity = await _verifiedQueryMatcher.MaxSimilarityAsync(question, cancellationToken);
        var schemaResult = await _schemaRetriever.RetrieveAsync(question, topK: opts.ScopeGateRetrieverTopK, cancellationToken);
        var schemaTopScore = schemaResult.Tables.Count > 0 ? schemaResult.Tables[0].Score : 0f;
        var topTable = schemaResult.Tables.Count > 0 ? schemaResult.Tables[0].Table.Name : "(none)";

        // Information level (not debug) so it's visible in normal app logs without flipping
        // log levels — calibration data is worth the small noise cost.
        _logger.LogInformation("[ScopeGate] '{Q}' vqMax={Vq:F3} (floor {VqFloor}) | schemaTop={Schema:F3} on '{Table}' (floor {SchemaFloor})",
            question, vqMaxSimilarity, opts.OutOfScopeVerifiedQueryFloor,
            schemaTopScore, topTable, opts.OutOfScopeSchemaFloor);

        ScopeSignals Signals(bool failedOpen) => new(
            vqMaxSimilarity, opts.OutOfScopeVerifiedQueryFloor,
            schemaTopScore, opts.OutOfScopeSchemaFloor, topTable, failedOpen);

        // Signal A: verified-query catalog cosine
        if (vqMaxSimilarity >= opts.OutOfScopeVerifiedQueryFloor) return new ScopeGateOutcome(null, Signals(false));

        // Signal B: schema-semantic top match
        if (schemaTopScore >= opts.OutOfScopeSchemaFloor) return new ScopeGateOutcome(null, Signals(false));

        // Both signals EXACTLY zero is the embedder-failure signature: both retrievers return
        // 0f from their catch blocks / empty-vector guards. Real cosines between non-zero
        // vectors are essentially never exactly 0 — even unrelated text scores ~0.05–0.20.
        // Treat this as "no signal" and fail-open rather than mis-refusing a valid question
        // (observed live: Ollama returned NaN for one input, refusing a clear data query).
        if (vqMaxSimilarity == 0f && schemaTopScore == 0f)
        {
            _logger.LogWarning("[ScopeGate] both signals exactly 0 for '{Q}' — likely embedder failure; failing open.", question);
            return new ScopeGateOutcome(null, Signals(failedOpen: true));
        }

        // Both signals weak — and the upstream fast paths already missed — so this question
        // does not link to anything in our configured scope. Refuse with the catalog message.
        _logger.LogInformation("[ScopeGate] REFUSED '{Q}' — both signals below floor.", question);
        return new ScopeGateOutcome(
            new OutOfScopeResult(
                _textMonitor.CurrentValue.PreflightOutOfScope,
                MatchedPattern: "low-scope-confidence",
                Language: "auto"),
            Signals(false));
    }
}
