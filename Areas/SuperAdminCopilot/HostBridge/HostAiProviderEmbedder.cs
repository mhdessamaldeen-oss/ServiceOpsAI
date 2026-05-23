namespace SuperAdminCopilot.HostBridge;

using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;

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
    private readonly ILogger<HostAiProviderEmbedder> _logger;
    private string? _cachedModelName;

    public HostAiProviderEmbedder(IAiProviderFactory factory, ILogger<HostAiProviderEmbedder> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string ModelName => _cachedModelName ??=
        _factory.GetProviderForWorkload(AiWorkloadType.Rag).ModelName ?? "unknown";

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
        try
        {
            var provider = _factory.GetProviderForWorkload(AiWorkloadType.Rag);
            return await provider.GetEmbeddingAsync(text);
        }
        catch (Exception ex)
        {
            // Empty array signals "embedding unavailable" — caller (VectorRetriever) falls back
            // to KeywordRetriever rather than crashing the pipeline. Logged at warning so an
            // operator notices a misconfigured Rag-workload provider without flooding error logs.
            _logger.LogWarning(ex, "[SuperAdminCopilot] Embedding failed via Rag-workload provider; caller will fall back.");
            return Array.Empty<float>();
        }
    }
}
