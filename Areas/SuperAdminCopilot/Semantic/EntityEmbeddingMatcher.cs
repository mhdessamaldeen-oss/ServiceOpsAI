namespace SuperAdminCopilot.Semantic;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Schema;

/// <summary>
/// Embedding-based entity-match fallback (D.2 — port of legacy <c>EmbeddingEntityMatcher</c>).
/// When the JSON-config synonym dictionary has no entry for a word the user typed (e.g. "agent",
/// "case worker", "submitter") this matcher embeds the user's text and cosine-ranks every
/// declared entity's name + synonyms, returning the nearest match above a confidence threshold.
///
/// <para>Designed as an opt-in supplement to <see cref="ISemanticLayer.GetEntityByNameOrSynonym"/>
/// — consumers (EntityRootGuard, IntentNormalizer, future entity-root guards) can invoke this
/// async method when the synchronous lookup returns null. We deliberately don't replace the
/// synchronous static lookup with this async fallback, because (a) async-everywhere infects
/// every call site of SemanticLayer, and (b) the static dictionary still wins on the common case
/// (~95% of entity references hit a declared synonym).</para>
///
/// <para><b>Caching</b>: entity vectors are computed once on first call and held for the
/// process lifetime via <see cref="IMemoryCache"/> (no expiry). The embedder model name is part
/// of the cache key so swapping models invalidates the cache automatically.</para>
///
/// <para><b>Cost</b>: one embedding call per fallback invocation (the user's text), plus the
/// one-time entity priming. Catalog filtering ensures entities for tables that don't exist in
/// the current DB don't cost an embed.</para>
/// </summary>
public interface IEntityEmbeddingMatcher
{
    /// <summary>True when the embedder is available and at least one entity is declared.</summary>
    bool IsAvailable { get; }

    /// <summary>Find the entity whose name / synonyms are semantically closest to <paramref name="text"/>.
    /// Returns null when no entity exceeds <paramref name="minConfidence"/>.</summary>
    Task<EntityDefinition?> FindAsync(string text, float minConfidence = 0.65f, CancellationToken cancellationToken = default);
}

internal sealed class EntityEmbeddingMatcher : IEntityEmbeddingMatcher
{
    private const string CachePrefix = "super-admin-copilot::entity-embeddings::";

    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EntityEmbeddingMatcher> _logger;

    public EntityEmbeddingMatcher(
        ISemanticLayer semantic,
        IEntityCatalog catalog,
        ITextEmbedder embedder,
        IMemoryCache cache,
        ILogger<EntityEmbeddingMatcher> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _embedder = embedder;
        _cache = cache;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName)
        && (_semantic.Config?.Entities.Count ?? 0) > 0;

    public async Task<EntityDefinition?> FindAsync(string text, float minConfidence = 0.65f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!IsAvailable) return null;

        try
        {
            var queryVec = await _embedder.EmbedAsync(text, cancellationToken);
            if (queryVec.Length == 0) return null;

            var entityVecs = await GetEntityVectorsAsync(cancellationToken);
            if (entityVecs.Count == 0) return null;

            EntityDefinition? best = null;
            var bestScore = 0f;
            foreach (var (entity, vec) in entityVecs)
            {
                if (vec.Length != queryVec.Length) continue;
                var score = Cosine(queryVec, vec);
                if (score > bestScore)
                {
                    best = entity;
                    bestScore = score;
                }
            }

            if (best is null || bestScore < minConfidence)
            {
                _logger.LogDebug("[EntityEmbeddingMatcher] no match above {Min} for '{Text}' (best={Score:F3}).", minConfidence, text, bestScore);
                return null;
            }

            _logger.LogDebug("[EntityEmbeddingMatcher] '{Text}' → {Department} (cosine={Score:F3}).", text, best.Name, bestScore);
            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EntityEmbeddingMatcher] FindAsync failed for '{Text}'.", text);
            return null;
        }
    }

    /// <summary>Single-flight semaphore — when N concurrent callers race to prime the cache,
    /// only one actually issues the embedder calls; the rest wait and read the populated cache.
    /// Without this, parallel evals (or concurrent chat requests after a cold start) each
    /// invoked the embedder N×entity_count times, wasting tokens and racing on the cache write.</summary>
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    private async Task<IReadOnlyList<(EntityDefinition Department, float[] Vector)>> GetEntityVectorsAsync(CancellationToken cancellationToken)
    {
        var cacheKey = CachePrefix + (_embedder.ModelName ?? "default");
        if (_cache.TryGetValue<IReadOnlyList<(EntityDefinition, float[])>>(cacheKey, out var cached) && cached is not null)
            return cached;

        // First miss — acquire the priming lock so concurrent callers don't all hit the embedder.
        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have populated the cache while we waited.
            if (_cache.TryGetValue<IReadOnlyList<(EntityDefinition, float[])>>(cacheKey, out cached) && cached is not null)
                return cached;

            var entities = (_semantic.Config?.Entities ?? Enumerable.Empty<EntityDefinition>())
                .Where(e => !string.IsNullOrEmpty(e.Table) && _catalog.TableExists(e.Table))
                .ToList();

            var built = new List<(EntityDefinition, float[])>(entities.Count);
            foreach (var e in entities)
            {
                var label = BuildEntityLabel(e);
                try
                {
                    var vec = await _embedder.EmbedAsync(label, cancellationToken);
                    if (vec.Length > 0) built.Add((e, vec));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[EntityEmbeddingMatcher] entity priming failed for '{Department}'.", e.Name);
                }
            }
            _cache.Set(cacheKey, (IReadOnlyList<(EntityDefinition, float[])>)built);   // no expiry — invalidated on model swap via cache key
            return built;
        }
        finally
        {
            _primingLock.Release();
        }
    }

    /// <summary>Compose a single vector-friendly string from the entity. Description first when
    /// present (more discriminative than the bare table name), then name + synonyms.</summary>
    private static string BuildEntityLabel(EntityDefinition e)
    {
        var parts = new List<string> { e.Name };
        if (!string.IsNullOrEmpty(e.Description)) parts.Add(e.Description);
        if (e.Synonyms is { Count: > 0 }) parts.Add(string.Join(", ", e.Synonyms));
        return string.Join(" — ", parts);
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }
}
