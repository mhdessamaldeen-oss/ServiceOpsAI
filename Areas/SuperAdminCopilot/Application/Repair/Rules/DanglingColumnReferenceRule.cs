namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>AutoQualifyColumnsPhase</c> + <c>DropAggregatedColumnsFromSelectPhase</c>.
///
/// <para>Detects two patterns:</para>
/// <list type="bullet">
///   <item>Unqualified column references ("Status" instead of "Tickets.Status") — qualifies
///         against the root entity when the column exists there.</item>
///   <item>SELECT entries that are now redundant because an aggregation covers the same
///         column — drops them to avoid GROUP BY contradictions.</item>
/// </list>
/// </summary>
public sealed class DanglingColumnReferenceRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DanglingColumnReference;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var actions = new List<DanglingAction>();

        // Pattern 1 — qualify bare column references on the root.
        for (int i = 0; i < spec.Filters.Count; i++)
            if (NeedsQualify(spec.Filters[i].Column, spec.Root, ctx.Schema))
                actions.Add(new DanglingAction(DanglingKind.QualifyFilter, i, default));
        for (int i = 0; i < spec.OrderBy.Count; i++)
            if (NeedsQualify(spec.OrderBy[i].Column, spec.Root, ctx.Schema))
                actions.Add(new DanglingAction(DanglingKind.QualifyOrderBy, i, default));
        for (int i = 0; i < spec.Aggregations.Count; i++)
        {
            var col = spec.Aggregations[i].Column;
            if (col != "*" && NeedsQualify(col, spec.Root, ctx.Schema))
                actions.Add(new DanglingAction(DanglingKind.QualifyAgg, i, default));
        }
        for (int i = 0; i < spec.GroupBy.Count; i++)
            if (NeedsQualify(spec.GroupBy[i], spec.Root, ctx.Schema))
                actions.Add(new DanglingAction(DanglingKind.QualifyGroupBy, i, default));

        // Pattern 2 — drop SELECT entries covered by an aggregation.
        var aggCols = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var a in spec.Aggregations)
            if (!string.IsNullOrEmpty(a.Column) && a.Column != "*") aggCols.Add(a.Column);
        for (int i = spec.Select.Count - 1; i >= 0; i--)
            if (!string.IsNullOrEmpty(spec.Select[i]) && aggCols.Contains(spec.Select[i]))
                actions.Add(new DanglingAction(DanglingKind.DropAggregatedSelect, i, default));

        if (actions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"{actions.Count} dangling action(s)", Payload: actions));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<DanglingAction> actions) return spec;

        foreach (var act in actions)
        {
            switch (act.Kind)
            {
                case DanglingKind.QualifyFilter:
                    spec.Filters[act.Index].Column = spec.Root + "." + spec.Filters[act.Index].Column;
                    break;
                case DanglingKind.QualifyOrderBy:
                    spec.OrderBy[act.Index].Column = spec.Root + "." + spec.OrderBy[act.Index].Column;
                    break;
                case DanglingKind.QualifyAgg:
                    spec.Aggregations[act.Index].Column = spec.Root + "." + spec.Aggregations[act.Index].Column;
                    break;
                case DanglingKind.QualifyGroupBy:
                    spec.GroupBy[act.Index] = spec.Root + "." + spec.GroupBy[act.Index];
                    break;
            }
        }
        // Drops process descending so indices stay stable after each RemoveAt.
        foreach (var act in actions.Where(a => a.Kind == DanglingKind.DropAggregatedSelect)
                                   .OrderByDescending(a => a.Index))
            spec.Select.RemoveAt(act.Index);

        return spec;
    }

    private static bool NeedsQualify(string? colRef, string root, Schema.ISchemaView schema)
    {
        if (string.IsNullOrEmpty(colRef) || colRef == "*") return false;
        if (colRef.Contains('.')) return false;
        return schema.ColumnExists(root, colRef);
    }

    private enum DanglingKind { QualifyFilter, QualifyOrderBy, QualifyAgg, QualifyGroupBy, DropAggregatedSelect }
    private sealed record DanglingAction(DanglingKind Kind, int Index, byte _);
}
