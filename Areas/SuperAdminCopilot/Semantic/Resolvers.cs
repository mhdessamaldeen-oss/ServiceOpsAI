namespace SuperAdminCopilot.Semantic;

using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

// ══════════════════════════════════════════════════════════════════════════════════════════
// Phase 5 — Generic Resolver Layer.
//
// These interfaces expose the semantic + schema + FK-graph capabilities that already exist
// in ISemanticLayer, ISemanticCandidateResolver, and IForeignKeyGraph through a single,
// purpose-built seam. Consumers (shapes, planner prompt builder, guards, metadata handler)
// depend on the resolver that is right for their concern, not on the full lower-level API.
//
// Resolver output always includes:
//   • Selected target (nullable — null means "could not resolve")
//   • Confidence  (0.0 – 1.0)
//   • Source      (explicit-config | synonym | fuzzy | embedding | catalog | graph)
//   • Reason      (human-readable trace note)
//   • RequiresClarification — true when multiple candidates are close in score
// ══════════════════════════════════════════════════════════════════════════════════════════

// ── Shared resolution result ─────────────────────────────────────────────────────────────

/// <summary>Generic resolution envelope returned by every resolver.</summary>
public sealed record ResolverResult<T>(
    T? Value,
    double Confidence,
    string Source,
    string Reason,
    bool RequiresClarification = false)
{
    public bool Resolved => Value is not null && !RequiresClarification;

    public static ResolverResult<T> NotFound(string reason) =>
        new(default, 0.0, "none", reason);

    public static ResolverResult<T> Ambiguous(string reason) =>
        new(default, 0.0, "none", reason, RequiresClarification: true);

    public static ResolverResult<T> Exact(T value, string source = "explicit-config") =>
        new(value, 1.0, source, $"exact match via {source}");

    public static ResolverResult<T> Fuzzy(T value, double confidence, string source = "fuzzy") =>
        new(value, confidence, source, $"fuzzy match ({confidence:P0}) via {source}");
}

// ── IEntityResolver ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a user-supplied noun (or phrase) to a <see cref="EntityDefinition"/>.
/// Resolution order: exact configured name → synonym → fuzzy/embedding fallback.
/// Returns null-value result when confidence is too low to act without clarification.
/// </summary>
public interface IEntityResolver
{
    ResolverResult<EntityDefinition> Resolve(string noun);
    ResolverResult<EntityDefinition> ResolveForTable(string tableName);
}

internal sealed class EntityResolver : IEntityResolver
{
    private readonly ISemanticLayer _semantic;
    private readonly ISemanticCandidateResolver _candidates;
    private readonly IEntityCatalog _catalog;
    private readonly CopilotOptions _options;

    public EntityResolver(
        ISemanticLayer semantic,
        ISemanticCandidateResolver candidates,
        IEntityCatalog catalog,
        IOptions<CopilotOptions> options)
    {
        _semantic = semantic;
        _candidates = candidates;
        _catalog = catalog;
        _options = options.Value;
    }

    public ResolverResult<EntityDefinition> Resolve(string noun)
    {
        if (string.IsNullOrWhiteSpace(noun))
            return ResolverResult<EntityDefinition>.NotFound("empty input");

        // 1. Exact name / synonym lookup (O(1) dictionary).
        var entity = _semantic.GetEntityByNameOrSynonym(noun);
        if (entity is not null && _catalog.TableExists(entity.Table))
            return ResolverResult<EntityDefinition>.Exact(entity);

        // 2. Fuzzy / embedding candidate search.
        var top = _candidates.ResolveTables(noun, limit: 3);
        if (top.Count == 0)
            return ResolverResult<EntityDefinition>.NotFound($"no candidate for '{noun}'");

        // Two candidates very close in score → ambiguous.
        if (top.Count >= 2 && top[0].Score - top[1].Score <= _options.AmbiguityClarificationThreshold)
            return ResolverResult<EntityDefinition>.Ambiguous($"'{noun}' is ambiguous between {top[0].Name} and {top[1].Name}");

        var best = top[0];
        if (best.Score < Math.Max(0.58, _options.ResolverMinConfidence))
            return ResolverResult<EntityDefinition>.NotFound($"low-confidence candidate '{best.Name}' ({best.Score:P0})");

        var resolved = _semantic.GetEntityForTable(best.Name);
        if (resolved is null)
            return ResolverResult<EntityDefinition>.NotFound($"table '{best.Name}' has no semantic entity");

        return ResolverResult<EntityDefinition>.Fuzzy(resolved, best.Score, best.Source);
    }

