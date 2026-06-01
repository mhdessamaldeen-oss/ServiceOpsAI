namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>ConvertFkEqualsNameToJoinPhase</c> + <c>ConvertNameFilterToLikePhase</c>.
///
/// <para>Two sub-actions:</para>
/// <list type="number">
///   <item><b>FK eq string redirect.</b> When a filter is <c>WHERE FK_Id = 'Houri'</c>, redirect
///         it to <c>WHERE FK.LabelColumn = 'Houri'</c> and add the join to the FK target. Driven
///         by FK graph + semantic-layer <c>LabelColumn</c>.</item>
///   <item><b>Eq → like on searchable columns.</b> When a filter is <c>WHERE FullName = 'Houri'</c>
///         on a column declared <c>searchable</c>, convert to <c>LIKE '%Houri%'</c>.</item>
/// </list>
/// </summary>
public sealed class FkNameToJoinRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.FkNameToJoin;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Filters.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var actions = new List<FkNameAction>();
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            if (string.IsNullOrEmpty(f.Column)) continue;
            if (!string.Equals(f.Op, "eq", System.StringComparison.OrdinalIgnoreCase)) continue;
            var valStr = f.Value?.ToString();
            if (string.IsNullOrWhiteSpace(valStr)) continue;
            if (decimal.TryParse(valStr, out _)) continue;
            if (System.Guid.TryParse(valStr, out _)) continue;

            var (table, col) = SplitQualified(f.Column);
            if (string.IsNullOrEmpty(table)) continue;

            // Action 1 — FK eq string: f.Column is an FK column referencing another table that
            // has a LabelColumn.
            var fk = ctx.Schema.ForeignKeysFrom(table)
                .FirstOrDefault(e => string.Equals(e.ParentColumn, col, System.StringComparison.OrdinalIgnoreCase));
            if (fk is not null)
            {
                var label = ctx.Semantic.LabelColumnFor(fk.ReferencedTable);
                if (!string.IsNullOrEmpty(label) && ctx.Schema.ColumnExists(fk.ReferencedTable, label))
                {
                    actions.Add(new FkNameAction(FkActionKind.FkRedirect, i, fk.ReferencedTable, label!));
                    continue;     // skip the like check for the same filter
                }
            }

            // Action 2 — eq on a searchable column: convert to LIKE '%value%'.
            var searchable = ctx.Semantic.SearchableColumnsFor(table);
            if (searchable.Any(s => string.Equals(s, col, System.StringComparison.OrdinalIgnoreCase)))
            {
                if (!valStr.Contains('%') && !valStr.Contains('_') && valStr.Length <= 80)
                    actions.Add(new FkNameAction(FkActionKind.EqToLike, i, "", ""));
            }
        }
        if (actions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"{actions.Count} FK/name action(s)", Payload: actions));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<FkNameAction> actions) return spec;
        var existingJoinTables = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) existingJoinTables.Add(j.Table);

        foreach (var act in actions)
        {
            var f = spec.Filters[act.Index];
            switch (act.Kind)
            {
                case FkActionKind.FkRedirect:
                    spec.Filters[act.Index].Column = act.TargetTable + "." + act.TargetColumn;
                    if (!existingJoinTables.Contains(act.TargetTable))
                    {
                        spec.Joins.Add(new JoinSpec { Table = act.TargetTable, Kind = "inner" });
                        existingJoinTables.Add(act.TargetTable);
                    }
                    break;
                case FkActionKind.EqToLike:
                    spec.Filters[act.Index].Op = "like";
                    spec.Filters[act.Index].Value = "%" + f.Value + "%";
                    break;
            }
        }
        return spec;
    }

    private static (string Table, string Column) SplitQualified(string col)
    {
        var dot = col.IndexOf('.');
        return dot <= 0 ? ("", col) : (col.Substring(0, dot).Trim('[', ']'), col.Substring(dot + 1).Trim('[', ']'));
    }

    private enum FkActionKind { FkRedirect, EqToLike }
    private sealed record FkNameAction(FkActionKind Kind, int Index, string TargetTable, string TargetColumn);
}
