namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>Question asks for aggregation ("how many"/"total"/"max"/etc) but spec lacks one → inject COUNT/SUM/AVG/MAX/MIN.</summary>
internal sealed class ForceAggregationOnCountQuestionPhase : ISpecRepairPhase
{
    private enum AggKind { None, Count, Sum, Avg, Max, Min }

    private static readonly Regex SumIntent = new(@"\b(total|sum|sum\s+of|overall)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AvgIntent = new(@"\b(average|avg|mean)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxIntent = new(@"\b(max|maximum|highest|largest|biggest|peak|top|tallest|most)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinIntent = new(@"\b(min|minimum|lowest|smallest|tiniest|least|earliest|oldest)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CountIntent = new(@"\b(how\s+many|count\s+of|number\s+of|count|tally|split\s+\w+|broken\s+down|per\s+\w+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "ForceAggregationOnCountQuestion";
    public string Covers => "Paraphrased aggregation intents that produce list specs";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count > 0) return;
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        var q = ctx.Question ?? string.Empty;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        var kind = ClassifyIntent(q);
        if (kind == AggKind.None) return;

        var (fn, column, alias) = BuildAggregation(kind, spec.Root, ctx);
        if (fn is null) return;

        spec.Aggregations.Add(new AggregateSpec { Function = fn, Column = column, Alias = alias });
        // When forcing an aggregation on a scalar shape (no groupBy), drop list-style select items.
        if (spec.GroupBy.Count == 0 && spec.Select.Count > 0) spec.Select.Clear();
        ctx.Diagnostics.Add(new(Name, $"forced {fn}({column}) for {kind} intent"));
    }

    private static AggKind ClassifyIntent(string question)
    {
        if (SumIntent.IsMatch(question)) return AggKind.Sum;
        if (AvgIntent.IsMatch(question)) return AggKind.Avg;
        if (MaxIntent.IsMatch(question)) return AggKind.Max;
        if (MinIntent.IsMatch(question)) return AggKind.Min;
        if (CountIntent.IsMatch(question)) return AggKind.Count;
        return AggKind.None;
    }

    private static (string? Fn, string Column, string Alias) BuildAggregation(AggKind kind, string rootTable, SpecRepairContext ctx) => kind switch
    {
        AggKind.Count => ("COUNT", "*", "Count"),
        AggKind.Sum => PickNumericColumn(rootTable, ctx) is { } c ? ("SUM", $"{rootTable}.{c}", "Total") : ("COUNT", "*", "Count"),
        AggKind.Avg => PickNumericColumn(rootTable, ctx) is { } c ? ("AVG", $"{rootTable}.{c}", "Average") : ("COUNT", "*", "Count"),
        AggKind.Max => PickNumericOrDateColumn(rootTable, ctx) is { } c ? ("MAX", $"{rootTable}.{c}", "Maximum") : ("COUNT", "*", "Count"),
        AggKind.Min => PickNumericOrDateColumn(rootTable, ctx) is { } c ? ("MIN", $"{rootTable}.{c}", "Minimum") : ("COUNT", "*", "Count"),
        _ => (null, "", ""),
    };

    // Prefer metric-name columns (Value/Amount/Total/...) over generic numeric columns. Skip ID-like.
    private static string? PickNumericColumn(string table, SpecRepairContext ctx)
    {
        var cols = ctx.Catalog.GetColumns(table);
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType) && IsLikelyMetricName(c.ColumnName)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType)) return c.ColumnName; }
        return null;
    }

    private static string? PickNumericOrDateColumn(string table, SpecRepairContext ctx)
    {
        var cols = ctx.Catalog.GetColumns(table);
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType) && IsLikelyMetricName(c.ColumnName)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsDateLike(c.DataType)) return c.ColumnName; }
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
        || name.Contains("Rate", System.StringComparison.OrdinalIgnoreCase);

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
