namespace SuperAdminCopilot.Pipeline.Stages;

using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// Heuristic chart-type suggestion based on result-set shape. Lightweight — no AI call, just
/// schema-shape pattern matching. Output is consumed by the host UI to pick a default view
/// (KPI card / bar / line / pie / table). Never wrong-rendering critical because "table" is
/// always a safe fallback.
/// </summary>
public interface IChartTypeSuggester
{
    string Suggest(QuerySpec spec, ExecutionResult result);
}

internal sealed class ChartTypeSuggester : IChartTypeSuggester
{
    // Byte-identical fallback to the pre-2026-06-02 hardcoded date-column tokens — used when
    // linguistic-cues.json is absent or declares no dateColumnTokens for any locale.
    private static readonly string[] FallbackDateTokens =
        { "date", "createdat", "updatedat", "month", "year", "week" };

    private readonly IReadOnlyList<string> _dateTokens;

    public ChartTypeSuggester(ILinguisticCuesProvider cues)
    {
        var fromConfig = cues.Compiled.Locales.Values
            .SelectMany(l => l.DateColumnTokens)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToArray();
        _dateTokens = fromConfig.Length > 0 ? fromConfig : FallbackDateTokens;
    }

    public string Suggest(QuerySpec spec, ExecutionResult result)
    {
        if (result.Rows is null || result.RowCount == 0) return "table";

        var firstRow = result.Rows[0];
        var columnCount = firstRow.Count;

        // 1. Single number → KPI card.
        if (result.RowCount == 1 && columnCount == 1 && IsNumeric(firstRow.Values.First()))
            return "kpi";

        // Aggregations with GROUP BY: typical chartable shape — one dim + one measure.
        var hasAgg = spec.Aggregations.Count > 0 || spec.Having.Count > 0;
        var hasGroupBy = spec.GroupBy.Count > 0;
        var hasOrderBy = spec.OrderBy.Count > 0;

        if (hasAgg && hasGroupBy && columnCount >= 2)
        {
            // Time-series: a date-shaped dimension → line chart.
            var groupCol = spec.GroupBy[0] ?? "";
            if (IsDateLike(groupCol)) return "line";

            // Categorical few rows → pie (≤8 categories is readable; more is noisy).
            if (result.RowCount <= 8) return "pie";

            // Larger categorical → bar.
            return "bar";
        }

        // Top-N pattern (limit + order by, no group): often a rank chart → bar.
        if (spec.Limit is > 0 && hasOrderBy && columnCount >= 2)
            return "bar";

        // Otherwise: table.
        return "table";
    }

    private static bool IsNumeric(object? v) =>
        v is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    // Internal for the golden config-vs-fallback test. Instance because the token set is resolved
    // from linguistic-cues.json at construction (byte-identical to the old 6 tokens when absent).
    internal bool IsDateLike(string column)
    {
        var lower = column.ToLowerInvariant();
        foreach (var token in _dateTokens)
            if (lower.Contains(token)) return true;
        return false;
    }
}
