namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Targets the self-join hierarchical failure pattern uncovered by the 2026-05-30 deep-dive
/// analyzer: the LLM projects "ParentRegion.NameEn" or "ParentCategory.Name" expecting a
/// self-join alias, but never adds the corresponding join (and the QuerySpec / compiler don't
/// support aliased joins today). Result: SQL Server throws "The multi-part identifier
/// 'ParentRegion.NameEn' could not be bound" — 3 of 11 SQL-error cases in baseline 190.
///
/// <para>This rule scans SELECT entries with a table prefix; if the prefix doesn't match
/// <see cref="QuerySpec.Root"/>, any joined table, any computed alias, or a known FK target
/// of the root, the entry is dropped. Effect: the user gets a partial-but-correct answer
/// (root + direct joins + computed) instead of a hard SQL error.</para>
///
/// <para>This rule is INTENTIONALLY conservative — it only fires on entries that cannot be
/// resolved against the known FROM/JOIN universe. Legitimate FK label projections that the
/// compiler will auto-join (via the FK graph) are NOT dropped because their target IS a
/// reachable FK target from the root.</para>
/// </summary>
public sealed class DropUnresolvedSelectColumnsRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DropUnresolvedSelectColumns;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Select.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Build the resolved-table universe: Root + every Joins[].Table + every FK target
        // reachable from Root + every Computed alias (computed expressions emit "AS [alias]").
        var resolved = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table)) resolved.Add(j.Table);
        foreach (var fk in ctx.Schema.ForeignKeysFrom(spec.Root))
            if (!string.IsNullOrEmpty(fk.ReferencedTable)) resolved.Add(fk.ReferencedTable);
        foreach (var c in spec.Computed)
            if (!string.IsNullOrEmpty(c.Alias)) resolved.Add(c.Alias);

        var indicesToDrop = new List<int>();
        for (int i = 0; i < spec.Select.Count; i++)
        {
            var entry = spec.Select[i];
            if (string.IsNullOrEmpty(entry)) continue;
            var (table, _) = SplitQualified(entry);
            if (string.IsNullOrEmpty(table)) continue;              // unqualified — DanglingColumnReferenceRule handles
            if (resolved.Contains(table)) continue;                 // resolved — keep
            indicesToDrop.Add(i);
        }

        if (indicesToDrop.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        var dropped = indicesToDrop.Select(i => spec.Select[i]).ToList();
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"dropping {indicesToDrop.Count} unresolved select column(s): {string.Join(", ", dropped)}",
                Payload: indicesToDrop));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<int> indices || indices.Count == 0) return spec;
        // Descending to keep earlier indices stable.
        foreach (var i in indices.OrderByDescending(x => x))
            if (i >= 0 && i < spec.Select.Count) spec.Select.RemoveAt(i);
        return spec;
    }

    private static (string Table, string Column) SplitQualified(string qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx < 0) return ("", qualified);
        return (qualified.Substring(0, idx), qualified.Substring(idx + 1));
    }
}
