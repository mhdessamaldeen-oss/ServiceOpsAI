namespace AnalystAgent.Tests.Compilation;

using AnalystAgent.Sql.Dialects;
using Xunit;

/// <summary>
/// Dialect contract tests. Each interface method is asserted on both <see cref="MssqlDialect"/>
/// and <see cref="PostgresDialect"/> so a future dialect implementer has a clear bar:
/// every test in this file MUST pass before merging a new <see cref="ISqlDialect"/> implementation.
/// </summary>
public sealed class SqlDialectTests
{
    private static readonly MssqlDialect Mssql = new();
    private static readonly PostgresDialect Postgres = new();

    // ── Identifier quoting ────────────────────────────────────────────────────

    [Fact]
    public void Mssql_QuoteIdentifier_Wraps_In_Brackets()
        => Assert.Equal("[Tickets]", Mssql.QuoteIdentifier("Tickets"));

    [Fact]
    public void Mssql_QuoteIdentifier_Strips_Existing_Brackets()
        => Assert.Equal("[Tickets]", Mssql.QuoteIdentifier("[Tickets]"));

    [Fact]
    public void Postgres_QuoteIdentifier_Wraps_In_Double_Quotes()
        => Assert.Equal("\"Tickets\"", Postgres.QuoteIdentifier("Tickets"));

    [Fact]
    public void Postgres_QuoteIdentifier_Strips_Tsql_Brackets_From_Cross_Compilation()
        => Assert.Equal("\"Tickets\"", Postgres.QuoteIdentifier("[Tickets]"));

    [Fact]
    public void QuoteQualified_Composes_Table_Dot_Column()
    {
        Assert.Equal("[Tickets].[Id]", Mssql.QuoteQualified("Tickets", "Id"));
        Assert.Equal("\"Tickets\".\"Id\"", Postgres.QuoteQualified("Tickets", "Id"));
    }

    // ── TOP / LIMIT ───────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_Uses_TopClause_Before_Columns()
    {
        Assert.True(Mssql.LimitGoesBeforeColumns);
        Assert.Equal("TOP (10) ", Mssql.TopClause(10));
        Assert.Equal("", Mssql.LimitOffsetClause(10, null));   // limit-only goes via TOP, not OFFSET-FETCH
    }

    [Fact]
    public void Postgres_Uses_TrailingLimit_Not_TopClause()
    {
        Assert.False(Postgres.LimitGoesBeforeColumns);
        Assert.Equal("", Postgres.TopClause(10));               // PG never uses TOP
        Assert.Equal("LIMIT 10", Postgres.LimitOffsetClause(10, null));
    }

    [Fact]
    public void Both_Dialects_Handle_Offset_Limit()
    {
        Assert.Equal("OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", Mssql.LimitOffsetClause(10, 20));
        Assert.Equal("LIMIT 10 OFFSET 20", Postgres.LimitOffsetClause(10, 20));
    }

    [Fact]
    public void Both_Dialects_Empty_Clause_When_No_Bounds()
    {
        Assert.Equal("", Mssql.LimitOffsetClause(null, null));
        Assert.Equal("", Postgres.LimitOffsetClause(null, null));
    }

    // ── Now / Today ───────────────────────────────────────────────────────────

    [Fact]
    public void NowExpression_Per_Dialect()
    {
        Assert.Equal("GETDATE()", Mssql.NowExpression);
        Assert.Equal("NOW()", Postgres.NowExpression);
    }

    [Fact]
    public void CurrentDateExpression_Per_Dialect()
    {
        Assert.Equal("CAST(GETDATE() AS DATE)", Mssql.CurrentDateExpression);
        Assert.Equal("CURRENT_DATE", Postgres.CurrentDateExpression);
    }

    // ── DateAdd ───────────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_DateAdd_Emits_DATEADD_Function()
        => Assert.Equal("DATEADD(day, -7, GETDATE())", Mssql.DateAdd("day", -7, "GETDATE()"));

