namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>EnsureOrderByForTopNPhase</c>. When the spec has a LIMIT but no ORDER BY,
/// inject a stable default order using the root entity's default date column (descending —
/// "newest first" is the analyst default) or fall back to the natural-key column ascending.
/// Prevents nondeterministic top-N results.
/// </summary>
public sealed class MissingOrderByForLimitRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingOrderByForLimit;
    public PlannerTier MaxTier => PlannerTier.Strong;     // even strong models forget OrderBy with LIMIT
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (!spec.Limit.HasValue || spec.Limit.Value <= 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.OrderBy.Count > 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Aggregations.Count > 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Prefer the default date column DESC; fall back to natural key ASC; then Id ASC.
        string? col = ctx.Semantic.GetDateColumn(spec.Root);
        string direction = "desc";
        if (string.IsNullOrEmpty(col) || !ctx.Schema.ColumnExists(spec.Root, col))
        {
            col = ctx.Semantic.NaturalKeyColumnFor(spec.Root);
            direction = "asc";
            if (string.IsNullOrEmpty(col) || !ctx.Schema.ColumnExists(spec.Root, col))
            {
                col = ctx.Schema.ColumnExists(spec.Root, "Id") ? "Id" : null;
                direction = "asc";
            }
        }
        if (string.IsNullOrEmpty(col)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var qualified = spec.Root + "." + col;
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting ORDER BY {qualified} {direction.ToUpperInvariant()} for LIMIT {spec.Limit}",
                          Payload: (qualified, direction)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (string qual, string dir)) return spec;
        spec.OrderBy.Add(new OrderBySpec { Column = qual, Direction = dir });
        return spec;
    }
}
