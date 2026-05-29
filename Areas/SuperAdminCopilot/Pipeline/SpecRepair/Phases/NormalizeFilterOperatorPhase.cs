namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Raw SQL operators ("=", "!=", ">") in filter.op → symbolic ("eq", "neq", "gt").</summary>
internal sealed class NormalizeFilterOperatorPhase : ISpecRepairPhase
{
    public string Name => "NormalizeFilterOperator";
    public string Covers => "Filter op field uses SQL operators instead of symbolic names";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        int mutated = 0;
        var map = ctx.Options.SqlComparisonOperatorMap;
        foreach (var f in spec.Filters.NotNull())
        {
            if (string.IsNullOrWhiteSpace(f.Op)) continue;
            if (map.TryGetValue(f.Op.Trim(), out var canonical) && !string.Equals(canonical, f.Op, System.StringComparison.Ordinal))
            {
                f.Op = canonical;
                mutated++;
            }
        }
        foreach (var h in spec.Having.NotNull())
        {
            if (string.IsNullOrWhiteSpace(h.Op)) continue;
            if (map.TryGetValue(h.Op.Trim(), out var canonical) && !string.Equals(canonical, h.Op, System.StringComparison.Ordinal))
            {
                h.Op = canonical;
                mutated++;
            }
        }
        if (mutated > 0)
            ctx.Diagnostics.Add(new(Name, $"normalised {mutated} operator(s)"));
    }
}
