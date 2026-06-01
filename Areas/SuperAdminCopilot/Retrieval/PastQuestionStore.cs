namespace SuperAdminCopilot.Retrieval;

using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Learning RAG over past successful traces. Every question that produces clean SQL gets
/// (1) embedded by the host's Rag-workload embedder and (2) persisted alongside the trace
/// (already happens via <c>CopilotTraceHistory.QuestionEmbeddingJson</c> — we just leverage it).
///
/// <para>Before each new planner call, we cosine-rank the question against the past corpus
/// (≥ <see cref="MinSimilarity"/>, top-K = 3) and inject the matched (question, SQL) pairs into
/// the planner prompt as worked examples — supplementing the static <see cref="FewShotExampleStore"/>.
/// Common paraphrases of past questions resolve in &lt;1s without re-running the LLM.</para>
///
/// <para>Cache: in-memory snapshot of the past corpus, refreshed every <see cref="CacheTtl"/>.
/// Avoids hitting the DB on every question. Eviction = TTL expiry; nothing fancier needed.</para>
/// </summary>
public interface IPastQuestionStore
{
    /// <summary>Find the K most-similar past questions whose embedding cosine ≥ minSimilarity. Empty when the embedder isn't available.</summary>
    Task<IReadOnlyList<PastQuestionMatch>> FindSimilarAsync(string question, int topK, float minSimilarity, CancellationToken cancellationToken = default);

    /// <summary>Evict the in-memory corpus cache. Call before benchmark/eval runs so the corpus
    /// is rebuilt from the freshest DB state instead of any prior session's snapshot.</summary>
    void InvalidateCache();
}

public sealed record PastQuestionMatch(string Question, string GeneratedScript, float Similarity);

