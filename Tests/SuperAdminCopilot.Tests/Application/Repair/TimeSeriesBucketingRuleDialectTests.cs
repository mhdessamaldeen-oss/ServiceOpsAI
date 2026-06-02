namespace SuperAdminCopilot.Tests.Application.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Compilation.Dialects;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 dialect-routing of TimeSeriesBucketingRule's bucket expressions (was
/// hardcoded T-SQL). On SQL Server the day/month/year forms must stay BYTE-IDENTICAL to the prior
/// emission; on Postgres no T-SQL-only function (DATEFROMPARTS) may appear.
/// </summary>
public class TimeSeriesBucketingRuleDialectTests
{
    private static readonly ISqlDialect Mssql = new MssqlDialect();
    private static readonly ISqlDialect Postgres = new PostgresDialect();
    private const string Q = "Tickets.CreatedAt";

    [Theory]
    [InlineData("day", "CAST(Tickets.CreatedAt AS DATE)")]
    [InlineData("month", "DATEFROMPARTS(YEAR(Tickets.CreatedAt), MONTH(Tickets.CreatedAt), 1)")]
    [InlineData("year", "DATEFROMPARTS(YEAR(Tickets.CreatedAt), 1, 1)")]
    public void Mssql_PreservesPriorTSqlBucket(string bucket, string expected)
        => Assert.Equal(expected, TimeSeriesBucketingRule.BucketExpression(Mssql, Q, bucket).Expression);

    [Theory]
    [InlineData("day")]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("year")]
    public void Postgres_EmitsNonTSqlBucket(string bucket)
    {
        var expr = TimeSeriesBucketingRule.BucketExpression(Postgres, Q, bucket).Expression;
        Assert.DoesNotContain("DATEFROMPARTS", expr); // T-SQL-only constructor must not leak
        Assert.False(string.IsNullOrWhiteSpace(expr));
    }
}
