namespace SuperAdminCopilot.Tests.Compilation;

using Microsoft.Extensions.Options;
using Moq;
using SuperAdminCopilot.Compilation;
using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using Xunit;

/// <summary>
/// End-to-end proof that <see cref="ISqlDialect"/> is the only thing standing between
/// SQL Server and PostgreSQL targeting. The compiler is fully constructed twice with the same
/// catalog / semantic layer / options — the only difference is the dialect binding. The same
/// <see cref="QuerySpec"/> must produce two STRUCTURALLY DIFFERENT SQL strings, both valid for
/// their target dialect. If this test breaks, an emission has regressed to hardcoded T-SQL.
/// </summary>
public sealed class SqlCompilerDialectIntegrationTests
{
    private static SqlCompiler Build(ISqlDialect dialect)
    {
        // Minimal catalog: one Tickets table with id+CreatedAt+Title; no FKs, no PK constraints.
        // Enough to exercise identifier quoting, TOP/LIMIT, NOW/CURRENT_DATE, COUNT(*) aliasing.
        var snapshot = new SchemaSnapshot
        {
            Tables = new[] { new TableInfo("dbo", "Tickets") },
            Columns = new[]
            {
                new ColumnInfo("dbo", "Tickets", "Id",        "int",      false, null, 1),
                new ColumnInfo("dbo", "Tickets", "CreatedAt", "datetime", false, null, 2),
                new ColumnInfo("dbo", "Tickets", "Title",     "nvarchar", true,  256,  3),
            },
            ForeignKeys = System.Array.Empty<ForeignKeyInfo>(),
            CapturedAt = System.DateTimeOffset.UtcNow,
        };

        var catalogMock = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalogMock.SetupGet(c => c.Snapshot).Returns(snapshot);
        catalogMock.SetupGet(c => c.Graph).Returns(new ForeignKeyGraph(snapshot));
        catalogMock.Setup(c => c.TableExists(It.IsAny<string>()))
            .Returns<string>(t => string.Equals(t, "Tickets", System.StringComparison.OrdinalIgnoreCase));
        catalogMock.Setup(c => c.ColumnExists(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((t, c) =>
                string.Equals(t, "Tickets", System.StringComparison.OrdinalIgnoreCase) &&
                snapshot.Columns.Any(ci => string.Equals(ci.ColumnName, c, System.StringComparison.OrdinalIgnoreCase)));
        catalogMock.Setup(c => c.GetColumns(It.IsAny<string>()))
            .Returns<string>(t => snapshot.Columns
                .Where(ci => string.Equals(ci.TableName, t, System.StringComparison.OrdinalIgnoreCase))
                .ToList());

        var semanticMock = new Mock<ISemanticLayer>(MockBehavior.Loose);
        semanticMock.SetupGet(s => s.Config).Returns(new SemanticLayerConfig());
        semanticMock.Setup(s => s.ResolveSynonymValue(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((_, v) => v);
        semanticMock.Setup(s => s.GetSensitiveColumns(It.IsAny<string>()))
            .Returns((IReadOnlySet<string>)new HashSet<string>(System.StringComparer.OrdinalIgnoreCase));

        var options = Options.Create(new CopilotOptions { MaxRows = 1000 });
        var joinResolver = new JoinResolver(catalogMock.Object);

        return new SqlCompiler(catalogMock.Object, joinResolver, semanticMock.Object, options, dialect,
            new TemporalTokenizer(),
            new SemanticExpander(semanticMock.Object),
            new FilterValueRewriter(catalogMock.Object, semanticMock.Object));
    }

    /// <summary>The proof: same spec → two SQLs, dialect-specific tokens in each.</summary>
    [Fact]
    public void Same_Spec_Produces_Distinct_But_Valid_Sql_For_Both_Dialects()
    {
        QuerySpec MakeSpec() => new()
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*", Alias = "Count" } },
            Filters = { new FilterSpec { Column = "Tickets.CreatedAt", Op = "gte", Value = "@today" } },
            Limit = 10,
        };

        var mssqlSql = Build(new MssqlDialect()).Compile(MakeSpec()).Sql;
        var pgSql    = Build(new PostgresDialect()).Compile(MakeSpec()).Sql;

        // MSSQL signatures.
        Assert.Contains("TOP (10)",              mssqlSql);
        Assert.Contains("[Tickets].[CreatedAt]", mssqlSql);
        Assert.Contains("CAST(GETDATE() AS DATE)", mssqlSql);
        Assert.Contains("COUNT(*) AS [Count]",   mssqlSql);
        Assert.DoesNotContain("LIMIT",           mssqlSql);
        Assert.DoesNotContain("\"Tickets\"",     mssqlSql);

        // Postgres signatures.
        Assert.Contains("LIMIT 10",                  pgSql);
        Assert.Contains("\"Tickets\".\"CreatedAt\"", pgSql);
        Assert.Contains("CURRENT_DATE",              pgSql);
        Assert.Contains("COUNT(*) AS \"Count\"",     pgSql);
        Assert.DoesNotContain("TOP (",               pgSql);
        Assert.DoesNotContain("GETDATE",             pgSql);
        Assert.DoesNotContain("[Tickets]",           pgSql);

        // Identity check — they really are different SQL strings.
        Assert.NotEqual(mssqlSql, pgSql);
    }

    /// <summary>Offset+limit exercises both BuildOffsetFetch paths: T-SQL's OFFSET/FETCH vs Postgres LIMIT/OFFSET.</summary>
    [Fact]
    public void Offset_And_Limit_Render_Per_Dialect_Pagination_Style()
    {
        QuerySpec MakeSpec() => new()
        {
            Root = "Tickets",
            Select = { "Tickets.Title" },
            OrderBy = { new OrderBySpec { Column = "Tickets.Id", Direction = "asc" } },
            Limit = 20,
            Offset = 40,
        };

        var mssqlSql = Build(new MssqlDialect()).Compile(MakeSpec()).Sql;
        var pgSql    = Build(new PostgresDialect()).Compile(MakeSpec()).Sql;

        // MSSQL uses OFFSET/FETCH at the tail (TOP is suppressed when offset > 0).
        Assert.Contains("OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY", mssqlSql);
        Assert.DoesNotContain("TOP (", mssqlSql);

        // Postgres uses LIMIT/OFFSET (TopClause was a no-op).
        Assert.Contains("LIMIT 20 OFFSET 40", pgSql);
        Assert.DoesNotContain("FETCH NEXT", pgSql);
    }

    /// <summary>Temporal-token expansion crosses every <see cref="ISqlDialect"/> date method
    /// (<see cref="ISqlDialect.DateFromParts"/>, <see cref="ISqlDialect.DatePart"/>, <see cref="ISqlDialect.NowExpression"/>).
    /// "Q1 of this year" is the toughest token the planner emits.</summary>
    [Fact]
    public void Q1_Of_This_Year_Token_Expands_Per_Dialect()
    {
        QuerySpec MakeSpec() => new()
        {
            Root = "Tickets",
            Select = { "Tickets.Title" },
            Filters =
            {
                new FilterSpec { Column = "Tickets.CreatedAt", Op = "gte", Value = "@q1_start" },
                new FilterSpec { Column = "Tickets.CreatedAt", Op = "lt",  Value = "@q1_end" },
            },
        };

        var mssqlSql = Build(new MssqlDialect()).Compile(MakeSpec()).Sql;
        var pgSql    = Build(new PostgresDialect()).Compile(MakeSpec()).Sql;

        // MSSQL: DATEFROMPARTS + YEAR(GETDATE())
        Assert.Contains("DATEFROMPARTS(YEAR(GETDATE()), 1, 1)", mssqlSql);
        Assert.Contains("DATEFROMPARTS(YEAR(GETDATE()), 4, 1)", mssqlSql);

        // Postgres: MAKE_DATE + EXTRACT(YEAR FROM NOW())::int
        Assert.Contains("MAKE_DATE(EXTRACT(YEAR FROM NOW())::int, 1, 1)", pgSql);
        Assert.Contains("MAKE_DATE(EXTRACT(YEAR FROM NOW())::int, 4, 1)", pgSql);
    }
}
