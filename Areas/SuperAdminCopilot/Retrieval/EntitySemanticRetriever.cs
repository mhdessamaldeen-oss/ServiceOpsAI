namespace SuperAdminCopilot.Retrieval;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Semantic;

/// <summary>
/// Entity-level semantic retriever (Slice 2 of the 2026-05-30 embedding plan). Counterpart to
/// <see cref="SchemaSemanticRetriever"/> (which embeds raw TABLE shape — name + columns) and
/// <see cref="ColumnSemanticRetriever"/> (which embeds individual COLUMNS): this one embeds
/// the DOMAIN-LEVEL entity description from <c>semantic-layer.json</c> — including the
/// human-readable description and the Arabic + English synonym lists.
///
/// <para>Why a third retriever: <see cref="SchemaSemanticRetriever"/> can pick the wrong root
/// when the question uses domain vocabulary that doesn't appear in raw column names
/// (e.g. Arabic "تذاكر" for Tickets; English "complaints" routed to Tickets by domain semantics).
/// The semantic-layer carries those mappings explicitly; this retriever surfaces them as
/// embeddings the LLM can attend to.</para>
///
/// <para>Same lazy-prime + IMemoryCache pattern as the sibling retrievers. Cache key is
/// versioned by (EmbeddingTextVersion, modelName, semanticLayerHash). Empty result on
/// embedder/layer unavailability — callers must tolerate fail-open.</para>
/// </summary>
public interface IEntitySemanticRetriever
{
    bool IsAvailable { get; }
    Task<EntitySemanticRetrieval> RetrieveAsync(
        string question,
        int topK = 5,
        float minSimilarity = 0.40f,
        CancellationToken cancellationToken = default);
}

public sealed class EntitySemanticRetrieval
{
    public IReadOnlyList<EntityMatch> Entities { get; init; } = Array.Empty<EntityMatch>();
}

/// <summary>One entity match. <see cref="Table"/> is the DB table name the planner uses;
/// <see cref="Description"/> is the natural-language entity meaning the LLM reads.</summary>
public sealed record EntityMatch(
    string Table,
    string Name,
    string Description,
    float Score);

internal sealed class EntitySemanticRetriever : IEntitySemanticRetriever
{
    // Bump EmbeddingTextVersion when BuildEmbeddingText changes — invalidates the cache so
    // stale vectors don't survive a deploy. Same idiom as SchemaSemanticRetriever.
    private const string CachePrefix = "super-admin-copilot::entity-vectors::";
    private const string EmbeddingTextVersion = "v1";

    private readonly ISemanticLayer _layer;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EntitySemanticRetriever> _logger;
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    public EntitySemanticRetriever(
        ISemanticLayer layer,
        ITextEmbedder embedder,
        IMemoryCache cache,
        ILogger<EntitySemanticRetriever> logger)
    {
        _layer = layer;
        _embedder = embedder;
        _cache = cache;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName) && _layer?.Config?.Entities is { Count: > 0 };

    public async Task<EntitySemanticRetrieval> RetrieveAsync(
        string question,
        int topK = 5,
        float minSimilarity = 0.40f,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable)
            return new EntitySemanticRetrieval();

        try
        {
            var entityVecs = await GetEntityVectorsAsync(cancellationToken);
            if (entityVecs.Count == 0) return new EntitySemanticRetrieval();

            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return new EntitySemanticRetrieval();

            var scored = new List<EntityMatch>(entityVecs.Count);
            foreach (var entry in entityVecs)
            {
                if (entry.Vector.Length != queryVec.Length) continue;
                var score = VectorMath.Cosine(queryVec, entry.Vector);
                if (score < minSimilarity) continue;
                scored.Add(new EntityMatch(
                    Table: entry.Table,
                    Name: entry.Name,
                    Description: entry.Description ?? "",
                    Score: score));
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            return new EntitySemanticRetrieval { Entities = scored.Take(topK).ToList() };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EntitySemanticRetriever] retrieval failed for '{Q}'.", question);
            return new EntitySemanticRetrieval();
        }
    }

    private async Task<IReadOnlyList<EntityVectorEntry>> GetEntityVectorsAsync(CancellationToken cancellationToken)
    {
        // Cache key includes a content-hash so a semantic-layer.json edit invalidates correctly.
        var layerHash = ComputeSemanticLayerHash(_layer);
        var key = CachePrefix + EmbeddingTextVersion + "::" + (_embedder.ModelName ?? "default") + "::" + layerHash;
        if (_cache.TryGetValue<IReadOnlyList<EntityVectorEntry>>(key, out var cached) && cached is not null)
            return cached;

        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entities = _layer.Config.Entities;
            var result = new List<EntityVectorEntry>(entities.Count);
            foreach (var e in entities)
            {
                if (string.IsNullOrEmpty(e.Table)) continue;
                var text = BuildEmbeddingText(e);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var vec = await _embedder.EmbedAsync(text, cancellationToken);
                if (vec.Length == 0) continue;
                result.Add(new EntityVectorEntry(
                    Table: e.Table,
                    Name: e.Name ?? e.Table,
                    Description: e.Description,
                    Vector: vec));
            }
            sw.Stop();
            _logger.LogInformation(
                "[EntitySemanticRetriever] Primed {Count} entity vectors in {Ms}ms (model={Model}).",
                result.Count, sw.ElapsedMilliseconds, _embedder.ModelName);

            _cache.Set(key, (IReadOnlyList<EntityVectorEntry>)result);
            return result;
        }
        finally { _primingLock.Release(); }
    }

    // Embedding text — domain-rich, intentionally different from SchemaSemanticRetriever:
    //   1. Entity name (2x to anchor identity)
    //   2. Description (the high-value natural-language meaning the LLM reads)
    //   3. Synonyms (Arabic + English — directly maps domain vocab → DB entity)
    // No DB-shape signals (column names, FK targets) — that's SchemaSemanticRetriever's job.
    private static string BuildEmbeddingText(EntityDefinition e)
    {
        var parts = new List<string>
        {
            e.Name ?? e.Table,
            e.Name ?? e.Table,
        };
        if (!string.IsNullOrWhiteSpace(e.Description)) parts.Add(e.Description);
        if (e.Synonyms is { Count: > 0 })
            parts.AddRange(e.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.Join(' ', parts);
    }

    // Cheap content-hash of the entity definitions so cache invalidates on a semantic-layer
    // edit. We hash names + descriptions + synonyms (the fields that go into the embedding
    // text); skipping fields the embedding ignores keeps the hash stable for non-relevant edits.
    private static string ComputeSemanticLayerHash(ISemanticLayer layer)
    {
        if (layer?.Config?.Entities is null) return "empty";
        var sb = new System.Text.StringBuilder();
        foreach (var e in layer.Config.Entities)
        {
            sb.Append(e.Table).Append('|').Append(e.Name).Append('|').Append(e.Description ?? "").Append('|');
            if (e.Synonyms is not null) foreach (var s in e.Synonyms) sb.Append(s).Append(',');
            sb.Append(';');
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return System.Convert.ToHexString(hash).Substring(0, 16);
    }

    private sealed record EntityVectorEntry(
        string Table,
        string Name,
        string? Description,
        float[] Vector);
}
