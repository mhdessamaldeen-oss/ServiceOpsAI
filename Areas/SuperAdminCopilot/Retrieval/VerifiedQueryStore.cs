namespace SuperAdminCopilot.Retrieval;

using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Hand-curated (question, SQL) catalog. When an incoming question's embedding cosine-matches
/// a stored verified question above the configured threshold, the orchestrator uses the
/// verified SQL directly and skips the LLM altogether. This is the Snowflake "Verified
/// Queries" pattern — the escape hatch for questions you absolutely care about getting right
/// every time, regardless of model size or local-LLM jitter.
///
/// <para>The file is JSON, domain-agnostic, and 100% editable by admins without code changes.
/// Empty list = feature inactive (matcher returns no hits, pipeline falls through normally).</para>
/// </summary>
public interface IVerifiedQueryStore
{
    bool IsAvailable { get; }
    int Count { get; }
    IReadOnlyList<VerifiedQuery> All { get; }
    /// <summary>Force re-read from disk (admin UI calls after edit).</summary>
    void Reload();
}

public sealed class VerifiedQuery
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public required string Sql { get; init; }

    // Paraphrases of <see cref="Question"/>. Each variant is embedded separately and
    // matched independently — letting one entry cover many natural-language phrasings of
    // the same intent without authoring a duplicate SQL block. Empty/null = canonical only.
    public List<string>? QuestionVariants { get; init; }

    // Canonical shape code (SEL / FLT / CNT / AGG / GRP / TOP / JOI / ANT / PER / MAX / MIN
    // / DIS / REF / TRD / PCT / COH / ANM / FUN / CUM). Drives per-shape coverage reports.
    public string? Shape { get; init; }

    // Provenance — answers "who blessed this SQL and when?" so a stale entry can be audited.
    public string? VerifiedAt { get; init; }
    public string? VerifiedBy { get; init; }

    // When true, this entry is eligible for the "example questions" menu the UI can show
    // first-time users. Curated examples should be representative, not exhaustive.
    public bool? UseAsOnboarding { get; init; }

    // Per-entry override of the default similarity threshold (admin can require a closer
    // match for specific entries, or loosen for fuzzy ones).
    public double? MinSimilarity { get; init; }
    public List<string>? Tags { get; init; }
    public string? Description { get; init; }
}

internal sealed class VerifiedQueryStore : IVerifiedQueryStore
{
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<VerifiedQueryStore> _logger;
    private readonly object _gate = new();
    private List<VerifiedQuery> _items = new();
    private bool _available;

    public VerifiedQueryStore(IOptions<CopilotOptions> options, ILogger<VerifiedQueryStore> logger)
    {
        _options = options;
        _logger = logger;
        TryLoad();
    }

    public bool IsAvailable => _available;
    public int Count => _items.Count;
    public IReadOnlyList<VerifiedQuery> All => _items;

    public void Reload() { lock (_gate) TryLoad(); }

    private void TryLoad()
    {
        var path = ResolvePath(_options.Value.VerifiedQueriesPath);
        if (!File.Exists(path))
        {
            _logger.LogInformation("[SuperAdminCopilot] Verified-queries file missing at {Path} — feature inactive.", path);
            _items = new();
            _available = false;
            return;
        }
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<VerifiedQueriesFile>(json, DeserializerOptions);
            _items = doc?.Queries?.Where(IsValid).ToList() ?? new();
            _available = _items.Count > 0;
            _logger.LogInformation("[SuperAdminCopilot] Verified-queries loaded: {Count} entries.", _items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SuperAdminCopilot] Verified-queries: load failed at {Path}", path);
            _items = new();
            _available = false;
        }
    }

    private static bool IsValid(VerifiedQuery q) =>
        !string.IsNullOrWhiteSpace(q.Id)
        && !string.IsNullOrWhiteSpace(q.Question)
        && !string.IsNullOrWhiteSpace(q.Sql);

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);

    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class VerifiedQueriesFile
    {
        public string? Version { get; set; }
        public List<VerifiedQuery>? Queries { get; set; }
    }
}

// ── Matcher ────────────────────────────────────────────────────────────────────────

public sealed record VerifiedMatch(VerifiedQuery Query, float Similarity);

