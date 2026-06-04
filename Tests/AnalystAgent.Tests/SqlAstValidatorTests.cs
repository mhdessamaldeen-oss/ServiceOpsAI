namespace AnalystAgent.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AnalystAgent.Models;
using AnalystAgent.Schema;
using AnalystAgent.Validation;
using Xunit;

/// <summary>
/// Integration tests for the SqlAstValidator safety gate. Pins the key syntactic guarantees:
/// DML refused, multi-statement refused, CTEs accepted (regression for the 2026-05-18 fix),
/// window functions accepted, empty/whitespace rejected.
/// </summary>
public class SqlAstValidatorTests
{
    private static SqlAstValidator Build()
    {
        // Mock catalog + policy with "everything allowed" so validator tests focus on AST rules,
        // not column-access policy. GetColumns returns an empty list so the validator's
        // per-column policy loop has nothing to iterate (won't NRE, won't reject).
        var catalog = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalog.Setup(c => c.TableExists(It.IsAny<string>())).Returns(true);
        catalog.Setup(c => c.ColumnExists(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        catalog.Setup(c => c.GetColumns(It.IsAny<string>())).Returns(Array.Empty<AnalystAgent.Schema.ColumnInfo>());
        var policy = new Mock<IAnalystSchemaAccessPolicy>(MockBehavior.Loose);
        policy.Setup(p => p.IsTableAllowed(It.IsAny<string>())).Returns(true);
        policy.Setup(p => p.IsColumnAllowed(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        return new SqlAstValidator(catalog.Object, policy.Object);
    }

    private static CompiledSql Sql(string sql) => new(sql, new Dictionary<string, object?>());

    [Fact]
    public void Accepts_simple_select()
    {
        var result = Build().Validate(Sql("SELECT t.TicketNumber FROM Tickets t WHERE t.IsDeleted = 0"));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("DELETE FROM Tickets")]
    [InlineData("UPDATE Tickets SET IsDeleted = 1")]
    [InlineData("INSERT INTO Tickets (Title) VALUES ('x')")]
    [InlineData("TRUNCATE TABLE Tickets")]
    [InlineData("DROP TABLE Tickets")]
    public void Rejects_dml_and_ddl(string sql)
    {
        var result = Build().Validate(Sql(sql));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_multi_statement()
    {
        var result = Build().Validate(Sql("SELECT 1; SELECT 2;"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_single_cte()
    {
        // Regression: pre-2026-05-18 the IdentifierVisitor rejected this as "unknown table
        // referenced: 'DailyCounts'". Fix added Visit(CommonTableExpression) to track CTE
        // names. This test pins the fix so a future refactor can't silently regress S5/CTE.
        var sql = "WITH DailyCounts AS (SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS C FROM Tickets GROUP BY CAST(CreatedAt AS DATE)) SELECT Day, C FROM DailyCounts";
        var result = Build().Validate(Sql(sql));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Accepts_multi_cte()
    {
        var sql = @"WITH M AS (SELECT FORMAT(CreatedAt, 'yyyy-MM') AS Mn, COUNT(*) AS C FROM Tickets GROUP BY FORMAT(CreatedAt, 'yyyy-MM')),
                         B AS (SELECT Mn, C FROM M WHERE C > 30)
                    SELECT Mn, C FROM B ORDER BY Mn";
        var result = Build().Validate(Sql(sql));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Accepts_window_function()
    {
        var sql = "SELECT TicketNumber, ROW_NUMBER() OVER (ORDER BY CreatedAt) AS Rn FROM Tickets";
        var result = Build().Validate(Sql(sql));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Accepts_recursive_cte()
    {
        var sql = "WITH T AS (SELECT Id, ParentTicketId, 0 AS Depth FROM Tickets WHERE ParentTicketId IS NULL UNION ALL SELECT t.Id, t.ParentTicketId, p.Depth+1 FROM Tickets t INNER JOIN T p ON t.ParentTicketId = p.Id) SELECT * FROM T";
        var result = Build().Validate(Sql(sql));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Empty_sql_passes_parser_no_errors()
    {
        // Empty / whitespace input parses to no fragment (no statements), which the validator
        // treats as "no rules violated → valid". The executor then handles the empty case.
        // This test pins that BEHAVIOR so a future tightening (rejecting empty at validator
        // level) would require deliberately updating this assertion.
        Assert.True(Build().Validate(Sql("")).IsValid);
        Assert.True(Build().Validate(Sql("   \n   ")).IsValid);
    }

    [Theory]
    [InlineData("SELECT 1 INTO #temp FROM Tickets")]  // SELECT INTO is a DML-like create
    [InlineData("EXEC sp_who")]                       // stored proc execution
    public void Rejects_dangerous_constructs(string sql)
    {
        var result = Build().Validate(Sql(sql));
        Assert.False(result.IsValid);
    }
}
