namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Targets the <c>distinct:true + aggregations</c> contradiction that
/// <see cref="SuperAdminCopilot.Pipeline.Stages.SqlIntentGuard"/>'s
/// <c>CheckDistinctWithAggregations</c> refuses on. DISTINCT on a grouped/aggregated result is
/// always degenerate — the GROUP BY (explicit or implicit via aggregations) already produces
/// one row per group, so DISTINCT is either redundant or it's the LLM forgetting to choose
/// between "unique projection" and "grouped totals".
///
/// <para>This rule simply clears <c>Distinct=false</c> when both flags are set — aggregations
/// win. Result: pipeline doesn't refuse, the user gets the grouped/aggregated answer they
/// most likely intended. Pre-empts the gate's refusal at the SpecRepair stage.</para>
/// </summary>
public sealed class DropDistinctWhenAggregatedRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DropDistinctWhenAggregated;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        // Shared predicate with SqlIntentGuard.CheckDistinctWithAggregations. Both call the
        // same QuerySpecShapePredicates helper (v2 + v3 overloads colocated) — no drift risk.
        if (!QuerySpecShapePredicates.IsDistinctWithAggregations(spec))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                "clear distinct=true (aggregations already produce one row per group)",
                Payload: null));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        spec.Distinct = false;
        return spec;
    }
}
