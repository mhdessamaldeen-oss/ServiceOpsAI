namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>EnforceCountOuterEntityPhase</c> + <c>Joins/CrossEntityCountRootInferencePhase</c>
/// + <c>Joins/ForceCountDistinctOnFanOutJoinPhase</c>.
///
/// <para>Two fixes under one fault class:</para>
/// <list type="bullet">
///   <item>"How many customers have X" — when the LLM picks the inner entity as root and emits
///         COUNT(*) over the fan-out, switch to COUNT(DISTINCT outer.Id).</item>
///   <item>1:N join with COUNT(*) and no DISTINCT — flip to COUNT(DISTINCT root.Id) so the
///         result counts root rows, not Cartesian-product rows.</item>
/// </list>
/// </summary>
public sealed class OuterEntityCountRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.OuterEntityCount;
    // 2026-06-01 — promoted Medium→Strong (audit). Join ARITHMETIC law, not a crutch: COUNT(*)
    // over a 1:N fan-out join double-counts the outer entity → must be COUNT(DISTINCT outer.Id).
    // Even strong models miss this Cartesian-overcount trap. Predicate-guarded: NoFault unless a
    // single COUNT(*) sits over a real join with a resolvable natural key.
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Aggregations.Count != 1) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Joins.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var agg = spec.Aggregations[0];
        if (!string.Equals(agg.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (agg.Distinct) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var nk = ctx.Semantic.NaturalKeyColumnFor(spec.Root) ?? "Id";
        if (!ctx.Schema.ColumnExists(spec.Root, nk)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var targetCol = spec.Root + "." + nk;
        if (string.Equals(agg.Column, targetCol, System.StringComparison.OrdinalIgnoreCase) && agg.Distinct)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"switching COUNT(*) → COUNT(DISTINCT {targetCol}) due to fan-out join(s)",
                          Payload: targetCol));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not string targetCol) return spec;
        var f = spec.Aggregations[0];
        f.Column = targetCol;
        f.Distinct = true;
        f.Alias = string.IsNullOrEmpty(f.Alias) ? "DistinctCount" : f.Alias;
        return spec;
    }
}
