namespace AnalystAgent.Sql.Dialects;

using System.Globalization;

/// <summary>
/// SQL Server (T-SQL) dialect — mirrors the current <c>SqlCompiler</c> emission verbatim so the
/// dialect refactor is behaviour-preserving. Any change here is a behaviour change — keep this
/// class in lockstep with the previous hardcoded strings.
/// </summary>
public sealed class MssqlDialect : ISqlDialect
{
    public string Name => "mssql";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "[]";
        // Strip any existing brackets and re-wrap. T-SQL escapes ] by doubling it.
        var bare = identifier.Trim('[', ']').Replace("]", "]]");
        return "[" + bare + "]";
    }

    public string QuoteQualified(string table, string column) =>
        QuoteIdentifier(table) + "." + QuoteIdentifier(column);

    public char IdentifierQuoteOpen  => '[';
    public char IdentifierQuoteClose => ']';

    // ── TOP / OFFSET-FETCH ────────────────────────────────────────────────────
    public bool LimitGoesBeforeColumns => true;
    public string TopClause(int limit) => limit > 0 ? $"TOP ({limit}) " : "";
    public string LimitOffsetClause(int? limit, int? offset)
    {
        // SQL Server requires ORDER BY for OFFSET. The compiler is responsible for emitting
        // a stable ORDER BY before invoking this; this method only emits the OFFSET-FETCH
        // clause itself.
        if (!(offset is > 0)) return ""; // T-SQL uses TOP for limit-without-offset
        var sb = new System.Text.StringBuilder();
        sb.Append("OFFSET ").Append(offset.Value).Append(" ROWS");
        if (limit is > 0)
            sb.Append(" FETCH NEXT ").Append(limit.Value).Append(" ROWS ONLY");
        return sb.ToString();
    }

    // ── Now / today ───────────────────────────────────────────────────────────
    public string NowExpression => "GETDATE()";
    public string CurrentDateExpression => "CAST(GETDATE() AS DATE)";

    // T-SQL date-part keywords (and their many shortform synonyms). Used by the compiler's
    // expression-qualifier to avoid mis-quoting "year" / "quarter" / etc. inside DATEDIFF /
    // DATEADD / DATEPART when a table happens to have a column of the same name.
    private static readonly System.Collections.Generic.HashSet<string> MssqlDateParts = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "year", "yyyy", "yy", "quarter", "qq", "q",
        "month", "mm", "m", "dayofyear", "dy", "y",
        "day", "dd", "d", "week", "wk", "ww", "weekday", "dw",
        "hour", "hh", "minute", "mi", "n", "second", "ss", "s",
        "millisecond", "ms", "microsecond", "mcs", "nanosecond", "ns",
        "tzoffset", "tz", "iso_week", "isowk", "isoww",
    };
    public System.Collections.Generic.IReadOnlySet<string> DatePartKeywords => MssqlDateParts;

    // ── Date arithmetic ───────────────────────────────────────────────────────
    public string DateAdd(string unit, int offset, string baseExpr) =>
        $"DATEADD({Normalize(unit)}, {offset.ToString(CultureInfo.InvariantCulture)}, {baseExpr})";

    public string DateDiff(string unit, string startExpr, string endExpr) =>
        $"DATEDIFF({Normalize(unit)}, {startExpr}, {endExpr})";

    public string DateFromParts(string yearExpr, string monthExpr, string dayExpr) =>
        $"DATEFROMPARTS({yearExpr}, {monthExpr}, {dayExpr})";

    public string DatePart(string unit, string expr)
    {
        var u = Normalize(unit);
        // T-SQL has dedicated YEAR(x), MONTH(x), DAY(x) for common cases — prefer these over
        // DATEPART for readability + parity with the prior hand-written compiler emission.
        return u switch
        {
            "year"  => $"YEAR({expr})",
            "month" => $"MONTH({expr})",
            "day"   => $"DAY({expr})",
            _       => $"DATEPART({u}, {expr})",
        };
    }

    public string DateTrunc(string unit, string expr)
    {
        // SQL Server has no native DATE_TRUNC; emit the DATEADD/DATEDIFF round-trip idiom that
        // the previous compiler used for time-series bucketing. This pattern preserves the
        // exact behaviour of <c>InjectTimeSeriesBucketingPhase</c>.
        var u = Normalize(unit);
        return $"DATEADD({u}, DATEDIFF({u}, 0, {expr}), 0)";
    }

    public string CastAsDate(string expr) => $"CAST({expr} AS DATE)";

    // ── Null handling ─────────────────────────────────────────────────────────
    public string NullCoalesce(string expr, string fallback) => $"ISNULL({expr}, {fallback})";

    // ── Casts ─────────────────────────────────────────────────────────────────
    public string CastAsString(string expr, int maxLength) =>
        maxLength > 0
            ? $"CAST({expr} AS NVARCHAR({maxLength}))"
            : $"CAST({expr} AS NVARCHAR(MAX))";

    public string CastAsInt(string expr) => $"CAST({expr} AS INT)";

    public string CastAsDecimal(string expr, int precision, int scale) =>
        $"CAST({expr} AS DECIMAL({precision},{scale}))";

    // ── Operators ─────────────────────────────────────────────────────────────
    public string LikeOperator => "LIKE";
    public string NotLikeOperator => "NOT LIKE";

    // ── Raw-SQL normalization (escape valve) ───────────────────────────────────
    // Rewrites Postgres/MySQL idioms a local LLM may emit into T-SQL. Implemented in the shared
    // TsqlDialectNormalizer helper; routed through the dialect so engine-specific behavior lives here.
    public string NormalizeRawSql(string sql) =>
        AnalystAgent.Pipeline.Stages.TsqlDialectNormalizer.Normalize(sql);

    /// <summary>Map common unit aliases to the canonical T-SQL token. Defensive: rejects bogus units.</summary>
    private static string Normalize(string unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "s" or "sec" or "second" or "seconds"   => "second",
        "min" or "minute" or "minutes"          => "minute",
        "h" or "hr" or "hour" or "hours"        => "hour",
        "d" or "day" or "days"                  => "day",
        "w" or "wk" or "week" or "weeks"        => "week",
        "mo" or "month" or "months"             => "month",
        "q" or "quarter" or "quarters"          => "quarter",
        "y" or "yr" or "year" or "years"        => "year",
        _ => "day",  // safe default; caller error
    };
}
