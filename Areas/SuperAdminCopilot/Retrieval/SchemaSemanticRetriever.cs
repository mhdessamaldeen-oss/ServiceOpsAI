namespace SuperAdminCopilot.Retrieval;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Schema;

/// <summary>
/// Embedding-based table retriever for the new copilot flow. At first use it embeds every
/// table in <see cref="ISchemaKnowledge"/> (table name + description + label/key column names).
/// Per-question retrieval is one embed of the question plus a cosine sweep — sub-50ms after
/// warmup.
///
/// <para>The new <c>SpecExtractor</c> reads from this to narrow the LLM's schema prompt to
/// only the relevant tables.</para>
/// </summary>
public interface ISchemaSemanticRetriever
{
    bool IsAvailable { get; }
    Task<SchemaSemanticRetrieval> RetrieveAsync(string question, int topK = 3, CancellationToken cancellationToken = default);
}

public sealed class SchemaSemanticRetrieval
{
    public IReadOnlyList<TableMatch> Tables { get; init; } = Array.Empty<TableMatch>();
}

public sealed record TableMatch(InferredTable Table, float Score);

internal sealed class SchemaSemanticRetriever : ISchemaSemanticRetriever
{
    // Bump EmbeddingTextVersion whenever BuildEmbeddingText changes — invalidates the cache
    // so existing vectors don't survive across deploy and mask the new ranking.
    private const string CachePrefix = "super-admin-copilot::schema-vectors::";
    // v4 — fact-table phrasing no longer adds "records entries items rows" + extra name
    // repetition. Those generic English triggers gave fact tables a structural edge that
    // made the retriever bias toward whichever big table dominated the catalog (Tickets in
    // the current deployment) regardless of question content.
    private const string EmbeddingTextVersion = "v4";

    private readonly ISchemaKnowledge _knowledge;
    private readonly ITextEmbedder _embedder;
    private readonly IMemoryCache _cache;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotOptions> _options;
    private readonly ILogger<SchemaSemanticRetriever> _logger;
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    public SchemaSemanticRetriever(
        ISchemaKnowledge knowledge,
        ITextEmbedder embedder,
        IMemoryCache cache,
        Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotOptions> options,
        ILogger<SchemaSemanticRetriever> logger)
    {
        _knowledge = knowledge;
        _embedder = embedder;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName) && _knowledge.IsAvailable;

    public async Task<SchemaSemanticRetrieval> RetrieveAsync(
        string question, int topK = 3, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailable)
            return new SchemaSemanticRetrieval();

        try
        {
            var tableVecs = await GetTableVectorsAsync(cancellationToken);
            if (tableVecs.Count == 0) return new SchemaSemanticRetrieval();

            var queryVec = await _embedder.EmbedAsync(question, cancellationToken);
            if (queryVec.Length == 0) return new SchemaSemanticRetrieval();

            var scored = new List<TableMatch>(tableVecs.Count);
            foreach (var (table, vec) in tableVecs)
            {
                if (vec.Length != queryVec.Length) continue;
                scored.Add(new TableMatch(table, Cosine(queryVec, vec)));
            }
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            return new SchemaSemanticRetrieval { Tables = scored.Take(topK).ToList() };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SchemaSemanticRetriever] retrieval failed for '{Q}'.", question);
            return new SchemaSemanticRetrieval();
        }
    }

    private async Task<IReadOnlyList<(InferredTable Table, float[] Vector)>> GetTableVectorsAsync(
        CancellationToken cancellationToken)
    {
        var key = CachePrefix + EmbeddingTextVersion + "::" + (_embedder.ModelName ?? "default") + "::" + (_knowledge.SchemaHash ?? "");
        if (_cache.TryGetValue<IReadOnlyList<(InferredTable, float[])>>(key, out var cached) && cached is not null)
            return cached;

        await _primingLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Phase 6 — operators can hide system / infra tables from the retriever so the
            // LLM never sees them as candidates. Without this the model occasionally picks
            // CopilotAssessmentRunSummaries / CopilotTraceHistories as the root for analytic
            // questions (see AGG-001 trace). Empty list = retrieve all (legacy behavior).
            var hidden = _options.CurrentValue.RetrieverHiddenTables ?? new List<string>();
            var hiddenSet = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
            var visibleTables = _knowledge.AllTables.Where(t => !hiddenSet.Contains(t.Name)).ToList();

            var result = new List<(InferredTable, float[])>(visibleTables.Count);
            foreach (var table in visibleTables)
            {
                var text = BuildEmbeddingText(table);
                var vec = await _embedder.EmbedAsync(text, cancellationToken);
                if (vec.Length > 0) result.Add((table, vec));
            }
            sw.Stop();
            _logger.LogInformation(
                "[SchemaSemanticRetriever] Primed {Count} table vectors in {Ms}ms (model={Model}).",
                result.Count, sw.ElapsedMilliseconds, _embedder.ModelName);

            _cache.Set(key, (IReadOnlyList<(InferredTable, float[])>)result);
            return result;
        }
        finally { _primingLock.Release(); }
    }

    // Embedding text — UNIFORM structure per table so no role gets a systematic
    // advantage. Each table contributes:
    //   - Name (3x repetition — equal across types, weights the identity term)
    //   - Description (when present in schema knowledge)
    //   - Role-specific disambiguation keywords (small set, equal-size per role)
    //   - Label / content column names (for fact and lookup tables that have them)
    //   - Bridge endpoints (only bridges — needed to find the relationship by either side)
    //
    // Previously fact tables got an EXTRA copy of the name plus the trigger phrase
    // "records entries items rows", which let big fact tables ride generic English
    // ("list", "show me", "records") to outrank every other table regardless of question
    // content. With ~117/142 verified queries rooted on Tickets in this deployment, that
    // structural bias became the dominant routing signal.
    //
    // The schema hash is in the cache key, so changes to this function require regeneration
    // to take effect (delete the file via UI + regenerate).
    private static string BuildEmbeddingText(InferredTable t)
    {
        var parts = new List<string>();
        // Table name: 3x repetition — equal weight across all role types.
        parts.Add(t.Name);
        parts.Add(t.Name);

        if (!string.IsNullOrEmpty(t.Description)) parts.Add(t.Description);

        if (t.Flags.IsLookup)
        {
            parts.Add("lookup reference values options types");
            if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) parts.Add(t.Roles.LabelColumn);
        }
        else if (t.Flags.IsBridge)
        {
            parts.Add("association relationship mapping link");
            var endpoints = t.ForeignKeysOut.Select(fk => fk.Table).Distinct(StringComparer.OrdinalIgnoreCase);
            parts.AddRange(endpoints);
        }
        else if (t.Flags.IsPerson)
        {
            parts.Add("users people members accounts");
            if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) parts.Add(t.Roles.LabelColumn);
        }
        else
        {
            // Fact / domain tables — label + a few content columns, no marker phrase.
            // Removing the prior "records entries items rows" boost eliminates the bias
            // that let any fact table outrank lookups/persons on short generic queries.
            if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) parts.Add(t.Roles.LabelColumn);
            var content = t.Columns
                .Where(c => c.Role is null or "label" or "natural_key")
                .Where(c => c.Type.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase)
                         || c.Type.StartsWith("varchar", StringComparison.OrdinalIgnoreCase)
                         || c.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .Take(3);
            parts.AddRange(content);
        }

        parts.Add(t.Name);  // tail boost — equal across all types
        return string.Join(' ', parts);
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
