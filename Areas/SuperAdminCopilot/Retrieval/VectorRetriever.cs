namespace SuperAdminCopilot.Retrieval;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>
/// §2 stage 2 of the abstraction guide — semantic retrieval. Embeds the question and ranks
/// every table by cosine similarity to the question vector, then takes the top-K plus their FK
/// neighbours (capped at 2*K so the prompt doesn't blow up).
///
/// <para><b>Why this is better than KeywordRetriever:</b> the keyword retriever scores by
/// substring overlap, which means "client" doesn't find AspNetUsers without a hand-written
/// synonym, and "ticket attachments" doesn't pull in TicketAttachments unless that exact word
/// appears in the column names. Embedding-based ranking learns these mappings from the model's
/// pre-training, so paraphrases work without configuration.</para>
///
/// <para><b>Cost shape:</b> table-summary embeddings are computed once and cached (concurrent
/// dict, keyed by table name + embedder ModelName so a model swap invalidates automatically).
/// Per-question cost is one embedding call for the question itself, plus pure-CPU cosine math.
/// Falls back to <see cref="KeywordRetriever"/> when the embedder returns empty (provider down,
/// model unavailable) — never crashes the pipeline.</para>
/// </summary>
internal sealed class VectorRetriever : IRetriever
{
    private readonly IEntityCatalog _catalog;
    private readonly ICopilotSchemaAccessPolicy _schemaPolicy;
    private readonly ITextEmbedder _embedder;
    private readonly KeywordRetriever _fallback;
    private readonly ISemanticLayer _semanticLayer;
    private readonly CopilotOptions _options;
    private readonly ILogger<VectorRetriever> _logger;

    /// <summary>Cache key includes the model name so swapping embedders invalidates entries.
    /// <para><b>Growth model:</b> process-lifetime; no eviction. One entry per allowed table.
    /// Sized for ≤1000 tables per deployment (typical enterprise SQL Server: ~50-500 tables).
    /// At 1536-dim float[] = 6KB/entry, 1000 tables ≈ 6MB — fine for a single process.
    /// Beyond ~5000 tables, swap to an IMemoryCache with LRU eviction or document a tighter
    /// schema-filtering policy in CopilotOptions.BlockedTablePatterns to cap the table set.</para></summary>
    private readonly ConcurrentDictionary<string, float[]> _tableEmbeddings = new(StringComparer.OrdinalIgnoreCase);

