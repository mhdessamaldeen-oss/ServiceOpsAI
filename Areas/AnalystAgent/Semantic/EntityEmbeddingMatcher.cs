namespace AnalystAgent.Semantic;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Schema;

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
/// process lifetime via <see cref="IMemoryCache"/> (no expiry). The cache key folds in the embedder
/// model name AND a content hash of the (queryable) entity set's name|synonyms|table, so BOTH a model
/// swap and a semantic-layer synonym/description edit invalidate the cache automatically.</para>
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
    private const string CachePrefix = "analyst-agent::entity-embeddings::";

    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;
    private readonly ITextEmbedder _embedder;
    private readonly AnalystAgent.Schema.IAnalystSchemaAccessPolicy _accessPolicy;
    private readonly ILogger<EntityEmbeddingMatcher> _logger;
    private readonly EmbeddingVectorCache<EntityDefinition> _vectorCache;

    public EntityEmbeddingMatcher(
        ISemanticLayer semantic,
        IEntityCatalog catalog,
        ITextEmbedder embedder,
        IMemoryCache cache,
        AnalystAgent.Schema.IAnalystSchemaAccessPolicy accessPolicy,
        ILogger<EntityEmbeddingMatcher> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _embedder = embedder;
        _accessPolicy = accessPolicy;
        _logger = logger;
        // Compose the shared prime+rank+fail-open cache. This matcher keeps its own label builder
        // (BuildEntityLabel) + cache-key builder (model name + content hash of the queryable entity set).
        _vectorCache = new EmbeddingVectorCache<EntityDefinition>(embedder, cache, logger);
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName)
        && (_semantic.Config?.Entities.Count ?? 0) > 0;

    public async Task<EntityDefinition?> FindAsync(string text, float minConfidence = 0.65f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!IsAvailable) return null;

        // The queryable entity set (table declared + exists + not hidden) — both the prime corpus and
        // the content-hash source, so a synonym/description edit re-keys the cache.
        var entities = (_semantic.Config?.Entities ?? Enumerable.Empty<EntityDefinition>())
            .Where(e => !string.IsNullOrEmpty(e.Table) && _catalog.TableExists(e.Table) && _accessPolicy.IsTableQueryable(e.Table))
            .ToList();
        if (entities.Count == 0) return null;

        var cacheKey = CachePrefix + (_embedder.ModelName ?? "default") + "::" + ComputeEntitySetHash(entities);

        // Shared prime + cosine-rank + fail-open. Returns scored pairs in INPUT order, so the
        // best-above-threshold selection below (strict '>', first-wins on ties) is byte-identical to before.
        var scored = await _vectorCache.PrimeAndRankAsync(entities, BuildEntityLabel, cacheKey, text, cancellationToken);
        if (scored.Count == 0) return null;

        EntityDefinition? best = null;
        var bestScore = 0f;
        foreach (var (entity, score) in scored)
        {
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

    /// <summary>Compose a single vector-friendly string from the entity. Description first when
    /// present (more discriminative than the bare table name), then name + synonyms.</summary>
    internal static string BuildEntityLabel(EntityDefinition e)
    {
        var parts = new List<string> { e.Name };
        if (!string.IsNullOrEmpty(e.Description)) parts.Add(e.Description);
        if (e.Synonyms is { Count: > 0 }) parts.Add(string.Join(", ", e.Synonyms));
        return string.Join(" — ", parts);
    }

    /// <summary>Stable content hash of the queryable entity set — concatenate each entity's
    /// name|synonyms|description|table and SHA-256 it. Any semantic-layer edit that changes the embedded
    /// label (a renamed entity, an added/removed synonym, an edited description, a re-pointed table)
    /// changes the hash → the cache key → a fresh prime. Backported from <see cref="ToolSemanticSelector"/>
    /// so an entity-synonym edit invalidates (the old model-name-only key did not). Lines are sorted so a
    /// pure re-ordering of the entity list doesn't needlessly invalidate.</summary>
    internal static string ComputeEntitySetHash(IReadOnlyList<EntityDefinition> entities)
    {
        var lines = entities
            .Select(e => $"{e.Name}|{(e.Synonyms is { Count: > 0 } ? string.Join(",", e.Synonyms) : "")}|{e.Description}|{e.Table}")
            .OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join("\n", lines);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes);
    }
}
