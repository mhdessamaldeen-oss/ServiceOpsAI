namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>InjectTimeSeriesBucketingPhase</c>. When the question carries a
/// time-series granularity ("by month" / "weekly" / "yearly" / "trend") AND the spec has no
/// date-bucket GROUP BY, inject the bucket expression + GROUP BY + ORDER BY. Driven by the
/// root entity's default date column from semantic-layer.
/// </summary>
public sealed class TimeSeriesBucketingRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.TimeSeriesBucketing;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    private readonly ISqlDialect _dialect;

    /// <summary>Dialect is injected so bucket expressions are emitted for the configured engine
    /// (<c>CopilotOptions.Database</c>) instead of hardcoded T-SQL.</summary>
    public TimeSeriesBucketingRule(ISqlDialect dialect) => _dialect = dialect;

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var gran = ctx.Linguistics.ExtractTimeSeriesGranularity(ctx.Question);
        if (gran is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var dateCol = ctx.Semantic.GetDateColumn(spec.Root);
        if (string.IsNullOrEmpty(dateCol)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Skip when a bucket-shaped GROUP BY already exists.
        foreach (var g in spec.GroupBy)
            if (!string.IsNullOrEmpty(g)
                && (g.Contains("DATEPART", System.StringComparison.OrdinalIgnoreCase)
                 || g.Contains("YEAR(",    System.StringComparison.OrdinalIgnoreCase)
                 || g.Contains("MONTH(",   System.StringComparison.OrdinalIgnoreCase)
                 || g.Contains("DATEADD",  System.StringComparison.OrdinalIgnoreCase)
                 || g.Contains("FORMAT(",  System.StringComparison.OrdinalIgnoreCase)))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting {gran.Bucket} time-series bucket",
                          Payload: (spec.Root, dateCol!, gran.Bucket)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not (string root, string dateCol, string bucket)) return spec;
        var qualified = root + "." + dateCol;

        // Bucket expression emitted via the dialect so non-SqlServer engines get valid SQL. On SQL
        // Server this reproduces the prior T-SQL (day/month/year byte-identical; week/quarter differ
        // only in keyword case, which T-SQL ignores).
        var (bucketExpr, alias) = BucketExpression(_dialect, qualified, bucket);

        spec.Computed.Add(new ComputedSpec { Alias = alias, Expression = bucketExpr });
        spec.Select.Add(alias);
        spec.GroupBy.Add(bucketExpr);
        if (spec.OrderBy.Count == 0)
            spec.OrderBy.Add(new OrderBySpec { Column = bucketExpr, Direction = "asc" });
        // If no aggregation yet, add a COUNT(*) — the analyst wants counts per bucket.
        if (spec.Aggregations.Count == 0)
            spec.Aggregations.Add(new AggregateSpec { Function = "COUNT", Column = "*", Alias = "Count" });

        return spec;
    }

    /// <summary>Builds the (expression, alias) for a time bucket via the dialect. Pure + unit-testable.
    /// SQL Server output is byte-identical to the pre-2026-06-02 hardcoded T-SQL for day/month/year;
    /// week/quarter use the dialect's DateTrunc (same result, lowercase unit keyword).</summary>
    internal static (string Expression, string Alias) BucketExpression(ISqlDialect d, string qualified, string bucket) => bucket switch
    {
        "day"     => (d.CastAsDate(qualified), "Day"),
        "week"    => (d.DateTrunc("week", qualified), "WeekStart"),
        "month"   => (d.DateFromParts(d.DatePart("year", qualified), d.DatePart("month", qualified), "1"), "MonthStart"),
        "quarter" => (d.DateTrunc("quarter", qualified), "QuarterStart"),
        "year"    => (d.DateFromParts(d.DatePart("year", qualified), "1", "1"), "YearStart"),
        _         => (d.DateFromParts(d.DatePart("year", qualified), d.DatePart("month", qualified), "1"), "Period"),
    };
}
