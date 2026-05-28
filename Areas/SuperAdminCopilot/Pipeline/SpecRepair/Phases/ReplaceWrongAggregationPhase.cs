namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question clearly asks for AVG / SUM / MAX / MIN but the spec already contains
/// only COUNT (the LLM's "safe default" on a hard-to-aggregate column), REPLACE the COUNT
/// with the intended aggregation against a numeric column on the root.
///
/// <para>Sibling to <see cref="ForceAggregationOnCountQuestionPhase"/>: that one fires when
/// <c>Aggregations</c> is empty; THIS one fires when it's non-empty but the wrong shape.
/// Without this, "average bill amount" emits <c>COUNT(*)</c> and the user sees a row count
/// when they asked for a mean.</para>
/// </summary>
internal sealed class ReplaceWrongAggregationPhase : ISpecRepairPhase
{
    private enum AggKind { None, Sum, Avg, Max, Min }

    // Same regexes as the sibling phase — kept in sync intentionally.
    private static readonly Regex SumIntent = new(@"\b(total|sum|sum\s+of|overall)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AvgIntent = new(@"\b(average|avg|mean)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxIntent = new(@"\b(max|maximum|highest|largest|biggest|peak|tallest)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinIntent = new(@"\b(min|minimum|lowest|smallest|tiniest|earliest|oldest)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "ReplaceWrongAggregation";
    public string Covers => "Question wants AVG/SUM/MAX/MIN but spec emitted COUNT — swap the aggregation";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count == 0) return;                                // sibling phase handles empty
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        // Only act when the ONLY aggregation present is COUNT — don't disturb intentional mixes.
        if (!spec.Aggregations.TrueForAll(a => string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase)))
            return;

        var q = ctx.Question ?? string.Empty;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        var kind = ClassifyIntent(q);
        if (kind == AggKind.None) return;

        var column = PickColumnForKind(kind, spec.Root, ctx);
        if (column is null) return;

        var (fn, alias) = AggregationFor(kind);
        spec.Aggregations.Clear();
        spec.Aggregations.Add(new AggregateSpec { Function = fn, Column = $"{spec.Root}.{column}", Alias = alias });
        ctx.Diagnostics.Add(new(Name, $"replaced COUNT with {fn}({spec.Root}.{column}) for {kind} intent"));
    }

    private static AggKind ClassifyIntent(string question)
    {
        // Order: AVG before SUM so "average total" picks Avg; MAX/MIN are last-resort because
        // they overlap with TOPN keywords (top, highest, lowest).
        if (AvgIntent.IsMatch(question)) return AggKind.Avg;
        if (SumIntent.IsMatch(question)) return AggKind.Sum;
        if (MaxIntent.IsMatch(question)) return AggKind.Max;
        if (MinIntent.IsMatch(question)) return AggKind.Min;
        return AggKind.None;
    }

    private static (string Fn, string Alias) AggregationFor(AggKind kind) => kind switch
    {
        AggKind.Sum => ("SUM", "Total"),
        AggKind.Avg => ("AVG", "Average"),
        AggKind.Max => ("MAX", "Maximum"),
        AggKind.Min => ("MIN", "Minimum"),
        _ => ("COUNT", "Count"),
    };

    private static string? PickColumnForKind(AggKind kind, string table, SpecRepairContext ctx)
    {
        var cols = ctx.Catalog.GetColumns(table);
        // Prefer metric-named numeric columns.
        foreach (var c in cols)
            if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType) && IsLikelyMetricName(c.ColumnName))
                return c.ColumnName;
        // Fall back to any non-id numeric.
        foreach (var c in cols)
            if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType))
                return c.ColumnName;
        // MAX/MIN can target dates too.
        if (kind is AggKind.Max or AggKind.Min)
            foreach (var c in cols)
                if (!IsIdLike(c.ColumnName) && IsDateLike(c.DataType))
                    return c.ColumnName;
        return null;
    }

    private static bool IsIdLike(string name) =>
        string.Equals(name, "Id", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyMetricName(string name) =>
        name.Contains("Value", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Amount", System.StringComparison.OrdinalIgnoreCase)
        || name.Equals("Total", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Total", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Price", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Cost", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Reading", System.StringComparison.OrdinalIgnoreCase)
        || name.Equals("Score", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Quantity", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Rate", System.StringComparison.OrdinalIgnoreCase)
        || name.Contains("Consumption", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsNumeric(string type) =>
        type.StartsWith("int", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("bigint", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("decimal", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("numeric", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("money", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("float", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("real", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("smallint", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("tinyint", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsDateLike(string type) =>
        type.StartsWith("date", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("datetime", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("smalldatetime", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("time", System.StringComparison.OrdinalIgnoreCase);
}
