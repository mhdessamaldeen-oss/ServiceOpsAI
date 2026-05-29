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
        foreach (var a in spec.Aggregations.NotNull())
        {
            if (string.IsNullOrEmpty(a.Column) || a.Column == "*") continue;
            if (a.Column.StartsWith("expr:", System.StringComparison.OrdinalIgnoreCase)) continue;
            var q = TryQualify(a.Column, spec.Root, ctx);
            if (q != a.Column) { a.Column = q; mutated++; }
        }
        foreach (var f in spec.Filters.NotNull())
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            var q = TryQualify(f.Column, spec.Root, ctx);
            if (q != f.Column) { f.Column = q; mutated++; }
        }
        foreach (var o in spec.OrderBy.NotNull())
        {
            if (string.IsNullOrEmpty(o.Column)) continue;
            var q = TryQualify(o.Column, spec.Root, ctx);
            if (q != o.Column) { o.Column = q; mutated++; }
        }
        foreach (var h in spec.Having.NotNull())
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
        if (columnRef.Contains('(') || columnRef.Contains(' ')) return columnRef;
        if (columnRef == "*") return columnRef;

        // Pre-qualified form: "Alias.Column". The LLM frequently emits SQL-style short aliases
        // (T, TC, TP, AU) instead of real table names. If the prefix isn't a real catalog table
        // OR the column doesn't exist on that prefix, try to resolve by walking known tables —
        // root first, then any joined table that has a column with that bare name. Universal
        // (no entity names hardcoded) and idempotent: returns the original if it's already valid.
        if (columnRef.Contains('.'))
        {
            var dotIdx = columnRef.LastIndexOf('.');
            var prefix = columnRef.Substring(0, dotIdx);
            var bare = columnRef.Substring(dotIdx + 1);
            if (string.IsNullOrEmpty(bare)) return columnRef;
            // Already valid: prefix is a real table AND column exists on it.
            if (ctx.Catalog.TableExists(prefix) && ctx.Catalog.ColumnExists(prefix, bare))
                return columnRef;
            // Resolve: try root first (most common case for LLM's short aliases).
            if (ctx.Catalog.ColumnExists(rootTable, bare))
                return $"{rootTable}.{bare}";
            // Fallback: walk every entity table and find the FIRST one with this column. We
            // pick deterministically by table name length (shorter wins — TicketCategories
            // before AspNetUsers when both have a "Name" column would prefer the more specific).
            string? bestTable = null;
            foreach (var e in ctx.SemanticLayer.Config.Entities)
            {
                if (string.IsNullOrEmpty(e.Table)) continue;
                if (!ctx.Catalog.TableExists(e.Table)) continue;
                if (!ctx.Catalog.ColumnExists(e.Table, bare)) continue;
                if (bestTable is null || e.Table.Length < bestTable.Length) bestTable = e.Table;
            }
            return bestTable is not null ? $"{bestTable}.{bare}" : columnRef;
        }

        return ctx.Catalog.ColumnExists(rootTable, columnRef) ? $"{rootTable}.{columnRef}" : columnRef;
    }
}
