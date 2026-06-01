namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>MapValueSynonymsPhase</c>. Canonicalises filter values via the semantic
/// layer's synonym map: <c>"urgent"</c> → <c>"Critical"</c>, <c>"pending payment"</c> →
/// <c>"Issued"</c>. Synonyms live in <c>semantic-layer.json</c> per column — universal, no
/// hardcoded vocab here.
/// </summary>
public sealed class ValueSynonymRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.ValueSynonym;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Filters.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        var rewrites = new List<(int Index, object? NewValue)>();
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            if (string.IsNullOrEmpty(f.Column)) continue;
            var current = f.Value?.ToString();
            if (string.IsNullOrEmpty(current)) continue;
            var canonical = ctx.Semantic.ResolveSynonymValue(f.Column, current);
            if (!string.Equals(canonical, current, System.StringComparison.Ordinal))
                rewrites.Add((i, canonical));
        }
        if (rewrites.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"canonicalising {rewrites.Count} synonym value(s)", Payload: rewrites));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<(int Index, object? NewValue)> rewrites) return spec;
        foreach (var (idx, val) in rewrites)
            spec.Filters[idx].Value = val;
        return spec;
    }
}
