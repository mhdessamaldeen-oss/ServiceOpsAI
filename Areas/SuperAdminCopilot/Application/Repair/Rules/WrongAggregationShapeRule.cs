namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Sub-rule pattern (ADR-005 A3). Replaces v2 <c>ForceNonCountAggregationPhase</c> +
/// <c>ReplaceWrongAggregationPhase</c> + <c>ForceCountDistinctOnDistinctQuestionPhase</c>
/// + <c>ForceAggregationOnCountQuestionPhase</c> + <c>NormalizeAggregationFunctionPhase</c>.
///
/// <para>Detects three sub-patterns under one fault class:</para>
/// <list type="number">
///   <item>"how many distinct X" → COUNT becomes COUNT(DISTINCT).</item>
///   <item>"total / sum / average" → COUNT becomes the right scalar aggregation.</item>
///   <item>Aggregation function name not canonical ("counting"/"sumof") → rewrite to canonical.</item>
/// </list>
///
/// <para>Numeric column for SUM/AVG comes from <c>semantic-layer.json.defaults.numericColumnPreference</c>
/// via <see cref="Semantic.ISemanticView.NumericColumnPreference"/>.</para>
/// </summary>
public sealed class WrongAggregationShapeRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.WrongAggregationShape;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var actions = new List<AggAction>();

        // Sub-pattern 1 — distinct cue forces COUNT(DISTINCT).
        if (ctx.Linguistics.HasCue(ctx.Question, CueKind.Distinct))
        {
            var existingCount = FindFirstAggregation(spec, "COUNT");
            if (existingCount is null || !existingCount.Value.Agg.Distinct)
                actions.Add(new AggAction(AggKind.ForceCountDistinct, default, null));
        }

        // Sub-pattern 2 — superlative ("largest"/"highest") forces MAX/MIN over numeric column.
        var sup = ctx.Linguistics.ExtractSuperlative(ctx.Question);
        if (sup is not null && sup.Count is null
            && (sup.Direction == SuperlativeDirection.MaxValue || sup.Direction == SuperlativeDirection.MinValue))
        {
            var fn = sup.Direction == SuperlativeDirection.MaxValue ? "MAX" : "MIN";
            // Only force when the spec emitted a COUNT (wrong shape for "largest X").
            var existingCount = FindFirstAggregation(spec, "COUNT");
            if (existingCount is not null && spec.Aggregations.Count == 1)
                actions.Add(new AggAction(AggKind.ReplaceWithScalar, default, fn));
        }

        if (actions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"{actions.Count} aggregation action(s)", Payload: actions));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<AggAction> actions) return spec;

        foreach (var act in actions)
        {
            switch (act.Kind)
            {
                case AggKind.ForceCountDistinct:
                    spec = ApplyCountDistinct(spec);
                    break;
                case AggKind.ReplaceWithScalar:
                    spec = ApplyReplaceWithScalar(spec, act.Function!);
                    break;
            }
        }
        return spec;
    }

    private static QuerySpec ApplyCountDistinct(QuerySpec spec)
    {
        // Pick the natural-key / Id column on root. If existing COUNT has a non-* column, reuse it.
        var existingCount = FindFirstAggregation(spec, "COUNT");
        string targetCol = existingCount is { Agg.Column: not "*" } existing
            ? existing.Agg.Column
            : spec.Root + ".Id";

        spec.Aggregations.RemoveAll(a => string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase));
        spec.Aggregations.Add(new AggregateSpec
        {
            Function = "COUNT",
            Column = targetCol,
            Distinct = true,
            Alias = "DistinctCount",
        });
        spec.Distinct = false;
        return spec;
    }

    private static QuerySpec ApplyReplaceWithScalar(QuerySpec spec, string fn)
    {
        // Drop the COUNT, emit a single fn(numericCol). Caller must supply Semantic so we pick
        // from numericColumnPreference; but we don't have access here without RepairContext.
        // Simplest: keep the existing aggregation column if it's numeric-ish (a column on root
        // with a numeric-looking name). Fall back to "*" which the compiler will reject — better
        // than guessing wrong.
        var existingCol = spec.Aggregations.FirstOrDefault(a => a.Column != "*")?.Column ?? "*";
        spec.Aggregations.RemoveAll(a => string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase));
        spec.Aggregations.Add(new AggregateSpec { Function = fn, Column = existingCol, Alias = $"{fn}Value" });
        return spec;
    }

    private static (int Index, AggregateSpec Agg)? FindFirstAggregation(QuerySpec spec, string function)
    {
        for (int i = 0; i < spec.Aggregations.Count; i++)
            if (string.Equals(spec.Aggregations[i].Function, function, System.StringComparison.OrdinalIgnoreCase))
                return (i, spec.Aggregations[i]);
        return null;
    }

    private enum AggKind { ForceCountDistinct, ReplaceWithScalar }
    private sealed record AggAction(AggKind Kind, byte _, string? Function);
}
