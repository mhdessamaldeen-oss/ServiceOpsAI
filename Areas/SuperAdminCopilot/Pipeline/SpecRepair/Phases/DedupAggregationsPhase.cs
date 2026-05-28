namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>
/// Remove duplicate aggregations that share the same (Function, Column) pair. The LLM sometimes
/// emits <c>SUM(amount) AS Total, SUM(amount) AS TotalSum</c> — both render in the SQL, and
/// the compiler's alias dedup turns the second into <c>TotalSum_2</c>, leaving an ugly
/// duplicate column in the result.
///
/// <para>Keeps the first occurrence (preserves any deliberate alias choice the LLM made).
/// Idempotent.</para>
/// </summary>
internal sealed class DedupAggregationsPhase : ISpecRepairPhase
{
    public string Name => "DedupAggregations";
    public string Covers => "Drop duplicate aggregation entries sharing the same function+column";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count < 2) return;
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var kept = new List<AggregateSpec>(spec.Aggregations.Count);
        var dropped = 0;
        foreach (var a in spec.Aggregations)
        {
            var key = $"{a.Function?.Trim() ?? ""}|{a.Column?.Trim() ?? ""}|{(a.Distinct ? "D" : "")}";
            if (seen.Add(key)) kept.Add(a);
            else dropped++;
        }
        if (dropped == 0) return;
        spec.Aggregations.Clear();
        spec.Aggregations.AddRange(kept);
        ctx.Diagnostics.Add(new(Name, $"removed {dropped} duplicate aggregation(s)"));
    }
}
