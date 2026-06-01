namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>EnsureDisplayColumnsPhase</c> + <c>EnrichSelectWithLabelsPhase</c>.
///
/// <para>For list-shape queries, expands SELECT to the root entity's display columns and
/// auto-projects FK labels for display-worthy FKs (target entity has a LabelColumn declared).
/// Skips when:</para>
/// <list type="bullet">
///   <item>Aggregations or PeriodComparisons present (not a list shape).</item>
///   <item>The same target table has MULTIPLE incoming FKs from the root (join key
///         ambiguous — Tickets has CreatedByUserId, AssignedToUserId, UpdatedByUserId all to
///         AspNetUsers; we don't pick one).</item>
/// </list>
/// </summary>
public sealed class DisplayColumnsMissingRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DisplayColumnsMissing;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Aggregations.Count > 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.PeriodComparisons.Count > 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var rootDisplay = ctx.Semantic.DisplayColumnsFor(spec.Root);
        if (rootDisplay.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var inSelect = new HashSet<string>(spec.Select, System.StringComparer.OrdinalIgnoreCase);
        var add = new List<string>();
        foreach (var dc in rootDisplay)
        {
            var qual = spec.Root + "." + dc;
            if (!inSelect.Contains(qual) && !inSelect.Contains(dc))
                add.Add(qual);
        }

        // FK labels — only when the target has a LabelColumn AND is the unique FK from root.
        var fkTargets = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var fk in ctx.Schema.ForeignKeysFrom(spec.Root))
        {
            if (string.IsNullOrEmpty(fk.ReferencedTable)) continue;
            fkTargets.TryGetValue(fk.ReferencedTable, out var c);
            fkTargets[fk.ReferencedTable] = c + 1;
        }
        var labelsToAdd = new List<(string Qual, string Target)>();
        foreach (var fk in ctx.Schema.ForeignKeysFrom(spec.Root))
        {
            if (string.IsNullOrEmpty(fk.ReferencedTable)) continue;
            if (fkTargets[fk.ReferencedTable] > 1) continue;    // ambiguous join key
            var label = ctx.Semantic.LabelColumnFor(fk.ReferencedTable);
            if (string.IsNullOrEmpty(label)) continue;
            var qual = fk.ReferencedTable + "." + label;
            if (!inSelect.Contains(qual)) labelsToAdd.Add((qual, fk.ReferencedTable));
        }

        if (add.Count == 0 && labelsToAdd.Count == 0)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"adding {add.Count} display + {labelsToAdd.Count} FK label(s)",
                          Payload: new Payload(add, labelsToAdd)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not Payload p) return spec;
        var existingJoins = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) existingJoins.Add(j.Table);

        foreach (var dc in p.Display) spec.Select.Add(dc);
        foreach (var (qual, target) in p.Labels)
        {
            spec.Select.Add(qual);
            if (!existingJoins.Contains(target))
            {
                spec.Joins.Add(new JoinSpec { Table = target, Kind = "left" });
                existingJoins.Add(target);
            }
        }
        return spec;
    }

    private sealed record Payload(List<string> Display, List<(string Qual, string Target)> Labels);
}
