namespace SuperAdminCopilot.Application.Repair;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Runs all <see cref="IRepairRule"/> implementations against a spec, in topological order
/// derived from each rule's <see cref="IRepairRule.Requires"/> list. Produces a typed result
/// containing the repaired spec + the diagnoses applied + any faults raised. See ADR-005.
/// </summary>
public sealed class RepairBus
{
    private readonly IReadOnlyList<IRepairRule> _rules;

    public RepairBus(IEnumerable<IRepairRule> rules)
    {
        _rules = TopoSort(rules.ToList());
    }

    public RepairResult Run(QuerySpec spec, RepairContext ctx)
    {
        var current = spec;
        var applied = new List<DiagnosisRecord>();
        var faults = new List<Fault>();
        var hashBefore = current.StructuralHash();

        foreach (var rule in _rules)
        {
            if ((int)ctx.ActiveTier > (int)rule.MaxTier) continue;
            var detected = rule.Detect(current, ctx);
            if (detected.IsFault) { faults.Add(detected.Fault); continue; }
            var dx = detected.Value;
            if (dx.Kind == RepairFaultKind.None) continue;

            var hashIn = current.StructuralHash();
            current = rule.Apply(current, dx);
            var hashOut = current.StructuralHash();
            applied.Add(new DiagnosisRecord(rule.GetType().Name, dx, hashIn, hashOut));
        }

        return new RepairResult(current, applied, faults, hashBefore, current.StructuralHash());
    }

    /// <summary>Topological sort by <see cref="IRepairRule.Requires"/>. Throws on cycle OR
    /// on an unmet <c>Requires</c> (the dependency contract is fiction if it silently drops).
    /// Both are programmer errors we want loud at startup, not silent.</summary>
    private static List<IRepairRule> TopoSort(List<IRepairRule> rules)
    {
        var byKind = rules.ToDictionary(r => r.FaultClass);
        var visited = new HashSet<RepairFaultKind>();
        var temp = new HashSet<RepairFaultKind>();
        var order = new List<IRepairRule>();

        void Visit(IRepairRule r)
        {
            if (visited.Contains(r.FaultClass)) return;
            if (!temp.Add(r.FaultClass))
                throw new System.InvalidOperationException(
                    $"Cycle in repair-rule dependency graph at {r.FaultClass}");
            foreach (var dep in r.Requires)
            {
                if (!byKind.TryGetValue(dep, out var depRule))
                {
                    throw new System.InvalidOperationException(
                        $"Repair rule {r.GetType().Name} declares Requires={dep} but no rule of " +
                        $"that fault class is registered. Either implement the dependency or " +
                        $"remove the Requires entry.");
                }
                Visit(depRule);
            }
            temp.Remove(r.FaultClass);
            visited.Add(r.FaultClass);
            order.Add(r);
        }

        foreach (var r in rules) Visit(r);
        return order;
    }
}

public sealed record DiagnosisRecord(
    string RuleName,
    Diagnosis Diagnosis,
    string BeforeHash,
    string AfterHash);

public sealed record RepairResult(
    QuerySpec Spec,
    IReadOnlyList<DiagnosisRecord> Applied,
    IReadOnlyList<Fault> Faults,
    string BeforeHash,
    string AfterHash);
