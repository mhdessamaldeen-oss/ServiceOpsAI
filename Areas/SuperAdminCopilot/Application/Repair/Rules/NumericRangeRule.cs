namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>Filters/RangeFilterFromQuestionPhase</c>. When the question contains a
/// numeric range cue ("between 5000 and 10000" / "more than 100000" / "less than 50") AND
/// the spec has no equivalent range filter, inject one on the root entity's preferred
/// numeric column. Numeric column preference comes from
/// <c>semantic-layer.json.defaults.numericColumnPreference</c> — universal.
/// </summary>
public sealed class NumericRangeRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.NumericRange;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var range = ctx.Linguistics.ExtractRange(ctx.Question);
        if (range is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Pick the first numeric column that exists on root from the preference list.
        var numericCol = ctx.Semantic.NumericColumnPreference
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && ctx.Schema.ColumnExists(spec.Root, c));
        if (string.IsNullOrEmpty(numericCol)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var qualified = spec.Root + "." + numericCol;

        // Skip if a comparable filter already exists.
        foreach (var f in spec.Filters)
            if (string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase)
                && IsRangeOp(f.Op))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting numeric range filter on {qualified}",
                          Payload: (qualified, range)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (string qual, NumericRange range)) return spec;
        if (range.Op == "between" && range.Min.HasValue && range.Max.HasValue)
        {
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "gte", Value = range.Min.Value });
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "lte", Value = range.Max.Value });
        }
        else if (range.Op == "gt" && range.Min.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "gt", Value = range.Min.Value });
        else if (range.Op == "gte" && range.Min.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "gte", Value = range.Min.Value });
        else if (range.Op == "lt" && range.Max.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "lt", Value = range.Max.Value });
        else if (range.Op == "lte" && range.Max.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qual, Op = "lte", Value = range.Max.Value });
        return spec;
    }

    private static bool IsRangeOp(string? op)
        => op is "between" or "gt" or "gte" or "lt" or "lte";
}
