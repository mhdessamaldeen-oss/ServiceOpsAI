namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Targets the dominant failure pattern from the 2026-05-30 deep-dive analyzer: when the LLM
/// emits an aggregation grouped by an FK column, it projects the FK's NUMERIC ID instead of
/// the target entity's human-readable LABEL column. Result: tables full of CustomerId=42 /
/// DepartmentId=7 / RegionId=12 instead of "Customer = Houri / Department = Damascus Electricity
/// / Region = Aleppo" — 31.6% of baseline-190 failures had this shape.
///
/// <para>This rule recognises that shape and swaps the FK id projection + group-by for the
/// target's label column projection + group-by. Sibling to <see cref="DisplayColumnsMissingRule"/>
/// (which skips on aggregations); this rule handles the AGGREGATION case.</para>
///
/// <para>Conditions for the rule to fire:</para>
/// <list type="bullet">
///   <item>spec has aggregations</item>
///   <item>spec groups by an FK column (root.FkColumn) where root is spec.Root</item>
///   <item>FK target table has a LabelColumn declared in the semantic layer</item>
///   <item>the FK is unique from root to target (no ambiguous join key)</item>
///   <item>the label column is NOT already in the SELECT or GROUP BY</item>
/// </list>
///
/// <para>Apply: replace `root.FkColumn` in SELECT and GROUP BY with `target.LabelColumn`,
/// and ensure a LEFT JOIN to target is present.</para>
/// </summary>
public sealed class ProjectLabelForFkGroupByRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.ProjectLabelForFkGroupBy;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Aggregations.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.GroupBy.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Build FK target counts so we skip ambiguous keys (e.g. Tickets has multiple FKs to
        // AspNetUsers — CreatedBy / AssignedTo / UpdatedBy; we can't pick one).
        var rootFks = ctx.Schema.ForeignKeysFrom(spec.Root);
        if (rootFks.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        var targetCounts = rootFks
            .GroupBy(fk => fk.ReferencedTable, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), System.StringComparer.OrdinalIgnoreCase);

        var inSelect = new HashSet<string>(spec.Select, System.StringComparer.OrdinalIgnoreCase);
        var actions = new List<LabelSwap>();
        for (int i = 0; i < spec.GroupBy.Count; i++)
        {
            var gb = spec.GroupBy[i];
            if (string.IsNullOrEmpty(gb)) continue;
            var (table, col) = SplitQualified(gb);
            if (!string.Equals(table, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;

            var fk = rootFks.FirstOrDefault(f => string.Equals(f.ParentColumn, col, System.StringComparison.OrdinalIgnoreCase));
            if (fk is null) continue;
            if (targetCounts.TryGetValue(fk.ReferencedTable, out var c) && c > 1) continue;   // ambiguous

            var label = ctx.Semantic.LabelColumnFor(fk.ReferencedTable);
            if (string.IsNullOrEmpty(label)) continue;
            if (!ctx.Schema.ColumnExists(fk.ReferencedTable, label)) continue;

            var labelQual = fk.ReferencedTable + "." + label;
            // Skip when the label is already projected and grouped — the LLM got it right.
            if (inSelect.Contains(labelQual)) continue;

            actions.Add(new LabelSwap(
                FkColumnInGroupBy: gb,
                FkColumnIndex: i,
                TargetTable: fk.ReferencedTable,
                LabelColumn: label!,
                LabelQualified: labelQual));
        }

        if (actions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        // Per-swap attribution: the SpecRepair bus already logs Detail at LogInformation level
        // for every rule that fires (see Pipeline/SpecRepair/SpecRepair.cs). Including the
        // FK column + label column per swap lets quality-loop trace analysis count which
        // (root, fk_column) shapes the rule actually targets at population scale.
        var swapList = string.Join(", ", actions.Select(a =>
            $"{a.FkColumnInGroupBy}→{a.LabelQualified}"));
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"swap {actions.Count} FK id(s) for label projection in aggregate-with-groupBy [{swapList}]",
                Payload: actions));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<LabelSwap> actions || actions.Count == 0) return spec;

        var existingJoins = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) existingJoins.Add(j.Table);

        foreach (var a in actions)
        {
            // 1. Add label to SELECT if not already.
            if (!spec.Select.Any(s => string.Equals(s, a.LabelQualified, System.StringComparison.OrdinalIgnoreCase)))
                spec.Select.Add(a.LabelQualified);

            // 2. Replace FK column in GROUP BY with label column.
            //    Search by value (the FK column qualified string from spec).
            for (int gi = 0; gi < spec.GroupBy.Count; gi++)
            {
                if (string.Equals(spec.GroupBy[gi], a.FkColumnInGroupBy, System.StringComparison.OrdinalIgnoreCase))
                {
                    spec.GroupBy[gi] = a.LabelQualified;
                    break;
                }
            }

            // 3. Drop the bare FK column from SELECT if it's there (we projected the label instead).
            for (int si = spec.Select.Count - 1; si >= 0; si--)
            {
                if (string.Equals(spec.Select[si], a.FkColumnInGroupBy, System.StringComparison.OrdinalIgnoreCase))
                {
                    spec.Select.RemoveAt(si);
                }
            }

            // 4. Ensure LEFT JOIN to the target table is present (the existing joins list should
            //    pick up INNER/LEFT/etc.; we only ADD a join if no entry references the target).
            if (!existingJoins.Contains(a.TargetTable))
            {
                spec.Joins.Add(new JoinSpec { Table = a.TargetTable, Kind = "left" });
                existingJoins.Add(a.TargetTable);
            }
        }

        return spec;
    }

    private static (string Table, string Column) SplitQualified(string qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx < 0) return ("", qualified);
        return (qualified.Substring(0, idx), qualified.Substring(idx + 1));
    }

    private sealed record LabelSwap(
        string FkColumnInGroupBy,
        int FkColumnIndex,
        string TargetTable,
        string LabelColumn,
        string LabelQualified);
}
