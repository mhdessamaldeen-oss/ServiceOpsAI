namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>Filters/NegationFilterPhase</c>. When the question carries a negation cue
/// ("not in Damascus" / "except gas" / "excluding closed tickets") and a filter exists whose
/// value matches the negated noun, flip the filter's operator from <c>eq</c>/<c>in</c>/<c>like</c>
/// to <c>neq</c>/<c>notin</c>/<c>notlike</c>. Conservative: only fires when the negation cue
/// token is within ~30 characters of the matched value in the question text.
/// </summary>
public sealed class NegationFilterRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.NegationFilter;
    public PlannerTier MaxTier => PlannerTier.Weak;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Filters.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hit = ctx.Linguistics.ExtractNegation(ctx.Question);
        if (hit is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var q = ctx.Question ?? "";
        var flips = new List<int>();
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            if (f.Value is not string sv || string.IsNullOrEmpty(sv) || sv[0] == '@') continue;

            // Find the value's first occurrence in the question and check whether the
            // negation cue's end is within 30 characters before the value.
            var valuePos = q.IndexOf(sv, System.StringComparison.OrdinalIgnoreCase);
            if (valuePos < 0) continue;
            var negEnd = hit.Position + hit.TriggerPhrase.Length;
            if (negEnd > valuePos || valuePos - negEnd > 30) continue;

            var op = (f.Op ?? "eq").ToLowerInvariant();
            if (op == "eq" || op == "in" || op == "like") flips.Add(i);
        }
        if (flips.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"flipping {flips.Count} filter(s) due to '{hit.TriggerPhrase}'", Payload: flips));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<int> flips) return spec;
        foreach (var idx in flips)
        {
            var f = spec.Filters[idx];
            var newOp = (f.Op ?? "eq").ToLowerInvariant() switch
            {
                "eq"   => "neq",
                "in"   => "notin",
                "like" => "notlike",
                _      => f.Op,
            };
            spec.Filters[idx].Op = newOp;
        }
        return spec;
    }
}