    public ResolverResult<EntityDefinition> ResolveForTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return ResolverResult<EntityDefinition>.NotFound("empty table name");
        var entity = _semantic.GetEntityForTable(tableName);
        return entity is not null
            ? ResolverResult<EntityDefinition>.Exact(entity)
            : ResolverResult<EntityDefinition>.NotFound($"no entity for table '{tableName}'");
    }
}

// ── IColumnResolver ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a user-supplied column hint (word / phrase) to a qualified <c>Table.Column</c>
/// string on the given table. Falls back to FK-neighbor lookup when the column is not
/// directly on the root table.
/// </summary>
public interface IColumnResolver
{
    ResolverResult<string> Resolve(string table, string columnHint);
}

internal sealed class ColumnResolver : IColumnResolver
{
    private readonly IEntityCatalog _catalog;
    private readonly ISemanticCandidateResolver _candidates;
    private readonly CopilotOptions _options;

    public ColumnResolver(
        IEntityCatalog catalog,
        ISemanticCandidateResolver candidates,
        IOptions<CopilotOptions> options)
    {
        _catalog = catalog;
        _candidates = candidates;
        _options = options.Value;
    }

    public ResolverResult<string> Resolve(string table, string columnHint)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(columnHint))
            return ResolverResult<string>.NotFound("empty input");

        // Direct PascalCase match.
        var pascal = ToPascal(columnHint);
        if (_catalog.ColumnExists(table, pascal))
            return ResolverResult<string>.Exact($"{table}.{pascal}");

        // Fuzzy column search scoped to this table.
        var top = _candidates.ResolveColumns(columnHint, table, limit: 2);
        if (top.Count == 0)
            return ResolverResult<string>.NotFound($"no column match for '{columnHint}' on {table}");
        if (top.Count >= 2 && top[0].Score - top[1].Score <= _options.AmbiguityClarificationThreshold)
            return ResolverResult<string>.Ambiguous($"column hint '{columnHint}' on {table} is ambiguous");
        if (top[0].Score < Math.Max(0.60, _options.ResolverMinConfidence))
            return ResolverResult<string>.NotFound($"low-confidence column match '{top[0].Name}' ({top[0].Score:P0})");

        return ResolverResult<string>.Fuzzy($"{top[0].Table}.{top[0].Name}", top[0].Score, top[0].Source);
    }

    private static string ToPascal(string hint)
    {
        var words = hint.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w =>
            w.Length == 1 ? char.ToUpperInvariant(w[0]).ToString() : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

// ── IMetricResolver ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a user-supplied metric phrase (\"resolution time\", \"avg fix time\") to a
/// <see cref="MetricDefinition"/> from the semantic layer.
/// Resolution order: exact dictionary lookup → FuzzySharp scan over all metric names + synonyms.
/// </summary>
public interface IMetricResolver
{
    ResolverResult<MetricDefinition> Resolve(string phrase);
}

internal sealed class MetricResolver : IMetricResolver
{
    private readonly ISemanticLayer _semantic;
    private const int MinFuzzyScore = 72;

    public MetricResolver(ISemanticLayer semantic) => _semantic = semantic;

    public ResolverResult<MetricDefinition> Resolve(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return ResolverResult<MetricDefinition>.NotFound("empty input");

        // 1. Exact dictionary lookup (O(1)).
        var metric = _semantic.GetMetric(phrase);
        if (metric is not null)
            return ResolverResult<MetricDefinition>.Exact(metric);

        // 2. FuzzySharp fallback: scan all metrics and their synonyms for a fuzzy match.
        //    Catches paraphrases like "avg fix time" → avg_resolution_hours, "how long to resolve"
        //    → avg_resolution_hours. Uses TokenSetRatio for word-order-independent matching.
        var normalizedPhrase = phrase.Replace('_', ' ').ToLowerInvariant().Trim();
        MetricDefinition? bestMetric = null;
        var bestScore = 0;
        foreach (var m in _semantic.Config.Metrics)
        {
            var nameScore = FuzzierSharp.Fuzz.TokenSetRatio(normalizedPhrase, m.Name.Replace('_', ' ').ToLowerInvariant());
            if (nameScore > bestScore) { bestScore = nameScore; bestMetric = m; }
            foreach (var s in m.Synonyms)
            {
                var synScore = FuzzierSharp.Fuzz.TokenSetRatio(normalizedPhrase, s.ToLowerInvariant());
                if (synScore > bestScore) { bestScore = synScore; bestMetric = m; }
            }
        }

        if (bestMetric is not null && bestScore >= MinFuzzyScore)
            return ResolverResult<MetricDefinition>.Fuzzy(bestMetric, bestScore / 100.0, "fuzzy-metric");

        return ResolverResult<MetricDefinition>.NotFound($"no metric for '{phrase}'");
    }
}

// ── IDimensionResolver ───────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a user-supplied dimension phrase to a <see cref="DimensionDefinition"/>.
/// Resolution order: exact dictionary lookup → FuzzySharp scan over all dimension names + synonyms.
/// </summary>
public interface IDimensionResolver
{
    ResolverResult<DimensionDefinition> Resolve(string phrase);
}

internal sealed class DimensionResolver : IDimensionResolver
{
    private readonly ISemanticLayer _semantic;
    private const int MinFuzzyScore = 72;

    public DimensionResolver(ISemanticLayer semantic) => _semantic = semantic;

    public ResolverResult<DimensionDefinition> Resolve(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return ResolverResult<DimensionDefinition>.NotFound("empty input");

        // 1. Exact dictionary lookup (O(1)).
        var dim = _semantic.GetDimension(phrase);
        if (dim is not null)
            return ResolverResult<DimensionDefinition>.Exact(dim);

        // 2. FuzzySharp fallback: scan all dimensions and their synonyms.
        //    Catches "priority level" → priority, "ticket state" → status, etc.
        var normalizedPhrase = phrase.Replace('_', ' ').ToLowerInvariant().Trim();
        DimensionDefinition? bestDim = null;
        var bestScore = 0;
        foreach (var d in _semantic.Config.Dimensions)
        {
            var nameScore = FuzzierSharp.Fuzz.TokenSetRatio(normalizedPhrase, d.Name.Replace('_', ' ').ToLowerInvariant());
            if (nameScore > bestScore) { bestScore = nameScore; bestDim = d; }
            foreach (var s in d.Synonyms)
            {
                var synScore = FuzzierSharp.Fuzz.TokenSetRatio(normalizedPhrase, s.ToLowerInvariant());
                if (synScore > bestScore) { bestScore = synScore; bestDim = d; }
            }
        }

        if (bestDim is not null && bestScore >= MinFuzzyScore)
            return ResolverResult<DimensionDefinition>.Fuzzy(bestDim, bestScore / 100.0, "fuzzy-dimension");

        return ResolverResult<DimensionDefinition>.NotFound($"no dimension for '{phrase}'");
    }
}

// ── IValueSynonymResolver ────────────────────────────────────────────────────────────────

/// <summary>
/// Rewrites a filter value in a column context to its canonical form.
/// E.g. \"urgent\" on <c>TicketPriorities.Name</c> → <c>\"Critical\"</c>.
/// Returns the original value unchanged when no synonym is configured.
/// </summary>
public interface IValueSynonymResolver
{
    string Resolve(string column, string value);
}

internal sealed class ValueSynonymResolver : IValueSynonymResolver
{
    private readonly ISemanticLayer _semantic;

    public ValueSynonymResolver(ISemanticLayer semantic) => _semantic = semantic;

    public string Resolve(string column, string value) =>
        _semantic.ResolveSynonymValue(column, value);
}

// ── ITemporalResolver ────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a date-role verb (\"created\", \"closed\", \"resolved\", null for default) to the
/// actual column name on a given table, per the entity's <c>DateRoles</c> configuration.
/// </summary>
public interface ITemporalResolver
{
    ResolverResult<string> ResolveColumn(string table, string? role);
}

internal sealed class TemporalResolver : ITemporalResolver
{
    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;

    public TemporalResolver(ISemanticLayer semantic, IEntityCatalog catalog)
    {
        _semantic = semantic;
        _catalog = catalog;
    }

    public ResolverResult<string> ResolveColumn(string table, string? role)
    {
        if (string.IsNullOrWhiteSpace(table))
            return ResolverResult<string>.NotFound("empty table");

        var col = _semantic.GetDateColumn(table, role);
        if (string.IsNullOrEmpty(col))
            return ResolverResult<string>.NotFound($"no date column for role '{role}' on {table}");

        if (!_catalog.ColumnExists(table, col))
            return ResolverResult<string>.NotFound($"configured date column '{col}' not found on {table}");

        var source = role is null ? "default" : "date-role-config";
        return ResolverResult<string>.Exact(col, source);
    }
}

// ── IRelationshipResolver ────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a join path between two tables using the FK graph.
/// Returns the ordered list of <see cref="FkEdge"/> steps, or a not-found / ambiguous result.
/// </summary>
public interface IRelationshipResolver
{
    ResolverResult<IReadOnlyList<FkEdge>> FindPath(string fromTable, string toTable);
    IEnumerable<string> Neighbors(string table);
}

internal sealed class RelationshipResolver : IRelationshipResolver
{
    private readonly IForeignKeyGraph _graph;

    public RelationshipResolver(IForeignKeyGraph graph) => _graph = graph;

    public ResolverResult<IReadOnlyList<FkEdge>> FindPath(string fromTable, string toTable)
    {
        if (string.IsNullOrWhiteSpace(fromTable) || string.IsNullOrWhiteSpace(toTable))
            return ResolverResult<IReadOnlyList<FkEdge>>.NotFound("empty table name");

        var path = _graph.FindPath(fromTable, toTable);
        return path is not null
            ? ResolverResult<IReadOnlyList<FkEdge>>.Exact(path, "fk-graph")
            : ResolverResult<IReadOnlyList<FkEdge>>.NotFound($"no FK path from {fromTable} to {toTable}");
    }

    public IEnumerable<string> Neighbors(string table) => _graph.Neighbors(table);
}

// ── INaturalKeyResolver ──────────────────────────────────────────────────────────────────

/// <summary>
/// Detects a natural-key ID value in a question string and routes it to the correct entity
/// by matching the entity's <see cref="EntityDefinition.NaturalKeyFormat"/> regex.
/// Returns the entity and the extracted ID value, or a not-found result.
/// </summary>
public interface INaturalKeyResolver
{
    ResolverResult<(EntityDefinition Entity, string Id)> Resolve(string question);
}

internal sealed class NaturalKeyResolver : INaturalKeyResolver
{
    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;

    // Generic ID detector: 1-6 letters, optional secondary letter segment, digits.
    private static readonly System.Text.RegularExpressions.Regex BareId = new(
        @"(?:^|\s)(?<id>[A-Za-z]{1,6}(?:-[A-Za-z]{1,4})?-\d{1,10}(?:-\d{1,10})*)(?:\s|$|[?!.,])",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // M4 — cache compiled Regex per NaturalKeyFormat to avoid 10+ regex compilations per
    // request. ConcurrentDictionary handles thread safety for the Singleton resolver.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex?> CompiledFormats = new();

    public NaturalKeyResolver(ISemanticLayer semantic, IEntityCatalog catalog)
    {
        _semantic = semantic;
        _catalog = catalog;
    }

    public ResolverResult<(EntityDefinition Entity, string Id)> Resolve(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return ResolverResult<(EntityDefinition, string)>.NotFound("empty question");

        var m = BareId.Match(question);
        if (!m.Success)
            return ResolverResult<(EntityDefinition, string)>.NotFound("no natural-key pattern in question");

        var id = m.Groups["id"].Value;

        EntityDefinition? winner = null;
        foreach (var e in _semantic.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.NaturalKeyColumn) || string.IsNullOrEmpty(e.NaturalKeyFormat)) continue;
            if (!_catalog.TableExists(e.Table)) continue;

            // M4 — use a cached compiled Regex instead of recompiling per entity per request.
            var formatRegex = CompiledFormats.GetOrAdd(e.NaturalKeyFormat!, pattern =>
            {
                try { return new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled); }
                catch (System.ArgumentException) { return null; }
            });
            if (formatRegex is null || !formatRegex.IsMatch(id))
                continue;

            if (winner is not null)
                return ResolverResult<(EntityDefinition, string)>.Ambiguous($"ID '{id}' matches multiple entities");
            winner = e;
        }

        return winner is not null
            ? ResolverResult<(EntityDefinition, string)>.Exact((winner, id.ToUpperInvariant()), "natural-key-format")
            : ResolverResult<(EntityDefinition, string)>.NotFound($"no entity NaturalKeyFormat matches '{id}'");
    }
}
