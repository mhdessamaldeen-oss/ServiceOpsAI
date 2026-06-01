namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Application.Repair.Semantic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>ApplyConceptPatternsPhase</c>. Matches question phrasings declared in
/// <c>semantic-layer.json</c>'s <c>defaults.conceptPatterns</c> (e.g. "overdue" / "stale" /
/// "backlog") and injects the declared filters. Adding a new concept is a JSON edit —
/// nothing here is hardcoded.
/// </summary>
public sealed class ConceptPatternRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.ConceptPattern;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var matches = ctx.Semantic.MatchConceptPatterns(ctx.Question, spec.Root);
        if (matches.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Collect filters that aren't already on the spec.
        var toInject = new List<FilterSpec>();
        foreach (var match in matches)
        {
            foreach (var cf in match.Filters)
            {
                if (string.IsNullOrEmpty(cf.Column)) continue;
                var qual = cf.Column.Contains('.') ? cf.Column : spec.Root + "." + cf.Column;
                bool already = false;
                foreach (var f in spec.Filters)
                    if (string.Equals(f.Column, qual, System.StringComparison.OrdinalIgnoreCase)
                        && string.Equals(f.Op, cf.Op, System.StringComparison.OrdinalIgnoreCase))
                    { already = true; break; }
                if (already) continue;
                toInject.Add(new FilterSpec { Column = qual, Op = cf.Op ?? "eq", Value = cf.Value });
            }
        }
        if (toInject.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting {toInject.Count} concept-pattern filter(s)", Payload: toInject));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<FilterSpec> filters) return spec;
        spec.Filters.AddRange(filters);
        return spec;
    }
}
