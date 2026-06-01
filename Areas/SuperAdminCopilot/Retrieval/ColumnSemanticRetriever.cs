namespace SuperAdminCopilot.Retrieval;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Schema;

/// <summary>
/// Column-level semantic retriever. Counterpart to <see cref="SchemaSemanticRetriever"/>, which
/// matches a question against TABLE descriptions. This one matches against individual COLUMN
/// descriptions so the LLM gets explicit "the user said 'income' → the semantically closest
/// columns are Bills.TotalAmount and Bills.PaidAmount" hints instead of guessing from a wall of
/// column names.
///
/// <para>Lazy prime: on first call the retriever embeds every (Table, Column) pair in the visible
/// catalog. Stored in <see cref="IMemoryCache"/> keyed by <c>(EmbeddingTextVersion, modelName, schemaHash)</c>.
/// Subsequent questions reuse the warm cache; sub-50 ms cosine sweep against the corpus.
/// When the operator regenerates <c>schema-inferred.json</c>, the <see cref="ISchemaKnowledge.SchemaHash"/>
/// changes, the cache key changes, and the next request automatically re-primes. NO BUTTON.</para>
///
/// <para>Embedder fail-open invariant: when the embedder is unavailable (<see cref="IsAvailable"/>
/// false) OR throws, the retriever returns an empty result. Caller falls back to the planner's
/// normal table-slice / DerivedMetricRule paths.</para>
/// </summary>
public interface IColumnSemanticRetriever
{
    /// <summary>True when both an embedder and schema knowledge are available; false → empty results.</summary>
    bool IsAvailable { get; }

    /// <summary>Find the top-K columns whose embedding text cosine-matches the question, above the
    /// minimum-similarity threshold. Restricts the search to columns belonging to one of
    /// <paramref name="limitToTables"/> when provided (typical case — the upstream
    /// <see cref="ISchemaSemanticRetriever"/> already picked the relevant tables).</summary>
    Task<ColumnSemanticRetrieval> RetrieveAsync(
        string question,
        IReadOnlyCollection<string>? limitToTables = null,
        int topK = 8,
        float minSimilarity = 0.50f,
        CancellationToken cancellationToken = default);
}

public sealed class ColumnSemanticRetrieval
{
    public IReadOnlyList<ColumnMatch> Columns { get; init; } = Array.Empty<ColumnMatch>();
}

/// <summary>One column match. <see cref="TableDotColumn"/> is the qualified identifier the
/// planner consumes; <see cref="Description"/> is the natural-language meaning the LLM reads.</summary>
public sealed record ColumnMatch(
    string TableDotColumn,
    string Description,
    string SqlType,
    float Score);

internal sealed class ColumnSemanticRetriever : IColumnSemanticRetriever
{
    // Bump EmbeddingTextVersion when BuildEmbeddingText changes — invalidates the cache so
    // stale vectors don't survive a deploy. Same cache-versioning idiom as SchemaSemanticRetriever.
    private const string CachePrefix = "super-admin-copilot::column-vectors::";
    private const string EmbeddingTextVersion = "v1";

    private readonly ISchemaKnowledge _knowledge;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotOptions> _options;
    private readonly ILogger<ColumnSemanticRetriever> _logger;
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    public ColumnSemanticRetriever(
        ISchemaKnowledge knowledge,
        ITextEmbedder embedder,
        IMemoryCache cache,
        Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotOptions> options,
        ILogger<ColumnSemanticRetriever> logger)
    {
        _knowledge = knowledge;
        _embedder = embedder;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName) && _knowledge.IsAvailable;

    public async Task<ColumnSemanticRetrieval> RetrieveAsync(
        string question,
        IReadOnlyCollection<string>? limitToTables = null,
        int topK = 8,
        float minSimilarity = 0.50f,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable)
            return new ColumnSemanticRetrieval();

        try
        {
            var columnVecs = await GetColumnVectorsAsync(cancellationToken);
            if (columnVecs.Count == 0) return new ColumnSemanticRetrieval();

            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return new ColumnSemanticRetrieval();

            HashSet<string>? tableFilter = null;
            if (limitToTables is not null && limitToTables.Count > 0)
                tableFilter = new HashSet<string>(limitToTables, StringComparer.OrdinalIgnoreCase);

            var scored = new List<ColumnMatch>(columnVecs.Count);
            foreach (var entry in columnVecs)
            {
                if (tableFilter is not null && !tableFilter.Contains(entry.TableName)) continue;
                if (entry.Vector.Length != queryVec.Length) continue;
                var score = VectorMath.Cosine(queryVec, entry.Vector);
                if (score < minSimilarity) continue;
                scored.Add(new ColumnMatch(
                    TableDotColumn: entry.TableName + "." + entry.ColumnName,
                    Description: entry.Description ?? "",
                    SqlType: entry.SqlType ?? "",
                    Score: score));
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            return new ColumnSemanticRetrieval
            {
                Columns = scored.Take(topK).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ColumnSemanticRetriever] retrieval failed for '{Q}'.", question);
            return new ColumnSemanticRetrieval();
        }
    }

