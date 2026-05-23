namespace SuperAdminCopilot.HostBridge;

using SuperAdminCopilot.Abstractions;

/// <summary>
/// Decorator over <see cref="ITextEmbedder"/> that memoises calls within an active
/// <see cref="QuestionEmbeddingScope"/>. When no scope is open the decorator is a no-op
/// passthrough — preserving the embedder's normal behaviour for warmup / startup paths.
///
/// <para>Wrapped at DI registration via the decorator pattern: the real embedder is
/// registered as a concrete type and this decorator is what <c>ITextEmbedder</c> resolves
/// to. Net effect: every existing consumer (VectorRetriever, PastQuestionStore, etc.)
/// automatically benefits without a signature change.</para>
/// </summary>
internal sealed class CachingTextEmbedder : ITextEmbedder
{
    private readonly ITextEmbedder _inner;
    public CachingTextEmbedder(ITextEmbedder inner) { _inner = inner; }

    public string ModelName => _inner.ModelName;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var scope = QuestionEmbeddingScope.Current;
        if (scope is null) return await _inner.EmbedAsync(text, cancellationToken);
        var cached = scope.TryGet(text, _inner.ModelName);
        if (cached is not null && cached.Length > 0) return cached;
        var vec = await _inner.EmbedAsync(text, cancellationToken);
        // Only cache non-empty vectors. The contract on ITextEmbedder is "empty = unavailable",
        // and we don't want to poison the cache with a one-off provider hiccup.
        if (vec is { Length: > 0 }) scope.Set(text, _inner.ModelName, vec);
        return vec;
    }
}
