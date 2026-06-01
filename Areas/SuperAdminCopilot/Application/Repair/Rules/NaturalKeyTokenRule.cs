namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>InjectNaturalKeyFilterFromQuestionPhase</c>. When the question contains a
/// token matching an entity's <c>naturalKeyFormat</c> regex (e.g. "TKT-00050", "SA-100",
/// "OUT-2025-100", "PAY-2025-9999"), inject a filter on the entity's natural-key column.
/// Universal — every (table, naturalKeyColumn, formatRegex) lives in
/// <c>semantic-layer.json</c>.
/// </summary>
public sealed class NaturalKeyTokenRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.NaturalKeyToken;
    // 2026-06-01 — demoted Strong→Weak (audit). Pure weak-NLU crutch: detects a bare natural-key
    // token ("TKT-00050") in the question text and injects a filter. A capable model reads the
    // token and filters on it itself, so firing this on Medium/Strong is wasted work that can
    // override a correct model choice. Fires only at Weak now.
    public PlannerTier MaxTier => PlannerTier.Weak;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var formats = ctx.Semantic.NaturalKeyFormats;
        if (formats.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hit = ctx.Linguistics.ExtractNaturalKeyToken(ctx.Question, formats);
        if (hit is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Only inject when the matched format belongs to spec.Root — otherwise the LLM
        // chose a different root and this rule shouldn't override it.
        if (!string.Equals(hit.Table, spec.Root, System.StringComparison.OrdinalIgnoreCase))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var qualified = spec.Root + "." + hit.NaturalKeyColumn;
        foreach (var f in spec.Filters)
            if (string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting {qualified}='{hit.Token}'", Payload: (qualified, hit.Token)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (string qual, string token)) return spec;
        return spec.AddFilter(new FilterSpec { Column = qual, Op = "eq", Value = token });
    }
}
