namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Joins;

using SuperAdminCopilot.Models;

/// <summary>
/// When the aggregation is <c>COUNT(*)</c> AND the spec has at least one join that fans out (any
/// table joined whose relationship to the root is many-to-one in the FK direction — i.e. the
/// root row joins multiple rows from the joined table), wrap as <c>COUNT(DISTINCT root.Id)</c>
/// so the result counts root rows, not the Cartesian product.
///
/// <para>Concrete example: "how many tickets in regions with at least 3 customers" — naive
/// COUNT(*) over Tickets JOIN Regions JOIN Customers double-counts each ticket once per
/// matching customer. The user wants distinct ticket count.</para>
///
/// <para>Conservative gates:
/// <list type="bullet">
///   <item>Only fires when the aggregation is COUNT(*); other aggregates (SUM/AVG/MIN/MAX) have
///         their own semantics that fan-out doesn't necessarily corrupt.</item>
///   <item>Requires at least one explicit join target OR an implicit join (cross-table refs in
///         the spec) — pure-root queries don't need this.</item>
///   <item>Skips if the spec already uses DISTINCT or COUNT(DISTINCT …).</item>
///   <item>Requires the root to have an <c>Id</c> column (universal in this codebase).</item>
/// </list></para>
/// </summary>
internal sealed class ForceCountDistinctOnFanOutJoinPhase : ISpecRepairPhase
{
    public string Name => "ForceCountDistinctOnFanOutJoin";
    public string Covers => "COUNT(*) with fan-out join → COUNT(DISTINCT root.Id) to avoid Cartesian over-count";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, "Id")) return;
        if (spec.Aggregations.Count == 0) return;

        // Only act on a single COUNT(*) aggregation. Multiple aggregates → leave the LLM's call
        // alone; could be a deliberate "count of X with COUNT of Y" cross-aggregate.
        if (spec.Aggregations.Count != 1) return;
        var agg = spec.Aggregations[0];
        if (!string.Equals(agg.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase)) return;
        if (agg.Distinct) return;                                                // already correct
        if (agg.Column != "*") return;                                           // already targeted

        // Detect fan-out: any reference to a table other than root in SELECT / Filters / GroupBy.
        var hasJoin = spec.Joins.Any(j => !string.IsNullOrEmpty(j.Table)
                                          && !string.Equals(j.Table, spec.Root, System.StringComparison.OrdinalIgnoreCase));
        if (!hasJoin)
        {
            // Check for implicit-join column refs (Table.Column for table != root).
            hasJoin = HasCrossTableReference(spec);
        }
        if (!hasJoin) return;

        // Swap COUNT(*) → COUNT(DISTINCT root.Id).
        agg.Column = $"{spec.Root}.Id";
        agg.Distinct = true;
        if (string.IsNullOrEmpty(agg.Alias)) agg.Alias = "Count";
        ctx.Diagnostics.Add(new(Name, $"swapped COUNT(*) → COUNT(DISTINCT {spec.Root}.Id) due to fan-out join"));
    }

    private static bool HasCrossTableReference(QuerySpec spec)
    {
        var checkRefs = new System.Collections.Generic.List<string?>();
        checkRefs.AddRange(spec.Select);
        foreach (var f in spec.Filters) checkRefs.Add(f.Column);
        foreach (var g in spec.GroupBy) checkRefs.Add(g);
        foreach (var o in spec.OrderBy) checkRefs.Add(o.Column);
        foreach (var c in spec.Computed) checkRefs.Add(c.Expression);
        foreach (var a in spec.Aggregations) checkRefs.Add(a.Column);

        foreach (var r in checkRefs)
        {
            if (string.IsNullOrEmpty(r)) continue;
            var matches = System.Text.RegularExpressions.Regex.Matches(
                r, @"\[?([A-Za-z_][A-Za-z0-9_]*)\]?\.");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (!m.Groups[1].Success) continue;
                if (!string.Equals(m.Groups[1].Value, spec.Root, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
