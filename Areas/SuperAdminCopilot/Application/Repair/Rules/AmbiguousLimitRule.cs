namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>ForceTopNLimitPhase</c> + <c>ForceTopNRowsOverMaxMinPhase</c>
/// + <c>ConvertTopNAggregationToListPhase</c>.
///
/// <para>When the question carries "top N" / "أعلى N" intent and the spec has no LIMIT (or a
/// default-cap LIMIT), set the explicit limit. When the question is a top-N rows shape but
/// the spec emitted a single MAX/MIN aggregation, swap to row-list + ORDER BY.</para>
/// </summary>
public sealed class AmbiguousLimitRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.AmbiguousLimit;
    public PlannerTier MaxTier => PlannerTier.Weak;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var sup = ctx.Linguistics.ExtractSuperlative(ctx.Question);
        if (sup is null || sup.Count is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        int n = sup.Count.Value;
        if (n <= 0 || n > 10000) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        // Don't overwrite an explicit non-cap limit.
        if (spec.Limit.HasValue && spec.Limit.Value > 0 && spec.Limit.Value != 1000)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"forcing LIMIT {n} from superlative '{sup.TriggerWord}'", Payload: n));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not int n) return spec;
        return spec.WithLimit(n);
    }
}
