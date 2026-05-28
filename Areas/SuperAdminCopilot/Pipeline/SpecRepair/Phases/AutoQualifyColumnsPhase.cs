namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Bare "Column" → "Table.Column" using spec.Root when the bare name exists on root.</summary>
internal sealed class AutoQualifyColumnsPhase : ISpecRepairPhase
{
    public string Name => "AutoQualifyColumns";
    public string Covers => "Unqualified column refs that would silently drop downstream";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        int mutated = 0;
        for (int i = 0; i < spec.Select.Count; i++)
        {
            var q = TryQualify(spec.Select[i], spec.Root, ctx);
            if (q != spec.Select[i]) { spec.Select[i] = q; mutated++; }
        }
        for (int i = 0; i < spec.GroupBy.Count; i++)
        {
            var q = TryQualify(spec.GroupBy[i], spec.Root, ctx);
            if (q != spec.GroupBy[i]) { spec.GroupBy[i] = q; mutated++; }
        }
        foreach (var a in spec.Aggregations)
        {
            if (string.IsNullOrEmpty(a.Column) || a.Column == "*") continue;
            if (a.Column.StartsWith("expr:", System.StringComparison.OrdinalIgnoreCase)) continue;
            var q = TryQualify(a.Column, spec.Root, ctx);
            if (q != a.Column) { a.Column = q; mutated++; }
        }
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            var q = TryQualify(f.Column, spec.Root, ctx);
            if (q != f.Column) { f.Column = q; mutated++; }
        }
        foreach (var o in spec.OrderBy)
        {
            if (string.IsNullOrEmpty(o.Column)) continue;
            var q = TryQualify(o.Column, spec.Root, ctx);
            if (q != o.Column) { o.Column = q; mutated++; }
        }
        foreach (var h in spec.Having)
        {
            if (string.IsNullOrEmpty(h.Column) || h.Column == "*") continue;
            var q = TryQualify(h.Column, spec.Root, ctx);
            if (q != h.Column) { h.Column = q; mutated++; }
        }
        if (mutated > 0)
            ctx.Diagnostics.Add(new(Name, $"qualified {mutated} ref(s) against root={spec.Root}"));
    }

    private static string TryQualify(string columnRef, string rootTable, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(columnRef)) return columnRef;
        if (columnRef.Contains('.') || columnRef.Contains('(') || columnRef.Contains(' ')) return columnRef;
        if (columnRef == "*") return columnRef;
        return ctx.Catalog.ColumnExists(rootTable, columnRef) ? $"{rootTable}.{columnRef}" : columnRef;
    }
}