internal sealed class PastQuestionStore : IPastQuestionStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string CacheKey = "SuperAdminCopilot.PastQuestionCorpus";

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly CopilotOptions _options;
    private readonly ILogger<PastQuestionStore> _logger;

    public PastQuestionStore(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ITextEmbedder embedder,
        IMemoryCache cache,
        IOptions<CopilotOptions> options,
        ILogger<PastQuestionStore> logger)
    {
        _dbFactory = dbFactory;
        _embedder = embedder;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);
        _logger.LogInformation("[PastQuestionStore] Corpus cache invalidated by caller.");
    }

    public async Task<IReadOnlyList<PastQuestionMatch>> FindSimilarAsync(string question, int topK, float minSimilarity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || topK <= 0)
            return Array.Empty<PastQuestionMatch>();

        var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
        if (queryVec.Length == 0)
        {
            _logger.LogDebug("[PastQuestionStore] Embedder returned empty vector — skipping RAG.");
            return Array.Empty<PastQuestionMatch>();
        }

        var corpus = await GetCorpusAsync(cancellationToken);
        if (corpus.Count == 0) return Array.Empty<PastQuestionMatch>();

        var modelName = _embedder.ModelName ?? "";
        var matches = new List<PastQuestionMatch>(corpus.Count);
        foreach (var entry in corpus)
        {
            // Vectors from different embedders aren't comparable. Skip cross-model matches.
            if (!string.Equals(entry.ModelName, modelName, StringComparison.OrdinalIgnoreCase)) continue;

            var sim = VectorMath.Cosine(queryVec, entry.Vector);
            if (sim < minSimilarity) continue;

            // Don't echo the user's own re-asked question back at them as an example.
            if (string.Equals(entry.Question, question, StringComparison.OrdinalIgnoreCase)) continue;

            matches.Add(new PastQuestionMatch(entry.Question, entry.GeneratedScript, sim));
        }

        // Dedupe by (Question, GeneratedScript): the corpus often contains retries of the same
        // case, which surfaced as e.g. "Earliest ticket created date" appearing twice in the
        // hits list. Keep the highest-similarity copy of each (question, sql) pair.
        var result = matches
            .GroupBy(m => (m.Question, m.GeneratedScript), new QuestionScriptComparer())
            .Select(g => g.OrderByDescending(m => m.Similarity).First())
            .OrderByDescending(m => m.Similarity)
            .Take(topK)
            .ToList();

        // Hit-rate telemetry — tagged [copilot.rag_lookup] for log-based aggregation. Operators
        // can answer "what % of questions return at least one past-question RAG hit?" without
        // any code change. Pair with the verified-query log line in VerifiedQueryStore.
        _logger.LogInformation("[copilot.rag_lookup] source=past_question outcome={Outcome} hits={Hits} topScore={Score:F2}",
            result.Count == 0 ? "miss" : "hit",
            result.Count,
            result.Count == 0 ? 0f : result[0].Similarity);
        return result;
    }

    private sealed class QuestionScriptComparer : IEqualityComparer<(string Question, string GeneratedScript)>
    {
        public bool Equals((string Question, string GeneratedScript) x, (string Question, string GeneratedScript) y) =>
            string.Equals(x.Question, y.Question, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.GeneratedScript, y.GeneratedScript, StringComparison.Ordinal);
        public int GetHashCode((string Question, string GeneratedScript) obj) =>
            HashCode.Combine(
                obj.Question?.ToLowerInvariant(),
                obj.GeneratedScript);
    }

    private async Task<IReadOnlyList<CorpusEntry>> GetCorpusAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<IReadOnlyList<CorpusEntry>>(CacheKey, out var cached) && cached is not null)
            return cached;

        // Filter out cross-embedder traces at the SQL layer instead of in C#. Vectors from a
        // different embedder are never comparable; previously they loaded into memory + got
        // deserialised + got skipped at compare-time — pure waste. With this filter the corpus
        // size only counts genuinely-comparable rows.
        var currentEmbedderModel = _embedder.ModelName ?? string.Empty;
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await ctx.Set<CopilotTraceHistory>()
            .Where(t => t.QuestionEmbeddingJson != null
                     && t.GeneratedScript != null
                     && t.ErrorMessage == null
                     && t.EmbeddingModelName == currentEmbedderModel)
            .OrderByDescending(t => t.Id)
            .Take(2000)        // bound the cache; older traces drop off the back
            .Select(t => new
            {
                t.Question,
                t.GeneratedScript,
                t.QuestionEmbeddingJson,
                t.EmbeddingModelName,
            })
            .ToListAsync(cancellationToken);

        // Degenerate-script filter: when ONE GeneratedScript shows up in N+ traces it almost
        // always means the planner collapsed many distinct questions onto the same wrong SQL
        // (the "default count-by-status template" failure mode). Including those traces in the
        // RAG corpus poisons future retrievals — questions that should diverge keep finding
        // the same dominant shape as their top "past question that worked" hit.
        var degenerateThreshold = _options.PastQuestionRagDegenerateScriptThreshold;
        var degenerateScripts = new HashSet<string>(StringComparer.Ordinal);
        if (degenerateThreshold > 0)
        {
            foreach (var grp in rows.GroupBy(r => r.GeneratedScript ?? "", StringComparer.Ordinal))
            {
                if (grp.Key.Length > 0 && grp.Count() >= degenerateThreshold)
                    degenerateScripts.Add(grp.Key);
            }
            if (degenerateScripts.Count > 0)
                _logger.LogInformation(
                    "[PastQuestionStore] Excluding {Count} degenerate SQL script(s) from RAG corpus (each appears in >= {Threshold} traces).",
                    degenerateScripts.Count, degenerateThreshold);
        }

        var corpus = new List<CorpusEntry>(rows.Count);
        int corruptCount = 0;
        foreach (var r in rows)
        {
            if (degenerateScripts.Contains(r.GeneratedScript ?? "")) continue;
            try
            {
                var vec = System.Text.Json.JsonSerializer.Deserialize<float[]>(r.QuestionEmbeddingJson!);
                if (vec is null || vec.Length == 0) continue;
                corpus.Add(new CorpusEntry(r.Question, r.GeneratedScript ?? "", vec, r.EmbeddingModelName!));
            }
            catch (Exception ex)
            {
                // Bad embedding JSON — skip the row, keep building the corpus. Count surfaces
                // a debug-level summary line below so corrupted rows become visible during
                // log triage without spamming a line per row.
                corruptCount++;
                _logger.LogTrace(ex, "[PastQuestionStore] Skipped a row with malformed embedding JSON.");
            }
        }
        if (corruptCount > 0)
            _logger.LogDebug("[PastQuestionStore] Skipped {Count} row(s) with malformed embedding JSON during corpus load.", corruptCount);

        _cache.Set(CacheKey, (IReadOnlyList<CorpusEntry>)corpus, CacheTtl);
        _logger.LogInformation("[PastQuestionStore] Cached {Count} past questions for RAG (TTL {Ttl}m).", corpus.Count, CacheTtl.TotalMinutes);
        return corpus;
    }

    private sealed record CorpusEntry(string Question, string GeneratedScript, float[] Vector, string ModelName);
}
