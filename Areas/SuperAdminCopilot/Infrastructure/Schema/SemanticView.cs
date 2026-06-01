namespace SuperAdminCopilot.Infrastructure.Schema;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Semantic;                              // v2 ISemanticLayer, DerivedMetricDefinition
using SuperAdminCopilot.Application.Repair;                 // NaturalKeyFormat
using SuperAdminCopilot.Application.Repair.Semantic;        // v3 ISemanticView

/// <summary>
/// V3 <see cref="ISemanticView"/> implementation that wraps v2's <see cref="ISemanticLayer"/>.
/// Surface lookup methods return <see cref="System.Array.Empty{T}"/> for non-declared
/// columns rather than throwing — repair rules cope with "no data" cleanly.
/// </summary>
internal sealed class SemanticView : ISemanticView
{
    private readonly ISemanticLayer _layer;

    public SemanticView(ISemanticLayer layer) { _layer = layer; }

    public EntityView? GetEntity(string table)
    {
        var e = _layer.GetEntityForTable(table);
        if (e is null) return null;
        return new EntityView(
            Table: e.Table,
            Name: e.Name ?? "",
            Synonyms: e.Synonyms ?? (IReadOnlyList<string>)System.Array.Empty<string>(),
            LabelColumn: e.LabelColumn,
            NaturalKeyColumn: e.NaturalKeyColumn,
            SearchableColumns: e.SearchableColumns ?? (IReadOnlyList<string>)System.Array.Empty<string>(),
            DisplayColumns: e.DisplayColumns ?? (IReadOnlyList<string>)System.Array.Empty<string>());
    }

    public string? LabelColumnFor(string table) => _layer.GetEntityForTable(table)?.LabelColumn;
    public string? NaturalKeyColumnFor(string table) => _layer.GetEntityForTable(table)?.NaturalKeyColumn;

    public IReadOnlyList<string> SearchableColumnsFor(string table)
        => (IReadOnlyList<string>?)_layer.GetEntityForTable(table)?.SearchableColumns
            ?? System.Array.Empty<string>();

    public IReadOnlyList<string> DisplayColumnsFor(string table)
        => (IReadOnlyList<string>?)_layer.GetEntityForTable(table)?.DisplayColumns
            ?? System.Array.Empty<string>();

    public string? GetDateColumn(string table, string? role = null)
        => _layer.GetDateColumn(table, role);

    public IReadOnlyList<string> NumericColumnPreference
        => (IReadOnlyList<string>?)_layer.Config?.Defaults?.NumericColumnPreference
            ?? System.Array.Empty<string>();

    public IReadOnlyList<string> AuxiliaryTableSuffixes
        => (IReadOnlyList<string>?)_layer.Config?.Defaults?.AuxiliaryTableSuffixes
            ?? System.Array.Empty<string>();

    public string ResolveSynonymValue(string columnRef, string value)
        => _layer.ResolveSynonymValue(columnRef, value);

    public IReadOnlyList<string> ReachableLookupTables(string root)
    {
        var hits = new List<string>();
        if (string.IsNullOrEmpty(root)) return hits;
        foreach (var e in _layer.Config.Entities)
        {
            // We don't have direct FK-out enumeration here; rely on entity flag IsLookup
            // (declared via semantic-layer) — universal, not entity-name hardcoded.
            if (e.IsLookup && !string.IsNullOrEmpty(e.Table))
                hits.Add(e.Table);
        }
        return hits;
    }

    public IReadOnlyList<NaturalKeyFormat> NaturalKeyFormats
    {
        get
        {
            var formats = new List<NaturalKeyFormat>();
            foreach (var e in _layer.Config.Entities)
            {
                if (string.IsNullOrEmpty(e.Table)) continue;
                if (string.IsNullOrEmpty(e.NaturalKeyColumn)) continue;
                if (string.IsNullOrEmpty(e.NaturalKeyFormat)) continue;
                formats.Add(new NaturalKeyFormat(e.Table, e.NaturalKeyColumn, e.NaturalKeyFormat));
            }
            return formats;
        }
    }

