namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>DetectOverJoinPhase</c> + <c>ForceCountDistinctOnFanOutJoinPhase</c>.
///
/// <para>Drops joins on tables that are referenced nowhere else in the spec (over-join from
/// the LLM hallucinating relationships). When a fan-out join survives, ensure the COUNT is
/// distinct on the root's natural key — otherwise the join inflates the count.</para>
/// </summary>
public sealed class OverJoinRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.OverJoin;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Joins.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var referenced = CollectReferencedTables(spec);
        var dropIdx = new List<int>();
        for (int i = 0; i < spec.Joins.Count; i++)
        {
            var j = spec.Joins[i];
            if (string.IsNullOrEmpty(j.Table)) continue;
            // Anti-joins are deliberately unreferenced — their presence IS the constraint.
            if (string.Equals(j.Kind, "anti", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!referenced.Contains(j.Table)) dropIdx.Add(i);
        }
        if (dropIdx.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"dropping {dropIdx.Count} over-join(s)", Payload: dropIdx));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<int> dropIdx) return spec;
        for (int i = dropIdx.Count - 1; i >= 0; i--) spec.Joins.RemoveAt(dropIdx[i]);
        return spec;
    }

    private static HashSet<string> CollectReferencedTables(QuerySpec spec)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(spec.Root)) set.Add(spec.Root);
        void Add(string? col)
        {
            if (string.IsNullOrEmpty(col)) return;
            var dot = col.IndexOf('.');
            if (dot > 0) set.Add(col.Substring(0, dot).Trim('[', ']'));
        }
        foreach (var s in spec.Select) Add(s);
        foreach (var f in spec.Filters) Add(f.Column);
        foreach (var g in spec.GroupBy) Add(g);
        foreach (var o in spec.OrderBy) Add(o.Column);
        foreach (var a in spec.Aggregations) Add(a.Column);
        foreach (var h in spec.Having) Add(h.Column);
        foreach (var c in spec.Computed) Add(c.Expression);
        return set;
    }
}
