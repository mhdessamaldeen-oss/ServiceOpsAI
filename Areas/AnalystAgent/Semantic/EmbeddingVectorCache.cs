namespace AnalystAgent.Semantic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;

/// <summary>
/// Shared embedding-vector priming + cosine-ranking helper. Extracts the pattern duplicated across
/// <see cref="ToolSemanticSelector"/> and <see cref="EntityEmbeddingMatcher"/>: lazily prime an
/// (item → float[]) vector list behind a single-flight lock keyed by a caller-supplied cache key,
/// embed the query once, cosine-rank every item, and FAIL OPEN (empty result) when the embedder is
/// unavailable or returns an empty vector.
///
/// <para><b>No vocabulary.</b> This class is purely mechanical — the caller supplies the items, a
/// <c>label</c> selector (item → the text to embed), and the cache key. It holds zero domain or
/// per-deployment literals, so it is portable to any item type.</para>
///
/// <para><b>Caching.</b> Primed vectors are held via the shared singleton <see cref="IMemoryCache"/>
/// with no expiry. The caller bakes whatever invalidation signal it needs (embedder model name +
/// a content hash of the item set) into <c>cacheKey</c>, so a model swap or a content edit produces
/// a different key and forces a fresh prime.</para>
///
/// <para><b>Single-flight.</b> The priming semaphore is STATIC so the guarantee is process-wide:
/// when N concurrent callers race a cold cache, only one issues the embedder calls; the rest wait
/// and read the populated cache. The cache is the shared singleton, so a static lock matches its
/// scope (keyed writes are idempotent + fail-open either way). The lock is per-closed-generic
/// (<typeparamref name="TItem"/>), which is exactly the granularity we want — tool priming and
/// entity priming don't contend with each other.</para>
///
/// <para><b>Ordering.</b> <see cref="PrimeAndRankAsync"/> returns scored pairs in the SAME order as
/// the supplied <c>items</c> (it does NOT sort). Callers that want descending-by-score sort the
/// result; callers that want the single best iterate and pick the max. This keeps both refactored
/// call sites byte-identical to their pre-refactor selection logic.</para>
/// </summary>
internal sealed class EmbeddingVectorCache<TItem>
{
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly ILogger _logger;

    public EmbeddingVectorCache(ITextEmbedder embedder, IMemoryCache cache, ILogger logger)
    {
        _embedder = embedder;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Process-wide single-flight gate for priming (see class remarks). Static + per-closed-generic.</summary>
    private static readonly SemaphoreSlim _primingLock = new(1, 1);

    /// <summary>
    /// Embed <paramref name="queryText"/> once, prime + cache the per-item vectors under
    /// <paramref name="cacheKey"/> (single-flight), and return each item with its cosine similarity to
    /// the query — in the SAME order as <paramref name="items"/>. Returns an empty list (FAIL OPEN) when
    /// the embedder model is unset, the query embeds to an empty vector, or no item vectors were primed.
    /// Items whose primed vector length differs from the query vector are skipped (never throw).
    /// </summary>
    /// <param name="items">The items to rank. Empty → empty result.</param>
    /// <param name="label">Item → the text to embed for it. An item whose label is null/blank is skipped at priming.</param>
    /// <param name="cacheKey">Stable key for the primed vector set (caller folds in model name + content hash).</param>
    /// <param name="queryText">The query to embed once and rank against.</param>
    public async Task<IReadOnlyList<(TItem Item, float Score)>> PrimeAndRankAsync(
        IReadOnlyList<TItem> items,
        Func<TItem, string?> label,
        string cacheKey,
        string queryText,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0 || string.IsNullOrWhiteSpace(queryText))
            return Array.Empty<(TItem, float)>();
        if (string.IsNullOrEmpty(_embedder.ModelName))
            return Array.Empty<(TItem, float)>();   // fail-open: no embedder configured

        try
        {
            var queryVec = await _embedder.EmbedAsync(queryText, cancellationToken);
            if (queryVec.Length == 0) return Array.Empty<(TItem, float)>();   // fail-open: embedder down

            var itemVecs = await GetItemVectorsAsync(items, label, cacheKey, cancellationToken);
            if (itemVecs.Count == 0) return Array.Empty<(TItem, float)>();

            var scored = new List<(TItem Item, float Score)>(itemVecs.Count);
            foreach (var (item, vec) in itemVecs)
            {
                if (vec.Length != queryVec.Length) continue;
                scored.Add((item, AnalystAgent.Retrieval.VectorMath.Cosine(queryVec, vec)));
            }
            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EmbeddingVectorCache<{Type}>] PrimeAndRankAsync failed for '{Q}'.",
                typeof(TItem).Name, queryText);
            return Array.Empty<(TItem, float)>();   // fail-open
        }
    }

    private async Task<IReadOnlyList<(TItem Item, float[] Vector)>> GetItemVectorsAsync(
        IReadOnlyList<TItem> items,
        Func<TItem, string?> label,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<IReadOnlyList<(TItem, float[])>>(cacheKey, out var cached) && cached is not null)
            return cached;

        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have populated the cache while we waited.
            if (_cache.TryGetValue<IReadOnlyList<(TItem, float[])>>(cacheKey, out cached) && cached is not null)
                return cached;

            var built = new List<(TItem, float[])>(items.Count);
            foreach (var item in items)
            {
                var text = label(item);
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    var vec = await _embedder.EmbedAsync(text, cancellationToken);
                    if (vec.Length > 0) built.Add((item, vec));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EmbeddingVectorCache<{Type}>] priming failed for one item.", typeof(TItem).Name);
                }
            }
            _cache.Set(cacheKey, (IReadOnlyList<(TItem, float[])>)built);   // no expiry — invalidated via cacheKey
            return built;
        }
        finally
        {
            _primingLock.Release();
        }
    }
}
