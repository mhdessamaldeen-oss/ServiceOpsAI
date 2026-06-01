namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Upgrades iteration 2's <c>DropUnresolvedSelectColumnsRule</c> partial fix to a full answer
/// for the hierarchical self-join pattern. When the LLM projects <c>ParentRegion.NameEn</c>
/// (or any <c>ParentX.Y</c> form) for a question like "list every region with its parent
/// region" and the root entity has a self-FK column, this rule INFERS the aliased self-join
/// instead of dropping the column. The compiler's new <see cref="JoinSpec.Alias"/> support
/// then emits <c>JOIN [Regions] AS [ParentRegion] ON [Regions].[ParentRegionId] = [ParentRegion].[Id]</c>
/// and the parent column is included in the result.
///
/// <para>Sibling to <c>DropUnresolvedSelectColumnsRule</c>: this rule runs FIRST. If it fires,
/// the dangling alias becomes a real join; the drop-rule then sees nothing left to drop.
/// If the LLM emitted some other unresolved reference (not a self-join), this rule no-ops
/// and the drop-rule still cleans up. Both rules together fix the pattern cleanly without
/// throwing or returning hallucinated data.</para>
///
/// <para>Conditions to fire:</para>
/// <list type="bullet">
///   <item>spec.Select has an entry <c>X.Y</c> where X is NOT root, NOT in spec.Joins, NOT a computed alias.</item>
///   <item>The root entity has a self-FK (a column that references its own primary key).</item>
///   <item>The unresolved table prefix X "looks like a self-alias" — heuristic: it starts with
///         "Parent", "Child", "Origin", "Target", "Self", or matches root's name with a
///         lifecycle prefix. We're conservative — only fire on clear self-join intent.</item>
/// </list>
/// </summary>
public sealed class InferSelfJoinFromUnresolvedAliasRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.InferSelfJoinFromUnresolvedAlias;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    // Heuristic prefixes that indicate self-join intent. Conservative — these are the prefixes
    // the LLM is genuinely most likely to emit for hierarchical questions.
    private static readonly string[] SelfJoinPrefixes =
        new[] { "Parent", "Child", "Origin", "Target", "Self", "Prior", "Previous", "Next" };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (spec.Select.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Find a self-FK on root (column referencing root's own primary key).
        var selfFk = ctx.Schema.ForeignKeysFrom(spec.Root)
            .FirstOrDefault(fk => string.Equals(fk.ReferencedTable, spec.Root, System.StringComparison.OrdinalIgnoreCase));
        if (selfFk is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // What table aliases are ALREADY resolved (root, declared joins, computed expressions).
        var resolved = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) resolved.Add(j.Table);
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Alias)) resolved.Add(j.Alias);
        foreach (var c in spec.Computed) if (!string.IsNullOrEmpty(c.Alias)) resolved.Add(c.Alias);

        // Find the first SELECT entry whose qualifier is a self-join-shaped alias on the root.
        string? aliasToCreate = null;
        foreach (var entry in spec.Select)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            var (table, _) = SplitQualified(entry);
            if (string.IsNullOrEmpty(table)) continue;
            if (resolved.Contains(table)) continue;
            // Heuristic: alias must start with a self-join prefix.
            if (!SelfJoinPrefixes.Any(p => table.StartsWith(p, System.StringComparison.OrdinalIgnoreCase))) continue;
            aliasToCreate = table;
            break;
        }

        if (aliasToCreate is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"inferring self-join '{spec.Root} AS {aliasToCreate}' (FK {selfFk.ParentColumn} → {selfFk.ReferencedColumn})",
                Payload: aliasToCreate));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not string aliasName || string.IsNullOrEmpty(aliasName)) return spec;
        // The compiler reads JoinSpec.Table to drive FK-graph resolution but emits the Alias.
        // For a self-join the FkEdge resolver sees root → root and picks the self-FK.
        // Avoid duplicate-join if a prior rule already added one with the same table+alias.
        var already = spec.Joins.Any(j => string.Equals(j.Table, spec.Root, System.StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(j.Alias, aliasName, System.StringComparison.OrdinalIgnoreCase));
        if (!already)
        {
            spec.Joins.Add(new JoinSpec { Table = spec.Root, Kind = "left", Alias = aliasName });
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
}