    private async Task<IReadOnlyList<ColumnVectorEntry>> GetColumnVectorsAsync(CancellationToken cancellationToken)
    {
        var key = CachePrefix + EmbeddingTextVersion + "::" + (_embedder.ModelName ?? "default") + "::" + (_knowledge.SchemaHash ?? "");
        if (_cache.TryGetValue<IReadOnlyList<ColumnVectorEntry>>(key, out var cached) && cached is not null)
            return cached;

        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hidden = _options.CurrentValue.RetrieverHiddenTables ?? new List<string>();
            var hiddenSet = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
            var visibleTables = _knowledge.AllTables.Where(t => !hiddenSet.Contains(t.Name)).ToList();

            var result = new List<ColumnVectorEntry>(capacity: 512);
            foreach (var table in visibleTables)
            {
                foreach (var col in table.Columns)
                {
                    if (string.IsNullOrEmpty(col.Name)) continue;
                    // PII columns the policy already blocks from SELECT do not belong in the
                    // retrieval pool either — surfacing them would hint at sensitive data.
                    if (col.IsPii) continue;
                    var text = BuildEmbeddingText(table, col);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var vec = await _embedder.EmbedAsync(text, cancellationToken);
                    if (vec.Length == 0) continue;
                    result.Add(new ColumnVectorEntry(
                        TableName: table.Name,
                        ColumnName: col.Name,
                        SqlType: col.Type,
                        Description: ResolveDescription(table, col),
                        Vector: vec));
                }
            }
            sw.Stop();
            _logger.LogInformation(
                "[ColumnSemanticRetriever] Primed {Count} column vectors across {Tables} tables in {Ms}ms (model={Model}).",
                result.Count, visibleTables.Count, sw.ElapsedMilliseconds, _embedder.ModelName);

            _cache.Set(key, (IReadOnlyList<ColumnVectorEntry>)result);
            return result;
        }
        finally { _primingLock.Release(); }
    }

    // Embedding text — three signal sources concatenated:
    //   1. Qualified column identity (Table.Column) — repeated 2x to anchor on name.
    //   2. Operator-curated Description from schema-overrides.json (high-value semantic content).
    //   3. Operator-curated Synonyms list (alternate phrasings users say for this column).
    // Type is included parenthetically — gives the embedder a soft signal about what kind
    // of data the column holds (decimal → money/quantity; nvarchar → text; datetime → date).
    //
    // Sample values are deliberately NOT included for fact-table columns — they add noise and
    // bias the cosine toward specific row instances rather than the column's semantic role.
    // Lookup-table label columns are an exception (the "Open/Closed/Resolved" values DO carry
    // semantic weight for routing); we include up to 5 of those.
    private static string BuildEmbeddingText(InferredTable table, InferredColumn col)
    {
        var parts = new List<string>
        {
            $"{table.Name}.{col.Name}",
            $"{table.Name}.{col.Name}",
            $"({col.Type})",
        };
        if (!string.IsNullOrWhiteSpace(col.Description)) parts.Add(col.Description);
        if (col.Synonyms is { Count: > 0 })
            parts.AddRange(col.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (table.Flags.IsLookup
            && string.Equals(col.Role, "label", StringComparison.OrdinalIgnoreCase)
            && col.SampleValues is { Count: > 0 })
            parts.AddRange(col.SampleValues.Take(5));
        return string.Join(' ', parts);
    }

    private static string ResolveDescription(InferredTable table, InferredColumn col)
    {
        if (!string.IsNullOrWhiteSpace(col.Description)) return col.Description;
        // Build a useful fallback from name + type + role so the prompt-side hint reads cleanly
        // even when no human description exists yet.
        var rolePart = !string.IsNullOrEmpty(col.Role) ? $" [{col.Role}]" : "";
        return $"{col.Name} of {table.Name} ({col.Type}){rolePart}";
    }

    private sealed record ColumnVectorEntry(
        string TableName,
        string ColumnName,
        string SqlType,
        string? Description,
        float[] Vector);
}