    [Fact]
    public void Postgres_DateAdd_Emits_Interval_Arithmetic()
        => Assert.Equal("(NOW() + INTERVAL '-7 day')", Postgres.DateAdd("day", -7, "NOW()"));

    [Theory]
    [InlineData("d",       "day")]
    [InlineData("days",    "day")]
    [InlineData("hr",      "hour")]
    [InlineData("hours",   "hour")]
    [InlineData("mo",      "month")]
    [InlineData("quarter", "quarter")]
    public void Both_Dialects_Normalize_Unit_Aliases(string alias, string expectedSubstring)
    {
        // MSSQL emits the canonical token directly; PG emits inside INTERVAL '… expectedUnit'.
        Assert.Contains(expectedSubstring, Mssql.DateAdd(alias, 1, "x"));
        Assert.Contains(expectedSubstring, Postgres.DateAdd(alias, 1, "x"));
    }

    // ── DateDiff ──────────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_DateDiff_Emits_DATEDIFF_Function()
        => Assert.Equal("DATEDIFF(day, A, B)", Mssql.DateDiff("day", "A", "B"));

    [Fact]
    public void Postgres_DateDiff_Day_Uses_Date_Subtraction()
        => Assert.Equal("(B::date - A::date)", Postgres.DateDiff("day", "A", "B"));

    [Fact]
    public void Postgres_DateDiff_Hour_Uses_Epoch_Math()
        => Assert.Contains("EXTRACT(EPOCH", Postgres.DateDiff("hour", "A", "B"));

    [Fact]
    public void Postgres_DateDiff_Month_Uses_Year_Plus_Month_Math()
    {
        var sql = Postgres.DateDiff("month", "A", "B");
        Assert.Contains("EXTRACT(YEAR", sql);
        Assert.Contains("EXTRACT(MONTH", sql);
    }

    // ── DateFromParts ─────────────────────────────────────────────────────────

    [Fact]
    public void DateFromParts_Per_Dialect()
    {
        Assert.Equal("DATEFROMPARTS(2026, 5, 28)", Mssql.DateFromParts("2026", "5", "28"));
        Assert.Equal("MAKE_DATE(2026, 5, 28)",      Postgres.DateFromParts("2026", "5", "28"));
    }

    // ── DatePart ──────────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_DatePart_Year_Month_Day_Use_Dedicated_Functions()
    {
        Assert.Equal("YEAR(X)",  Mssql.DatePart("year", "X"));
        Assert.Equal("MONTH(X)", Mssql.DatePart("month", "X"));
        Assert.Equal("DAY(X)",   Mssql.DatePart("day", "X"));
    }

    [Fact]
    public void Mssql_DatePart_Week_Uses_Datepart_Function()
        => Assert.Equal("DATEPART(week, X)", Mssql.DatePart("week", "X"));

    [Fact]
    public void Postgres_DatePart_Uses_Extract()
    {
        Assert.Equal("EXTRACT(YEAR FROM X)::int", Postgres.DatePart("year", "X"));
        Assert.Equal("EXTRACT(MONTH FROM X)::int", Postgres.DatePart("month", "X"));
        Assert.Equal("EXTRACT(WEEK FROM X)::int", Postgres.DatePart("week", "X"));
    }

    // ── DateTrunc ─────────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_DateTrunc_Uses_DateAdd_DateDiff_Roundtrip()
        // The exact emission the InjectTimeSeriesBucketingPhase used pre-refactor.
        => Assert.Equal("DATEADD(month, DATEDIFF(month, 0, X), 0)", Mssql.DateTrunc("month", "X"));

    [Fact]
    public void Postgres_DateTrunc_Uses_Native_Function()
        => Assert.Equal("DATE_TRUNC('month', X)", Postgres.DateTrunc("month", "X"));

    // ── CastAsDate ────────────────────────────────────────────────────────────

    [Fact]
    public void CastAsDate_Per_Dialect()
    {
        Assert.Equal("CAST(GETDATE() AS DATE)", Mssql.CastAsDate("GETDATE()"));
        Assert.Equal("(NOW())::date", Postgres.CastAsDate("NOW()"));
    }

