namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Application.Repair.Semantic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>Aggregation/ApplyDerivedMetricHintPhase</c>. Detects question phrasings
/// that imply a derived metric ("average resolution time" → MTTR; "revenue" → SUM(TotalAmount);
/// "consumption" → SUM(UsageQuantity)). Metric definitions live in <c>semantic-layer.json</c>
/// per entity (deferred — schema not yet added). Currently a stub that no-ops; will become
/// effective when the JSON section is added in a follow-up pass.
/// </summary>
public sealed class DerivedMetricRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DerivedMetric;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hint = ctx.Semantic.ResolveDerivedMetric(ctx.Question, spec.Root);
        if (hint is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Skip when an aggregation already exists for this expression.
        foreach (var a in spec.Aggregations)
            if (!string.IsNullOrEmpty(a.Column)
                && a.Column.Contains(hint.Expression, System.StringComparison.OrdinalIgnoreCase))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting derived metric {hint.Function}({hint.Expression})", Payload: hint));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not DerivedMetricHint hint) return spec;
        spec.Aggregations.Add(new AggregateSpec
        {
            Function = hint.Function,
            Column = hint.Expression,
            Alias = hint.Alias,
        });
        return spec;
    }
}
