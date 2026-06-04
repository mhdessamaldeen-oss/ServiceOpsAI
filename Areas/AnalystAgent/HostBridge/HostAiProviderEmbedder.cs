namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;

/// <summary>
/// Bridges the new copilot's <see cref="ITextEmbedder"/> to the host's existing
/// <see cref="IAiProviderFactory"/>. Embeddings flow through whatever provider the user has
/// selected for the <see cref="AiWorkloadType.Rag"/> workload in the settings UI — the same
/// workload the host's existing semantic-search uses, so one settings knob covers both.
///
/// THIS IS THE ONLY EMBEDDING FILE that depends on host AI types. When this code moves to a DLL,
/// only this file is replaced (the rest of the copilot sees only <see cref="ITextEmbedder"/>).
/// </summary>
internal sealed class HostAiProviderEmbedder : ITextEmbedder
{
    private readonly IAiProviderFactory _factory;
    private readonly IOptionsMonitor<AnalystOptions> _options;
    private readonly ILogger<HostAiProviderEmbedder> _logger;
    private string? _cachedModelName;

    public HostAiProviderEmbedder(
        IAiProviderFactory factory,
        IOptionsMonitor<AnalystOptions> options,
        ILogger<HostAiProviderEmbedder> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public string ModelName => _cachedModelName ??=
        _factory.GetProviderForWorkload(AiWorkloadType.Rag).ModelName ?? "unknown";

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dims = 0; var success = false; string? error = null;
        try
        {
            var provider = _factory.GetProviderForWorkload(AiWorkloadType.Rag);
            var vec = await provider.GetEmbeddingAsync(text);
            dims = vec?.Length ?? 0;
            success = dims > 0;
            return vec ?? Array.Empty<float>();
        }
        catch (Exception ex)
        {
            // Empty array signals "embedding unavailable" — caller (VectorRetriever) falls back
            // to KeywordRetriever rather than crashing the pipeline. Logged at warning so an
            // operator notices a misconfigured Rag-workload provider without flooding error logs.
            error = ex.Message;
            _logger.LogWarning(ex, "[AnalystAgent] Embedding failed via Rag-workload provider; caller will fall back.");
            return Array.Empty<float>();
        }
        finally
        {
            sw.Stop();
            RecordTrace(text, dims, sw.ElapsedMilliseconds, success, error);
        }
    }

    /// <summary>Record this embedding call into the per-question trace scope (Kind="embedding").
    /// The embedded text is the "prompt"; the "response" is just the vector dimension (vectors aren't
    /// human-readable). Best-effort; only the CachingTextEmbedder's real cache-misses reach here, so
    /// repeated identical embeds don't flood the trace.</summary>
    private void RecordTrace(string text, int dims, long elapsedMs, bool success, string? error)
    {
        if (LlmCallScope.Current is null) return;
        try
        {
            var opts = _options.CurrentValue;
            LlmCallScope.Current.Record(LlmTraceCapture.BuildRecord(
                stage: LlmCallStageHint.Current ?? "Embedding",
                provider: "Embedder",
                model: ModelName,
                usage: null,
                elapsedMs: elapsedMs,
                success: success,
                error: error,
                prompt: text,
                response: success ? $"[{dims}-dim vector]" : null,
                previewCap: opts.LlmTracePreviewMaxChars,
                fullCap: opts.LlmTraceFullMaxChars,
                kind: "embedding"));
        }
        catch { /* tracing must never break embedding */ }
    }
}
