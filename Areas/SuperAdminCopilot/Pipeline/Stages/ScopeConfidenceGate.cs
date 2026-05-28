namespace SuperAdminCopilot.Pipeline.Stages;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Retrieval;

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
/// configurable per deployment via <see cref="CopilotOptions.OutOfScopeSchemaFloor"/> and
/// <see cref="CopilotOptions.OutOfScopeVerifiedQueryFloor"/>.</para>
/// </summary>
public interface IScopeConfidenceGate
{
    /// <summary>Returns a refusal when both scope signals are below their floors; null otherwise.
    /// Callers should run this AFTER the fast-path probes have all missed, so the only
    /// remaining question is whether the LLM-driven schema-extraction path should fire.</summary>
    Task<OutOfScopeResult?> CheckAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a scope-gate refusal. <see cref="MatchedPattern"/> carries a short
/// machine-readable reason ("low-scope-confidence") so the trace can attribute the refusal.</summary>
public sealed record OutOfScopeResult(string Reason, string MatchedPattern, string Language);

internal sealed class ScopeConfidenceGate : IScopeConfidenceGate
{
    private readonly IVerifiedQueryMatcher _verifiedQueryMatcher;
    private readonly ISchemaSemanticRetriever _schemaRetriever;
    private readonly IOptionsMonitor<CopilotOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textMonitor;
    private readonly ILogger<ScopeConfidenceGate> _logger;

    public ScopeConfidenceGate(
        IVerifiedQueryMatcher verifiedQueryMatcher,
        ISchemaSemanticRetriever schemaRetriever,
        IOptionsMonitor<CopilotOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textMonitor,
        ILogger<ScopeConfidenceGate> logger)
    {
        _verifiedQueryMatcher = verifiedQueryMatcher;
        _schemaRetriever = schemaRetriever;
        _options = options;
        _textMonitor = textMonitor;
        _logger = logger;
    }

    public async Task<OutOfScopeResult?> CheckAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var opts = _options.CurrentValue;
        if (!opts.EnableScopeConfidenceGate) return null;

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

        // Signal A: verified-query catalog cosine
        if (vqMaxSimilarity >= opts.OutOfScopeVerifiedQueryFloor) return null;

        // Signal B: schema-semantic top match
        if (schemaTopScore >= opts.OutOfScopeSchemaFloor) return null;

        // Both signals EXACTLY zero is the embedder-failure signature: both retrievers return
        // 0f from their catch blocks / empty-vector guards. Real cosines between non-zero
        // vectors are essentially never exactly 0 — even unrelated text scores ~0.05–0.20.
        // Treat this as "no signal" and fail-open rather than mis-refusing a valid question
        // (observed live: Ollama returned NaN for one input, refusing a clear data query).
        if (vqMaxSimilarity == 0f && schemaTopScore == 0f)
        {
            _logger.LogWarning("[ScopeGate] both signals exactly 0 for '{Q}' — likely embedder failure; failing open.", question);
            return null;
        }

        // Both signals weak — and the upstream fast paths already missed — so this question
        // does not link to anything in our configured scope. Refuse with the catalog message.
        _logger.LogInformation("[ScopeGate] REFUSED '{Q}' — both signals below floor.", question);
        return new OutOfScopeResult(
            _textMonitor.CurrentValue.PreflightOutOfScope,
            MatchedPattern: "low-scope-confidence",
            Language: "auto");
    }
}