    // ── NullCoalesce ──────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_NullCoalesce_Uses_ISNULL()
        => Assert.Equal("ISNULL(X, '(Unassigned)')", Mssql.NullCoalesce("X", "'(Unassigned)'"));

    [Fact]
    public void Postgres_NullCoalesce_Uses_COALESCE()
        => Assert.Equal("COALESCE(X, '(Unassigned)')", Postgres.NullCoalesce("X", "'(Unassigned)'"));

    // ── CastAsString ──────────────────────────────────────────────────────────

    [Fact]
    public void Mssql_CastAsString_Uses_NVARCHAR_Length()
        => Assert.Equal("CAST(X AS NVARCHAR(64))", Mssql.CastAsString("X", 64));

    [Fact]
    public void Mssql_CastAsString_Uses_NVARCHAR_MAX_For_Zero_Length()
        => Assert.Equal("CAST(X AS NVARCHAR(MAX))", Mssql.CastAsString("X", 0));

    [Fact]
    public void Postgres_CastAsString_Uses_Varchar_Cast()
        => Assert.Equal("(X)::varchar(64)", Postgres.CastAsString("X", 64));

    [Fact]
    public void Postgres_CastAsString_Uses_Text_For_Zero_Length()
        => Assert.Equal("(X)::text", Postgres.CastAsString("X", 0));

    // ── CastAsInt / Decimal ───────────────────────────────────────────────────

    [Fact]
    public void CastAsInt_Per_Dialect()
    {
        Assert.Equal("CAST(X AS INT)", Mssql.CastAsInt("X"));
        Assert.Equal("(X)::int",        Postgres.CastAsInt("X"));
    }

    [Fact]
    public void CastAsDecimal_Per_Dialect()
    {
        Assert.Equal("CAST(X AS DECIMAL(18,2))", Mssql.CastAsDecimal("X", 18, 2));
        Assert.Equal("(X)::numeric(18,2)",        Postgres.CastAsDecimal("X", 18, 2));
    }

    // ── Operators ─────────────────────────────────────────────────────────────

    [Fact]
    public void LIKE_Operators_Identical_In_Both()
    {
        Assert.Equal("LIKE",     Mssql.LikeOperator);
        Assert.Equal("LIKE",     Postgres.LikeOperator);
        Assert.Equal("NOT LIKE", Mssql.NotLikeOperator);
        Assert.Equal("NOT LIKE", Postgres.NotLikeOperator);
    }

    // ── Dialect name ──────────────────────────────────────────────────────────

    [Fact]
    public void Each_Dialect_Has_A_Distinct_Name()
    {
        Assert.Equal("mssql",    Mssql.Name);
        Assert.Equal("postgres", Postgres.Name);
        Assert.NotEqual(Mssql.Name, Postgres.Name);
    }

    // ── Cross-dialect "same input, different output" proof ────────────────────

    [Fact]
    public void Same_QuerySpec_Fragment_Emits_Different_Sql_Per_Dialect()
    {
        // Simulate a small bucketing expression that's used heavily in TIMESERIES:
        //   DateTrunc('month', Tickets.CreatedAt) on each dialect.
        var col = "Tickets.CreatedAt";
        var mssqlBucket = Mssql.DateTrunc("month", Mssql.QuoteQualified("Tickets", "CreatedAt"));
        var pgBucket    = Postgres.DateTrunc("month", Postgres.QuoteQualified("Tickets", "CreatedAt"));

        // Both fragments reference the column, but the SQL differs structurally.
        Assert.Contains(col.Split('.')[1], mssqlBucket);  // "CreatedAt" appears
        Assert.Contains(col.Split('.')[1], pgBucket);
        Assert.NotEqual(mssqlBucket, pgBucket);
        Assert.StartsWith("DATEADD", mssqlBucket);
        Assert.StartsWith("DATE_TRUNC", pgBucket);
    }
}