    public DerivedMetricHint? ResolveDerivedMetric(string question, string root)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(question)) return null;
        var entity = _layer.GetEntityForTable(root);
        if (entity?.DerivedMetrics is null || entity.DerivedMetrics.Count == 0) return null;

        var qLower = question.ToLowerInvariant();
        foreach (var m in entity.DerivedMetrics)
        {
            if (m.Cues is null || m.Cues.Count == 0) continue;
            string? matchedCue = null;
            foreach (var cue in m.Cues)
            {
                if (string.IsNullOrEmpty(cue)) continue;
                if (qLower.Contains(cue.ToLowerInvariant())) { matchedCue = cue; break; }
            }
            if (matchedCue is null) continue;

            var expr = m.Expression ?? "";
            if (expr.Contains("{unit}", System.StringComparison.Ordinal))
            {
                var unit = ResolveUnitCue(qLower, m) ?? m.DefaultUnit ?? "DAY";
                expr = expr.Replace("{unit}", unit);
            }
            var alias = string.IsNullOrEmpty(m.Key) ? "DerivedMetric" : m.Key;
            return new DerivedMetricHint(m.Function ?? "AVG", expr, alias);
        }
        return null;
    }

    private static string? ResolveUnitCue(string qLower, DerivedMetricDefinition metric)
    {
        if (metric.UnitCue is null || metric.UnitCue.Count == 0) return null;
        foreach (var (unit, patterns) in metric.UnitCue)
        {
            if (patterns is null) continue;
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(qLower, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return unit;
            }
        }
        return null;
    }

    public bool IsTemporalStatusColumn(string entity, string column)
    {
        if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(column)) return false;
        var e = _layer.GetEntityForTable(entity);
        if (e?.TemporalStatusColumns is null) return false;
        foreach (var col in e.TemporalStatusColumns)
            if (string.Equals(col, column, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public IReadOnlyList<ConceptPatternMatch> MatchConceptPatterns(string question, string root)
    {
        // semantic-layer.json's `conceptPatterns` (global) block: each pattern is a
        // list of trigger words + a list of filters to inject when triggered. Universal —
        // adding "overdue" semantics is a JSON edit. Returns matched patterns.
        var hits = new List<ConceptPatternMatch>();
        var patterns = _layer.Config?.ConceptPatterns;
        if (patterns is null || patterns.Count == 0) return hits;
        var qLower = question.ToLowerInvariant();
        foreach (var cp in patterns)
        {
            if (cp.Triggers is null) continue;
            bool matched = false;
            foreach (var trig in cp.Triggers)
            {
                if (string.IsNullOrEmpty(trig)) continue;
                if (qLower.Contains(trig.ToLowerInvariant())) { matched = true; break; }
            }
            if (!matched) continue;
            var filters = new List<SuperAdminCopilot.Application.Repair.Semantic.ConceptFilter>();
            if (cp.Filters is not null)
            {
                foreach (var f in cp.Filters)
                {
                    if (string.IsNullOrEmpty(f.Column)) continue;
                    filters.Add(new SuperAdminCopilot.Application.Repair.Semantic.ConceptFilter(
                        f.Column!, f.Op ?? "eq", f.Value?.ToString() ?? ""));
                }
            }
            hits.Add(new ConceptPatternMatch(cp.Meaning ?? "concept", filters));
        }
        return hits;
    }

    public string? ResolveDateRoleForVerb(string verb, string root)
    {
        // semantic-layer.json's `dateRoles` block per entity: { resolved: "ResolvedAt",
        // issued: "IssuedAt", created: "CreatedAt", ... }. Returns the column name for
        // the verb when declared; null otherwise. Universal — no hardcoded mappings.
        if (string.IsNullOrEmpty(verb) || string.IsNullOrEmpty(root)) return null;
        var entity = _layer.GetEntityForTable(root);
        if (entity?.DateRoles is null) return null;
        return entity.DateRoles.TryGetValue(verb, out var col) ? col : null;
    }
}
