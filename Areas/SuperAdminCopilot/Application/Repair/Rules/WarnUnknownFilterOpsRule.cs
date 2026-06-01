namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Defensive sanitizer: detects <see cref="FilterSpec.Op"/> values that aren't in the canonical
/// <see cref="SpecConstants.FilterOps.KnownOps"/> set. The WHERE-builder would silently drop
/// these filters (the operator switch has a default branch that does nothing useful for typos),
/// so the user gets a SQL without the filter they asked for — a quiet wrong-answer.
///
/// <para>This rule surfaces the typo:</para>
/// <list type="bullet">
///   <item>Apply rewrites the bad op to <c>eq</c> (the safest default) so the filter still
///         lands in the SQL. The user sees the (possibly imperfect) filter rather than nothing.</item>
///   <item>Detail string carries the original bad op + column so the trace shows what was
///         repaired — operators can grep <c>[copilot.rule_fired] rules=[…WarnUnknownFilterOps…]</c>
///         to spot which ops the LLM is hallucinating most often.</item>
/// </list>
///
/// <para>Conservative — only fires when the op is non-empty AND not in the known set AND not
/// the empty string (empty defaults to "eq" elsewhere, harmless).</para>
/// </summary>
public sealed class WarnUnknownFilterOpsRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.WarnUnknownFilterOps;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Filters.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var bad = new List<(int Index, string OriginalOp, string Column)>();
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            if (string.IsNullOrEmpty(f.Op)) continue;        // empty is harmless (defaults to eq)
            if (SuperAdminCopilot.Models.SpecConstants.FilterOps.KnownOps.Contains(f.Op)) continue;
            bad.Add((i, f.Op, f.Column ?? ""));
        }

        if (bad.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var summary = string.Join(", ", bad.Select(b => $"{b.Column}:{b.OriginalOp}→eq"));
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"rewrite {bad.Count} unknown filter op(s) to 'eq' [{summary}]",
                Payload: bad));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<(int Index, string OriginalOp, string Column)> bad
            || bad.Count == 0) return spec;

        var indexes = new HashSet<int>(bad.Select(b => b.Index));
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            if (!indexes.Contains(i)) continue;
            spec.Filters[i].Op = "eq";   // canonical fallback; SpecConstants.FilterOps.Eq value
        }
        return spec;
    }
}
