namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>Drop eq-filter whose column is also in GROUP BY (LLM-hallucinated contradiction).</summary>
internal sealed class DropFilterContradictingGroupByPhase : ISpecRepairPhase
{
    public string Name => "DropFilterContradictingGroupBy";
    public string Covers => "Filter eq + GROUP BY on the same column collapses per-group results";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Filters.Count == 0 || spec.GroupBy.Count == 0) return;
        var groupCols = new System.Collections.Generic.HashSet<string>(
            spec.GroupBy.Where(g => !string.IsNullOrEmpty(g)),
            System.StringComparer.OrdinalIgnoreCase);
        int before = spec.Filters.Count;
        spec.Filters.RemoveAll(f =>
            !string.IsNullOrEmpty(f.Column)
            && string.Equals(f.Op, SpecConst.FilterOps.Eq, System.StringComparison.OrdinalIgnoreCase)
            && groupCols.Contains(f.Column));
        int removed = before - spec.Filters.Count;
        if (removed > 0)
            ctx.Diagnostics.Add(new(Name, $"dropped {removed} contradicting filter(s)"));
    }
}
