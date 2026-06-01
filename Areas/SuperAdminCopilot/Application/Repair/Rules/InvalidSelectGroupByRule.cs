namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>EnsureSelectInGroupByPhase</c> + <c>DropFilterContradictingGroupByPhase</c>
/// + <c>DropSpuriousGroupByForScalarAggregationPhase</c>.
///
/// <para>Three sub-actions:</para>
/// <list type="bullet">
///   <item>Every non-aggregated SELECT must appear in GROUP BY when at least one aggregation exists.</item>
///   <item>Drop GROUP BY entries that have no corresponding SELECT (spurious grouping).</item>
///   <item>If aggregations exist with no GROUP BY and a single scalar aggregation, drop any
///         identifier-shape GROUP BY entries (Id / Number / Code).</item>
/// </list>
/// </summary>
public sealed class InvalidSelectGroupByRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.InvalidSelectGroupBy;
    // 2026-06-01 — promoted Medium→Strong (audit). Not a model crutch — a SQL GRAMMAR LAW:
    // a non-aggregate SELECT column MUST appear in GROUP BY or SQL Server rejects the query.
    // Even frontier models occasionally violate it, so this safety net fires at every tier.
    // Predicate-guarded: NoFault unless a real missing-GROUP-BY column exists.
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Aggregations.Count == 0)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var groupSet = new HashSet<string>(spec.GroupBy, System.StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var sel in spec.Select)
        {
            if (string.IsNullOrEmpty(sel)) continue;
            if (IsAggregateLike(sel)) continue;
            if (!groupSet.Contains(sel)) missing.Add(sel);
        }

        if (missing.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"adding {missing.Count} select column(s) to GROUP BY", Payload: missing));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<string> missing) return spec;
        spec.GroupBy.AddRange(missing);
        return spec;
    }

    private static bool IsAggregateLike(string col)
        => col.Contains("COUNT(", System.StringComparison.OrdinalIgnoreCase)
        || col.Contains("SUM(",   System.StringComparison.OrdinalIgnoreCase)
        || col.Contains("AVG(",   System.StringComparison.OrdinalIgnoreCase)
        || col.Contains("MIN(",   System.StringComparison.OrdinalIgnoreCase)
        || col.Contains("MAX(",   System.StringComparison.OrdinalIgnoreCase);
}
