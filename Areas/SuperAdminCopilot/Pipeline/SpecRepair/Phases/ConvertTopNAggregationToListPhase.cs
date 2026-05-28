namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>"top N largest X" + LLM emitted SUM/MAX/MIN/AVG with LIMIT → convert to list (drop agg, bare select, ORDER BY DESC).</summary>
internal sealed class ConvertTopNAggregationToListPhase : ISpecRepairPhase
{
    private static readonly Regex TopNIntent = new(
        @"\b(top|largest|biggest|highest|smallest|lowest|first|bottom)\s+\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Collections.Generic.HashSet<string> ScalarAggs =
        new(System.StringComparer.OrdinalIgnoreCase) { "MAX", "MIN", "SUM", "AVG" };

    public string Name => "ConvertTopNAggregationToList";
    public string Covers => "Top-N list shape mis-emitted as scalar aggregation";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count != 1) return;
        if (!spec.Limit.HasValue || spec.Limit.Value <= 0) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        if (!TopNIntent.IsMatch(ctx.Question)) return;

        var agg = spec.Aggregations[0];
        if (string.IsNullOrEmpty(agg.Function) || !ScalarAggs.Contains(agg.Function)) return;

        var metricCol = agg.Column;
        spec.Aggregations.Clear();

        if (spec.Select.Count == 0 && !string.IsNullOrEmpty(metricCol) && metricCol != "*"
            && !metricCol.StartsWith("expr:", System.StringComparison.OrdinalIgnoreCase))
        {
            spec.Select.Add(metricCol);
        }

        var ascending = Regex.IsMatch(ctx.Question, @"\b(smallest|lowest|bottom)\b", RegexOptions.IgnoreCase);
        var direction = ascending ? "asc" : "desc";
        if (spec.OrderBy.Count == 0 && !string.IsNullOrEmpty(metricCol) && metricCol != "*")
        {
            spec.OrderBy.Add(new OrderBySpec { Column = metricCol, Direction = direction });
        }

        ctx.Diagnostics.Add(new(Name, $"converted {agg.Function} to TOP-{spec.Limit} list on {metricCol}"));
    }
}
