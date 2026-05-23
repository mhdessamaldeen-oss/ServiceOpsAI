using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.Extensions.Logging;

namespace ServiceOpsAI.Services.AI
{
    /// <summary>
    /// Ticket embedder that delegates to whichever provider is currently selected for the
    /// <see cref="AiWorkloadType.Rag"/> workload. Replaces the hardcoded OllamaTicketEmbedder —
    /// the model name and the underlying provider both come from the app's existing model-selection
    /// UI (no more `=> "bge-m3"` baked into source).
    ///
    /// <para><b>Tagging:</b> Each generated vector is tagged with the model name returned by the
    /// active Rag provider. The candidate query in SemanticSearchService filters by this tag, so
    /// switching the active embedder model effectively partitions the embedding store — old vectors
    /// remain valid for the model that produced them but become invisible to queries embedded with
    /// the new model. Re-embed the corpus when you switch (one-time cost).</para>
    /// </summary>
    public sealed class ProviderTicketEmbedder : ITicketEmbedder
    {
        private readonly IAiProviderFactory _providerFactory;
        private readonly ILogger<ProviderTicketEmbedder> _logger;

        public ProviderTicketEmbedder(IAiProviderFactory providerFactory, ILogger<ProviderTicketEmbedder> logger)
        {
            _providerFactory = providerFactory;
            _logger = logger;
        }

        // Cache the model-name reference to avoid hitting the provider factory (and thus the DB)
        // on every property read. The embedder is a request-scoped singleton; the model can change
        // mid-request only if the admin updates settings, which warrants an app restart anyway.
        // SemanticSearchService reads this property repeatedly during a single search call (logs,
        // candidate-filter, trace) — caching makes that O(1) instead of O(N) factory invocations.
        private string? _cachedModelName;
        public string ModelName => _cachedModelName ??=
            _providerFactory.GetProviderForWorkload(AiWorkloadType.Rag).ModelName;

        public float[] GenerateEmbedding(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
            try
            {
                var provider = _providerFactory.GetProviderForWorkload(AiWorkloadType.Rag);
                // Sync wait is safe here: callers are background workers (EmbeddingQueueService) and
                // request-bound search paths that are already on a thread-pool thread.
                return provider.GetEmbeddingAsync(text).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding generation failed via Rag-workload provider.");
                return Array.Empty<float>();
            }
        }
    }
}
