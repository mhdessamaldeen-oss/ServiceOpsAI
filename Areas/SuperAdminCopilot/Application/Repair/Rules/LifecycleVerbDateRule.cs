namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>SwapDateColumnByVerbPhase</c>. When the question contains a lifecycle
/// verb (resolved / issued / paid / closed / created / started / ended / registered) AND
/// the spec's filters / order-by reference a different date column, swap them to the
/// verb-implied column. Mappings live in <c>semantic-layer.json</c> per entity's
/// <c>dateRoles</c> dictionary — universal, no hardcoded entity names.
/// </summary>
public sealed class LifecycleVerbDateRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.LifecycleVerbDate;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hit = ctx.Linguistics.ExtractLifecycleVerb(ctx.Question);
        if (hit is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var targetCol = ctx.Semantic.ResolveDateRoleForVerb(hit.Verb, spec.Root);
        if (string.IsNullOrEmpty(targetCol)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (!ctx.Schema.ColumnExists(spec.Root, targetCol)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var targetQual = spec.Root + "." + targetCol;

        // Find date-typed columns on root that differ from target — those are candidates for swap.
        var otherDateCols = ctx.Schema.ColumnsOf(spec.Root)
            .Where(c => (c.EndsWith("At", System.StringComparison.OrdinalIgnoreCase)
                         || c.EndsWith("Date", System.StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(c, targetCol, System.StringComparison.OrdinalIgnoreCase))
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        if (otherDateCols.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var swaps = new List<(SwapKind Kind, int Index)>();
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var (t, c) = SplitQualified(spec.Filters[i].Column);
            if (string.Equals(t, spec.Root, System.StringComparison.OrdinalIgnoreCase) && otherDateCols.Contains(c))
                swaps.Add((SwapKind.Filter, i));
        }
        for (int i = 0; i < spec.OrderBy.Count; i++)
        {
            var (t, c) = SplitQualified(spec.OrderBy[i].Column);
            if (string.Equals(t, spec.Root, System.StringComparison.OrdinalIgnoreCase) && otherDateCols.Contains(c))
                swaps.Add((SwapKind.OrderBy, i));
        }
        if (swaps.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"swapping {swaps.Count} ref(s) to {targetQual} (verb '{hit.Verb}')",
                Payload: (targetQual, swaps)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (string targetQual, List<(SwapKind Kind, int Index)> swaps)) return spec;
        foreach (var (kind, idx) in swaps)
        {
            if (kind == SwapKind.Filter) spec.Filters[idx].Column = targetQual;
            else                          spec.OrderBy[idx].Column = targetQual;
        }
        return spec;
    }

    private static (string Table, string Column) SplitQualified(string? col)
    {
        if (string.IsNullOrEmpty(col)) return ("", "");
        var dot = col.IndexOf('.');
        return dot <= 0 ? ("", col) : (col.Substring(0, dot).Trim('[', ']'), col.Substring(dot + 1).Trim('[', ']'));
    }

    private enum SwapKind { Filter, OrderBy }
}
