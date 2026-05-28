namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>"COUNT(DISTINCT)" / "COUNT_DISTINCT" → COUNT + distinct=true. Strips leading DISTINCT from column.</summary>
internal sealed class NormalizeAggregationFunctionPhase : ISpecRepairPhase
{
    public string Name => "NormalizeAggregationFunction";
    public string Covers => "Function-name variants like COUNT(DISTINCT) collapse to COUNT + distinct";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        int mutated = 0;
        foreach (var a in spec.Aggregations)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(a.Function))
            {
                var upper = a.Function.Trim().ToUpperInvariant();
                if (ctx.Options.AggregationFunctionAliases.TryGetValue(upper, out var canonical))
                {
                    a.Function = canonical;
                    a.Distinct = true;
                    changed = true;
                }
                else if (upper.Contains("DISTINCT"))
                {
                    var stripped = upper.Replace("DISTINCT", "").Replace("(", "").Replace(")", "")
                                        .Replace("_", "").Replace(" ", "");
                    if (!string.IsNullOrEmpty(stripped))
                    {
                        a.Function = stripped;
                        a.Distinct = true;
                        changed = true;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(a.Column))
            {
                var trimmed = a.Column.Trim();
                if (trimmed.StartsWith("DISTINCT ", System.StringComparison.OrdinalIgnoreCase))
                {
                    a.Column = trimmed.Substring("DISTINCT ".Length).Trim();
                    a.Distinct = true;
                    changed = true;
                }
            }
            if (changed) mutated++;
        }
        if (mutated > 0)
            ctx.Diagnostics.Add(new(Name, $"normalised {mutated} function(s)"));
    }
}
