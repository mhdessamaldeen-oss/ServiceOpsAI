namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>DedupAggregationsPhase</c> + <c>DedupContradictoryFiltersPhase</c>. Removes:
/// <list type="bullet">
///   <item>Duplicate aggregations (same Function + Column + Distinct) — keep first.</item>
///   <item>Exact-duplicate filters (same Column + Op + Value) — keep first.</item>
///   <item>Contradictory eq filter on a column that also has an <c>in</c> filter — keep the IN.</item>
/// </list>
/// Conservative: only removes filters when redundancy / contradiction is unambiguous.
/// </summary>
public sealed class FilterDedupeRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.FilterDedupe;
    public PlannerTier MaxTier => PlannerTier.Strong;     // benefit at every tier
    // Runs late so other rules have inserted/modified the filters/aggs first.
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[]
        {
            RepairFaultKind.MissingRoot,
            RepairFaultKind.DanglingColumnReference,
            RepairFaultKind.MissingLookupFilter,
        };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        var aggDropIdx = new List<int>();
        var seenAggs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < spec.Aggregations.Count; i++)
        {
            var a = spec.Aggregations[i];
            var key = $"{a.Function}|{a.Column}|{a.Distinct}";
            if (!seenAggs.Add(key)) aggDropIdx.Add(i);
        }

        var filterDropIdx = new List<int>();
        var seenFilters = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        // First pass — exact duplicates.
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            var key = $"{f.Column}|{f.Op}|{f.Value}";
            if (!seenFilters.Add(key)) filterDropIdx.Add(i);
        }
        // Second pass — contradictions: eq with in on same column.
        var inColumns = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < spec.Filters.Count; i++)
            if (!string.IsNullOrEmpty(spec.Filters[i].Column)
                && string.Equals(spec.Filters[i].Op, "in", System.StringComparison.OrdinalIgnoreCase))
                inColumns.Add(spec.Filters[i].Column);
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            if (string.Equals(f.Op, "eq", System.StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(f.Column) && inColumns.Contains(f.Column)
                && !filterDropIdx.Contains(i))
                filterDropIdx.Add(i);
        }

        if (aggDropIdx.Count == 0 && filterDropIdx.Count == 0)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"dropping {aggDropIdx.Count} dup agg(s) + {filterDropIdx.Count} dup/contradictory filter(s)",
                Payload: (aggDropIdx, filterDropIdx)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (List<int> aggIdx, List<int> filterIdx)) return spec;
        foreach (var i in aggIdx.OrderByDescending(x => x)) spec.Aggregations.RemoveAt(i);
        foreach (var i in filterIdx.OrderByDescending(x => x)) spec.Filters.RemoveAt(i);
        return spec;
    }
}
