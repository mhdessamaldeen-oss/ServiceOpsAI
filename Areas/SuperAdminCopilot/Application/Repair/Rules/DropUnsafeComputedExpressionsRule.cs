namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;

/// <summary>
/// Targets the SUB-shape pipeline-crash pattern uncovered by trace ID 3 on 2026-05-30
/// ("Regions where ticket count exceeds the country-wide average"): the LLM emitted a
/// <c>Computed.Expression</c> containing a raw <c>SELECT … FROM …</c> subquery. The access-policy
/// validator (<see cref="QuerySpecAccessPolicyValidator"/>) correctly rejects this for safety,
/// but the rejection HARD-FAILS the entire pipeline — no partial answer, just a red error.
///
/// <para>This rule sits in SpecRepair (BEFORE the access-policy gate runs) and scrubs the
/// offending <see cref="QuerySpec.Computed"/> entries instead. Scrubbing one bad Computed
/// drops it from <see cref="QuerySpec.Select"/>, <see cref="QuerySpec.GroupBy"/>, and
/// <see cref="QuerySpec.Having"/> wherever its alias is referenced. The rest of the spec
/// stays intact, the validator is happy, and the user gets a partial answer (root + filters +
/// safe aggregations) instead of a hard refusal.</para>
///
/// <para>Single source of truth on what counts as "unsafe": the validator's
/// <see cref="QuerySpecAccessPolicyValidator.IsUnsafeExpression"/>. The rule and the gate
/// can never drift on which SQL keywords are disallowed.</para>
///
/// <para>This rule is INTENTIONALLY a "best-effort fail-graceful" pattern. The SUB shape
/// (multi-level aggregate) is documented as beyond 7B-model capacity — engineering goal here
/// is "don't crash the pipeline", not "make subqueries work". Stronger models won't trigger
/// this rule because they won't emit raw SQL into Computed in the first place.</para>
/// </summary>
public sealed class DropUnsafeComputedExpressionsRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.DropUnsafeComputedExpressions;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Computed.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var unsafeAliases = new List<string>();
        foreach (var c in spec.Computed)
        {
            if (string.IsNullOrEmpty(c.Alias)) continue;
            if (QuerySpecAccessPolicyValidator.IsUnsafeExpression(c.Expression))
                unsafeAliases.Add(c.Alias);
        }

        if (unsafeAliases.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"drop {unsafeAliases.Count} unsafe Computed entr{(unsafeAliases.Count == 1 ? "y" : "ies")} [{string.Join(", ", unsafeAliases)}]",
                Payload: unsafeAliases));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<string> unsafeAliases || unsafeAliases.Count == 0) return spec;
        var dropSet = new HashSet<string>(unsafeAliases, System.StringComparer.OrdinalIgnoreCase);

        // 1. Remove the offending Computed entries — those with a non-empty alias in the drop set.
        spec.Computed.RemoveAll(c => !string.IsNullOrEmpty(c.Alias) && dropSet.Contains(c.Alias));

        // 2. Remove any SELECT entry that references a dropped alias (bare or qualified).
        spec.Select.RemoveAll(s => IsAliasReference(s, dropSet));

        // 3. Remove any GROUP BY entry referencing a dropped alias.
        spec.GroupBy.RemoveAll(g => IsAliasReference(g, dropSet));

        // 4. Remove any HAVING entry whose column targets a dropped alias.
        spec.Having.RemoveAll(h => IsAliasReference(h.Column, dropSet));

        return spec;
    }

    // A reference matches a dropped alias if it's exactly the alias, or qualified as
    // <anything>.<alias>. We don't try to substring-match — that's too aggressive and could
    // strip legitimate columns whose name happens to contain the alias.
    private static bool IsAliasReference(string? value, HashSet<string> dropSet)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (dropSet.Contains(value)) return true;
        var dot = value.LastIndexOf('.');
        if (dot < 0) return false;
        return dropSet.Contains(value.Substring(dot + 1));
    }
}
