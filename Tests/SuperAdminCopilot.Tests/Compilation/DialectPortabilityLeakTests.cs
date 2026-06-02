namespace SuperAdminCopilot.Tests.Compilation;

using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Semantic;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 Stage-2 dialect-leak fixes: raw-SQL normalization is now dialect-routed
/// (NormalizeRawSql) and text-type detection is cross-engine. On SQL Server behavior is unchanged;
/// on Postgres the normalizer is a no-op (so valid Postgres isn't corrupted into T-SQL) and Postgres
/// text types are recognized as searchable.
/// </summary>
public class DialectPortabilityLeakTests
{
    [Fact]
    public void Mssql_NormalizeRawSql_RewritesPostgresIdiomsToTSql()
    {
        var sql = new MssqlDialect().NormalizeRawSql("SELECT * FROM t WHERE d >= NOW()");
        Assert.Contains("GETDATE()", sql);
        Assert.DoesNotContain("NOW()", sql);
    }

    [Fact]
    public void Postgres_NormalizeRawSql_IsNoOp_PreservesValidPostgres()
    {
        const string sql = "SELECT * FROM t WHERE d >= NOW() LIMIT 10";
        // Rewriting to T-SQL would corrupt valid Postgres — the dialect must leave it untouched.
        Assert.Equal(sql, new PostgresDialect().NormalizeRawSql(sql));
    }

    [Theory]
    // SQL Server type names — still match exactly (T-SQL behavior unchanged).
    [InlineData("nvarchar(100)", true)]
    [InlineData("varchar", true)]
    [InlineData("text", true)]
    [InlineData("int", false)]
    [InlineData("datetime2", false)]
    [InlineData("decimal(18,2)", false)]
    // Cross-engine text types — newly recognized so searchable columns work on other engines.
    [InlineData("character varying(255)", true)] // PostgreSQL
    [InlineData("citext", true)]                  // PostgreSQL
    [InlineData("longtext", true)]                // MySQL
    public void IsTextType_RecognizesCrossEngineTextTypes(string sqlType, bool expected)
        => Assert.Equal(expected, SemanticLayer.IsTextType(sqlType));
}