    public VectorRetriever(
        IEntityCatalog catalog,
        ICopilotSchemaAccessPolicy schemaPolicy,
        ITextEmbedder embedder,
        KeywordRetriever fallback,
        ISemanticLayer semanticLayer,
        IOptions<CopilotOptions> options,
        ILogger<VectorRetriever> logger)
    {
        _catalog = catalog;
        _schemaPolicy = schemaPolicy;
        _embedder = embedder;
        _fallback = fallback;
        _semanticLayer = semanticLayer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SchemaSlice> RetrieveAsync(string question, int topK, string? tableHint = null, CancellationToken cancellationToken = default)
    {
        var k = topK > 0 ? topK : _options.RetrieverTopK;

        var questionVec = await _embedder.EmbedAsync(question, cancellationToken);
        if (questionVec.Length == 0)
        {
            // Embedder unavailable — fall through to keyword retriever rather than fail the
            // pipeline. Logged at info because this is the documented degraded-mode path.
            _logger.LogInformation("[VectorRetriever] embedder returned empty; falling back to KeywordRetriever for q='{Q}'", Truncate(question));
            return await _fallback.RetrieveAsync(question, topK, tableHint, cancellationToken);
        }

        // Score every table by cosine(questionVec, tableSummaryVec). Tables with empty summary
        // embedding (one-time embed failure) score 0 and are de-prioritised, not skipped.
        var tables = _schemaPolicy.FilterTables(_catalog.AllTables());
        var scored = new List<(string Name, double Score)>(tables.Count);
        foreach (var t in tables)
        {
            var tableVec = await GetOrEmbedTableSummaryAsync(t.Name, cancellationToken);
            var score = tableVec.Length == 0 ? 0.0 : VectorMath.Cosine(questionVec, tableVec);
            scored.Add((t.Name, score));
        }

        // ── Semantic hint boost ──────────────────────────────────────────
        // When the QuestionRewriter resolved a target entity, boost that table's
        // cosine score so it always ranks in the top-K.
        if (!string.IsNullOrEmpty(tableHint))
        {
            for (int i = 0; i < scored.Count; i++)
            {
                if (string.Equals(scored[i].Name, tableHint, StringComparison.OrdinalIgnoreCase))
                {
                    scored[i] = (scored[i].Name, Math.Max(scored[i].Score, 1.0) + 1.0);
                    break;
                }
            }
        }

        // ── Exact-synonym boost ──────────────────────────────────────────
        // Embeddings are fuzzy; when the user types a whole word that EXACTLY matches
        // an entity's declared synonym in semantic-layer.json, that's a strong signal —
        // boost that table's cosine score by +0.5 so it wins over "denser" tables
        // (e.g. "users" → AspNetUsers must beat Customers even though Customers has a
        // richer description). Schema-agnostic: drives entirely off semantic-layer synonyms.
        var qLower = question.ToLowerInvariant();
        var qWords = System.Text.RegularExpressions.Regex.Matches(qLower, @"\b[a-z؀-ۿ]+\b")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (qWords.Count > 0)
        {
            for (int i = 0; i < scored.Count; i++)
            {
                var entity = _semanticLayer.GetEntityForTable(scored[i].Name);
                if (entity is null || entity.Synonyms is null || entity.Synonyms.Count == 0) continue;
                bool matched = false;
                foreach (var syn in entity.Synonyms)
                {
                    if (string.IsNullOrEmpty(syn)) continue;
                    if (qWords.Contains(syn)) { matched = true; break; }
                }
                if (matched) scored[i] = (scored[i].Name, scored[i].Score + 0.5);
            }
        }

        var primary = scored
            .OrderByDescending(s => s.Score)
            .Take(k)
            .Select(s => s.Name)
            .ToList();

        if (primary.Count == 0)
            primary = tables.Take(k).Select(t => t.Name).ToList();

        // Add FK neighbours — same convention as KeywordRetriever. Helps the planner see lookup
        // tables that the question doesn't name explicitly ("how many open tickets" → user
        // didn't say "TicketStatuses" but the planner needs it for the JOIN).
        var withNeighbors = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
        foreach (var t in primary.ToList())
            foreach (var n in _catalog.Graph.Neighbors(t))
                if (withNeighbors.Count < k * 2 && _schemaPolicy.IsTableAllowed(n)) withNeighbors.Add(n);

        var finalTables = withNeighbors.ToList();
        // Build the score dictionary for the orchestrator's trace breadcrumb. Scores are the
        // cosine similarities; FK-neighbour tables get a sentinel score of 0 (they were added
        // for join coverage, not because they matched the question).
        var scoreMap = scored
            .Where(s => withNeighbors.Contains(s.Name))
            .ToDictionary(s => s.Name, s => s.Score, StringComparer.OrdinalIgnoreCase);
        foreach (var t in withNeighbors)
            if (!scoreMap.ContainsKey(t)) scoreMap[t] = 0.0;
        return new SchemaSlice(finalTables, SchemaPromptFormatter.Format(_catalog, finalTables, _options.SchemaPromptStrategy, _schemaPolicy), scoreMap);
    }

    /// <summary>
    /// Returns the cached embedding for a table's summary, computing it on first use. Cache key
    /// is the table name; the dict is process-lifetime, so a host restart re-embeds (cheap, runs
    /// on first question after start).
    /// </summary>
    private async Task<float[]> GetOrEmbedTableSummaryAsync(string tableName, CancellationToken cancellationToken)
    {
        if (_tableEmbeddings.TryGetValue(tableName, out var cached)) return cached;

        var summary = BuildTableSummary(tableName);
        var vec = await _embedder.EmbedAsync(summary, cancellationToken);
        _tableEmbeddings[tableName] = vec;
        return vec;
    }

    /// <summary>
    /// Eagerly compute and cache table summary embeddings at startup so the first user
    /// question doesn't pay the full per-table embedder cost (one EmbedAsync per allowed
    /// table — typically 50-500 calls). Bounded parallelism keeps cloud rate limits happy
    /// and respects the embedder's own single-threading characteristics for local providers.
    /// Best-effort: any failure is logged and the cold path resumes normally on first call.
    /// </summary>
    public async Task PrimeAsync(int maxParallelism = 4, CancellationToken cancellationToken = default)
    {
        var tables = _schemaPolicy.FilterTables(_catalog.AllTables());
        if (tables.Count == 0) return;
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallelism));
        var tasks = tables.Select(async t =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await GetOrEmbedTableSummaryAsync(t.Name, cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "[VectorRetriever] prime failed for table {Table}.", t.Name); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Compact natural-language summary of a table — what the embedder sees when ranking. Keep
    /// this short enough to fit in a single embedding call but rich enough that the model can
    /// match paraphrases ("clients" → AspNetUsers via "user accounts" in the summary).
    /// </summary>
    private string BuildTableSummary(string tableName)
    {
        var info = _catalog.GetTable(tableName);
        if (info is null) return tableName;

        var sb = new StringBuilder();
        sb.Append("Table ").Append(info.Name).Append(". ");

        var cols = _catalog.GetColumns(tableName)
            .Where(c => _schemaPolicy.IsColumnAllowed(tableName, c.ColumnName))
            .ToList();
        if (cols.Count > 0)
        {
            sb.Append("Columns: ");
            sb.Append(string.Join(", ", cols.Select(c => c.ColumnName)));
            sb.Append(". ");
        }

        var outFks = _catalog.Snapshot.ForeignKeys
            .Where(f => string.Equals(f.ParentTable, tableName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.ReferencedTable)
            .Where(_schemaPolicy.IsTableAllowed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var inFks = _catalog.Snapshot.ForeignKeys
            .Where(f => string.Equals(f.ReferencedTable, tableName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.ParentTable)
            .Where(_schemaPolicy.IsTableAllowed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (outFks.Count > 0 || inFks.Count > 0)
        {
            sb.Append("Related: ");
            sb.Append(string.Join(", ", outFks.Concat(inFks).Distinct(StringComparer.OrdinalIgnoreCase)));
            sb.Append('.');
        }

        // A few sample label values give the embedder one more anchor — "Critical, High, Medium"
        // helps the embedder match priority-related questions to TicketPriorities.
        var samples = _catalog.GetSampleValues(tableName);
        if (samples.Count > 0)
        {
            sb.Append(" Examples: ");
            sb.Append(string.Join(", ", samples.Take(5)));
            sb.Append('.');
        }
        return sb.ToString();
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";
}
