namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Single MAX/MIN/SUM/AVG + GROUP BY on root.PK/NK → drop GROUP BY (per-row stats not scalar).</summary>
internal sealed class DropSpuriousGroupByForScalarAggregationPhase : ISpecRepairPhase
{
    public string Name => "DropSpuriousGroupByForScalarAggregation";
    public string Covers => "MAX/MIN with GROUP BY on identifier columns yields per-row not scalar";

    private static readonly System.Collections.Generic.HashSet<string> ScalarAggregations =
        new(System.StringComparer.OrdinalIgnoreCase) { "MAX", "MIN", "SUM", "AVG" };

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.GroupBy.Count == 0) return;
        if (spec.Aggregations.Count != 1) return;
        var agg = spec.Aggregations[0];
        if (string.IsNullOrEmpty(agg.Function) || !ScalarAggregations.Contains(agg.Function)) return;

        var allIdentifiers = true;
        foreach (var g in spec.GroupBy)
        {
            if (!IsIdentifierColumn(g, spec.Root)) { allIdentifiers = false; break; }
        }
        if (!allIdentifiers) return;

        var droppedGroupBy = spec.GroupBy.Count;
        var groupByCols = new System.Collections.Generic.HashSet<string>(spec.GroupBy, System.StringComparer.OrdinalIgnoreCase);
        spec.GroupBy.Clear();
        int droppedSelect = spec.Select.RemoveAll(s => groupByCols.Contains(s));
        ctx.Diagnostics.Add(new(Name, $"dropped {droppedGroupBy} groupBy + {droppedSelect} select for scalar {agg.Function}"));
    }

    private static bool IsIdentifierColumn(string columnRef, string root)
    {
        if (string.IsNullOrEmpty(columnRef)) return false;
        var dotIdx = columnRef.LastIndexOf('.');
        var bare = dotIdx >= 0 ? columnRef.Substring(dotIdx + 1) : columnRef;
        if (string.Equals(bare, "Id", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (bare.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (bare.EndsWith("Number", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (bare.EndsWith("Code", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
