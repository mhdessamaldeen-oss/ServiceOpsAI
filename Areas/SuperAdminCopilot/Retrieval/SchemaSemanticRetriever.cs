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
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Configuration.EmbeddingKeywordsOptions> _keywordOptions;
    private readonly Semantic.ISemanticLayer _semanticLayer;
    private readonly ILogger<SchemaSemanticRetriever> _logger;
    private readonly SemaphoreSlim _primingLock = new(1, 1);

    public SchemaSemanticRetriever(
        ISchemaKnowledge knowledge,
        ITextEmbedder embedder,
        IMemoryCache cache,
        Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CopilotOptions> options,
        Microsoft.Extensions.Options.IOptionsMonitor<Configuration.EmbeddingKeywordsOptions> keywordOptions,
        Semantic.ISemanticLayer semanticLayer,
        ILogger<SchemaSemanticRetriever> logger)
    {
        _knowledge = knowledge;
        _embedder = embedder;
        _options = options;
        _keywordOptions = keywordOptions;
        _cache = cache;
        _semanticLayer = semanticLayer;
        _logger = logger;
    }

    /// <summary>
    /// True when the table's name ends with any of the operator-configured auxiliary
    /// suffixes (Histories / Notifications / Audits / Logs / Snapshots / …). Used by the
    /// retriever to apply a small score penalty so main entities outrank their satellites.
    /// </summary>
    private static bool IsAuxiliaryTable(string? tableName, System.Collections.Generic.List<string> suffixes)
    {
        if (string.IsNullOrEmpty(tableName) || suffixes is null || suffixes.Count == 0) return false;
        foreach (var sfx in suffixes)
        {
            if (string.IsNullOrEmpty(sfx)) continue;
            if (tableName.EndsWith(sfx, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

            // Load the auxiliary-table-suffix list once per call. Empty by default; operators
            // add suffixes via semantic-layer.json. Tables ending in any of these get a small
            // score penalty so main entities outrank their satellites ("Outages" beats
            // "OutageHistories" / "OutageNotifications" for "show me outages"). Universal —
            // applies to any future satellite without naming it.
            var auxSuffixes = _semanticLayer?.Config?.Defaults?.AuxiliaryTableSuffixes
                ?? new System.Collections.Generic.List<string>();
            // Penalty comes from CopilotOptions — operators tune via copilot-options.json
            // without recompiling. Default 0.05 keeps a clear cosine win intact while
            // breaking near-ties toward the main entity. Set to 0 to disable.
            var auxPenalty = (float)_options.CurrentValue.AuxiliaryTableScorePenalty;

            var scored = new List<TableMatch>(tableVecs.Count);
            foreach (var (table, vec) in tableVecs)
            {
                if (vec.Length != queryVec.Length) continue;
                var rawScore = VectorMath.Cosine(queryVec, vec);
                if (IsAuxiliaryTable(table.Name, auxSuffixes))
                {
                    rawScore -= auxPenalty;
                }
                scored.Add(new TableMatch(table, rawScore));
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
                var text = BuildEmbeddingText(table, _keywordOptions.CurrentValue);
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
    // Role-keyword boosts now come from Configuration/copilot-options.json → EmbeddingKeywords;
    // see Configuration/EmbeddingKeywordsOptions.cs. Operators can tune or translate these
    // without recompiling.
    private static string BuildEmbeddingText(InferredTable t, Configuration.EmbeddingKeywordsOptions kw)
    {
        var parts = new List<string>();
        // Table name: 3x repetition — equal weight across all role types.
        parts.Add(t.Name);
        parts.Add(t.Name);

        if (!string.IsNullOrEmpty(t.Description)) parts.Add(t.Description);

        if (t.Flags.IsLookup)
        {
            if (!string.IsNullOrEmpty(kw.Lookup)) parts.Add(kw.Lookup);
            if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) parts.Add(t.Roles.LabelColumn);
        }
        else if (t.Flags.IsBridge)
        {
            if (!string.IsNullOrEmpty(kw.Bridge)) parts.Add(kw.Bridge);
            var endpoints = t.ForeignKeysOut.Select(fk => fk.Table).Distinct(StringComparer.OrdinalIgnoreCase);
            parts.AddRange(endpoints);
        }
        else if (t.Flags.IsPerson)
        {
            if (!string.IsNullOrEmpty(kw.Person)) parts.Add(kw.Person);
            if (!string.IsNullOrEmpty(t.Roles.LabelColumn)) parts.Add(t.Roles.LabelColumn);
        }
        else
        {
            // Fact / domain tables — label + a few content columns. Default kw.Fact is empty
            // (historic behavior: no boost), but operators can opt in via JSON if they want.
            if (!string.IsNullOrEmpty(kw.Fact)) parts.Add(kw.Fact);
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

}