public interface IVerifiedQueryMatcher
{
    bool IsAvailable { get; }
    /// <summary>Returns the highest-similarity verified query above the threshold, or null
    /// when no match qualifies.</summary>
    Task<VerifiedMatch?> MatchAsync(string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the highest cosine similarity to any catalog entry, REGARDLESS of threshold.
    /// Used by the ScopeConfidenceGate to decide whether a question is even remotely close to
    /// something we've seen before — separate from the strict ≥ 0.90 catalog-trust threshold
    /// MatchAsync applies. Returns 0 when the matcher is unavailable or the catalog is empty.
    /// </summary>
    Task<float> MaxSimilarityAsync(string question, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="topK"/> closest verified-query matches whose cosine
    /// similarity meets <paramref name="minSimilarity"/>. Used by SpecExtractor as few-shot
    /// fuel — when no past-question RAG match exists, the curated VQ catalog acts as a
    /// safety net of high-quality (question, SQL) examples to anchor the planner.
    ///
    /// <para>Distinct from <see cref="MatchAsync"/>: MatchAsync uses the strict per-entry
    /// trust threshold (typically ≥ 0.90) to decide whether the catalog can short-circuit
    /// the LLM. FindTopAsync uses a softer floor (typically 0.65-0.75) — the matches won't
    /// run AS the answer, they'll be shown to the LLM as worked examples of similar shapes.
    /// Entries are deduplicated by their canonical question so the same entry doesn't appear
    /// twice when both its canonical and a variant score highly.</para>
    /// </summary>
    Task<IReadOnlyList<VerifiedMatch>> FindTopAsync(string question, int topK, float minSimilarity, CancellationToken cancellationToken = default);
}

internal sealed class VerifiedQueryMatcher : IVerifiedQueryMatcher
{
    private const string CachePrefix = "super-admin-copilot::verified-vectors::";
    // Bumped to v2 when QuestionVariants[] was introduced — invalidates pre-variant caches
    // so existing deployments re-embed including the new paraphrases.
    private const string EmbeddingTextVersion = "v2";

    private readonly IVerifiedQueryStore _store;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<VerifiedQueryMatcher> _logger;
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    public VerifiedQueryMatcher(
        IVerifiedQueryStore store,
        ITextEmbedder embedder,
        IMemoryCache cache,
        IOptions<CopilotOptions> options,
        ILogger<VerifiedQueryMatcher> logger)
    {
        _store = store;
        _embedder = embedder;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable => _store.IsAvailable && !string.IsNullOrEmpty(_embedder.ModelName);

    public async Task<VerifiedMatch?> MatchAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable) return null;
        try
        {
            var vectors = await GetVerifiedVectorsAsync(cancellationToken);
            if (vectors.Count == 0) return null;
            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return null;

            VerifiedMatch? best = null;
            foreach (var (vq, vec) in vectors)
            {
                if (vec.Length != queryVec.Length) continue;
                var score = Cosine(queryVec, vec);
                var threshold = (float)(vq.MinSimilarity ?? _options.Value.VerifiedQueryMinSimilarity);
                if (score < threshold) continue;
                if (best is null || score > best.Similarity) best = new VerifiedMatch(vq, score);
            }
            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VerifiedQueryMatcher] match failed for '{Q}'", question);
            return null;
        }
    }

    public async Task<float> MaxSimilarityAsync(string question, CancellationToken cancellationToken = default)
    {
        // Returns the raw best cosine similarity across the catalog with NO threshold filter.
        // The ScopeConfidenceGate uses this to decide whether a question is even loosely close
        // to anything we've curated — a much lower bar than MatchAsync's strict trust threshold.
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable) return 0f;
        try
        {
            var vectors = await GetVerifiedVectorsAsync(cancellationToken);
            if (vectors.Count == 0) return 0f;
            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return 0f;

            float best = 0f;
            foreach (var (_, vec) in vectors)
            {
                if (vec.Length != queryVec.Length) continue;
                var score = Cosine(queryVec, vec);
                if (score > best) best = score;
            }
            return best;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VerifiedQueryMatcher] MaxSimilarity failed for '{Q}'", question);
            return 0f;
        }
    }

    public async Task<IReadOnlyList<VerifiedMatch>> FindTopAsync(string question, int topK, float minSimilarity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable || topK <= 0) return Array.Empty<VerifiedMatch>();
        try
        {
            var vectors = await GetVerifiedVectorsAsync(cancellationToken);
            if (vectors.Count == 0) return Array.Empty<VerifiedMatch>();
            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return Array.Empty<VerifiedMatch>();

            // Score every vector once, but dedupe by VerifiedQuery.Id afterwards — each
            // entry contributes its canonical + each questionVariant, so the same entry
            // can appear multiple times in the vector list. Keep only the best score per id.
            var bestById = new Dictionary<string, VerifiedMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var (vq, vec) in vectors)
            {
                if (vec.Length != queryVec.Length) continue;
                var score = Cosine(queryVec, vec);
                if (score < minSimilarity) continue;
                if (!bestById.TryGetValue(vq.Id, out var existing) || score > existing.Similarity)
                    bestById[vq.Id] = new VerifiedMatch(vq, score);
            }
            return bestById.Values
                .OrderByDescending(m => m.Similarity)
                .Take(topK)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VerifiedQueryMatcher] FindTop failed for '{Q}'", question);
            return Array.Empty<VerifiedMatch>();
        }
    }

    private async Task<IReadOnlyList<(VerifiedQuery, float[])>> GetVerifiedVectorsAsync(CancellationToken cancellationToken)
    {
        // Cache key must change when an entry's variant list changes (otherwise an admin
        // could add a paraphrase, hit Reload, and we'd serve the stale embedding set).
        var variantTotal = _store.All.Sum(v => v.QuestionVariants?.Count ?? 0);
        var key = $"{CachePrefix}{EmbeddingTextVersion}::{_embedder.ModelName ?? "default"}::{_store.Count}::{variantTotal}";
        if (_cache.TryGetValue<IReadOnlyList<(VerifiedQuery, float[])>>(key, out var cached) && cached is not null)
            return cached;
        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;
            // Each entry contributes one vector for its canonical Question plus one vector
            // per QuestionVariants[] paraphrase. The matcher returns the best score among
            // them, so a single entry can cover several natural phrasings without a separate
            // SQL block per variant.
            var result = new List<(VerifiedQuery, float[])>(_store.Count);
            foreach (var vq in _store.All)
            {
                var canonical = await _embedder.EmbedAsync(vq.Question, cancellationToken);
                if (canonical.Length > 0) result.Add((vq, canonical));
                if (vq.QuestionVariants is { Count: > 0 })
                {
                    foreach (var variant in vq.QuestionVariants)
                    {
                        if (string.IsNullOrWhiteSpace(variant)) continue;
                        var vec = await _embedder.EmbedAsync(variant, cancellationToken);
                        if (vec.Length > 0) result.Add((vq, vec));
                    }
                }
            }
            _cache.Set(key, (IReadOnlyList<(VerifiedQuery, float[])>)result);
            _logger.LogInformation("[VerifiedQueryMatcher] primed {N} verified-query vectors.", result.Count);
            return result;
        }
        finally { _primingLock.Release(); }
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
