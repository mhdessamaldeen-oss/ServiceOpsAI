namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Drop select items that are also the target of an aggregation when no GROUP BY.</summary>
internal sealed class DropAggregatedColumnsFromSelectPhase : ISpecRepairPhase
{
    public string Name => "DropAggregatedColumnsFromSelect";
    public string Covers => "Metric column appears in select alongside its own aggregation";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count == 0 || spec.Select.Count == 0) return;
        if (spec.GroupBy.Count > 0) return;
        var aggregatedCols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var a in spec.Aggregations)
        {
            if (string.IsNullOrEmpty(a.Column) || a.Column == "*") continue;
            if (a.Column.StartsWith("expr:", System.StringComparison.OrdinalIgnoreCase)) continue;
            aggregatedCols.Add(a.Column);
        }
        if (aggregatedCols.Count == 0) return;
        int before = spec.Select.Count;
        spec.Select.RemoveAll(s => aggregatedCols.Contains(s));
        int removed = before - spec.Select.Count;
        if (removed > 0)
            ctx.Diagnostics.Add(new(Name, $"dropped {removed} duplicate(s)"));
    }
}
