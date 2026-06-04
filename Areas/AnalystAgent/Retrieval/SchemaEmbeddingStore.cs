namespace AnalystAgent.Retrieval;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>Status of one persisted embedding index (table / column / entity).</summary>
public sealed record SchemaEmbeddingKindStatus(
    string Kind, string? Model, string? SchemaHash, int Count, DateTime? GeneratedAt, bool Stale);

/// <summary>
/// Persists the schema-embedding indexes (table / column / entity vectors) to disk so they survive an
/// app restart and are NOT re-embedded on every boot — and so the embedding model (bge-m3) need not sit
/// in VRAM at runtime competing with the generator. Vectors are stamped with the EMBEDDING MODEL + the
/// SCHEMA HASH; a mismatch on load = STALE (schema or model changed) → the retriever regenerates, and the
/// "Generate Schema Embeddings" admin action forces a clean rebuild. Files live next to schema-inferred.json.
/// </summary>
public interface ISchemaEmbeddingStore
{
    /// <summary>Persisted vectors for an index kind, but ONLY if the stored (model, schemaHash) still
    /// match the current ones; otherwise null (stale/absent → caller regenerates).</summary>
    IReadOnlyDictionary<string, float[]>? Load(string kind, string model, string schemaHash);
    void Save(string kind, string model, string schemaHash, IReadOnlyDictionary<string, float[]> vectors);
    /// <summary>Drop all persisted indexes (the regenerate button) so the next prime re-embeds.</summary>
    void Clear();
    IReadOnlyList<SchemaEmbeddingKindStatus> GetStatus(string currentModel, string currentSchemaHash, IReadOnlyList<string> kinds);
}

internal sealed class SchemaEmbeddingStore : ISchemaEmbeddingStore
{
    public const string KindTables = "tables";
    public const string KindColumns = "columns";
    public const string KindEntities = "entities";

    private readonly string _dir;
    private readonly ILogger<SchemaEmbeddingStore> _logger;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public SchemaEmbeddingStore(ILogger<SchemaEmbeddingStore> logger)
    {
        _logger = logger;
        // Alongside schema-inferred.json (same resolution as SchemaKnowledge.ResolvePath).
        _dir = Path.Combine(Directory.GetCurrentDirectory(), "Areas", "AnalystAgent", "Configuration", "embeddings");
    }

    private string PathFor(string kind) => Path.Combine(_dir, $"schema-emb-{kind}.json");

    private sealed class Payload
    {
        public string? Model { get; set; }
        public string? SchemaHash { get; set; }
        public DateTime GeneratedAt { get; set; }
        public Dictionary<string, float[]> Vectors { get; set; } = new();
    }

    public IReadOnlyDictionary<string, float[]>? Load(string kind, string model, string schemaHash)
    {
        try
        {
            var p = PathFor(kind);
            if (!File.Exists(p)) return null;
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(p), Json);
            if (payload is null) return null;
            if (!string.Equals(payload.Model, model, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(payload.SchemaHash, schemaHash, StringComparison.Ordinal))
            {
                _logger.LogInformation("[SchemaEmbeddingStore] '{Kind}' on disk is STALE (model/schema changed) — regenerating.", kind);
                return null;
            }
            _logger.LogInformation("[SchemaEmbeddingStore] loaded {Count} '{Kind}' vectors from disk (model={Model}).", payload.Vectors.Count, kind, model);
            return payload.Vectors;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[SchemaEmbeddingStore] load '{Kind}' failed.", kind); return null; }
    }

    public void Save(string kind, string model, string schemaHash, IReadOnlyDictionary<string, float[]> vectors)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                var payload = new Payload
                {
                    Model = model, SchemaHash = schemaHash, GeneratedAt = DateTime.UtcNow,
                    Vectors = new Dictionary<string, float[]>(vectors),
                };
                File.WriteAllText(PathFor(kind), JsonSerializer.Serialize(payload, Json));
                _logger.LogInformation("[SchemaEmbeddingStore] saved {Count} '{Kind}' vectors to disk (model={Model}).", vectors.Count, kind, model);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[SchemaEmbeddingStore] save '{Kind}' failed.", kind); }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
                if (Directory.Exists(_dir))
                    foreach (var f in Directory.GetFiles(_dir, "schema-emb-*.json")) File.Delete(f);
            _logger.LogInformation("[SchemaEmbeddingStore] cleared persisted indexes.");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[SchemaEmbeddingStore] clear failed."); }
    }

    public IReadOnlyList<SchemaEmbeddingKindStatus> GetStatus(string currentModel, string currentSchemaHash, IReadOnlyList<string> kinds)
    {
        var list = new List<SchemaEmbeddingKindStatus>();
        foreach (var kind in kinds)
        {
            var p = PathFor(kind);
            if (!File.Exists(p)) { list.Add(new(kind, null, null, 0, null, true)); continue; }
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(p), Json);
                var stale = payload is null
                    || !string.Equals(payload.Model, currentModel, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(payload.SchemaHash, currentSchemaHash, StringComparison.Ordinal);
                list.Add(new(kind, payload?.Model, payload?.SchemaHash, payload?.Vectors.Count ?? 0, payload?.GeneratedAt, stale));
            }
            catch { list.Add(new(kind, null, null, 0, null, true)); }
        }
        return list;
    }
}
