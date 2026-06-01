namespace SuperAdminCopilot.Compilation.Dialects;

using System.Globalization;

/// <summary>
/// PostgreSQL dialect. Proves the <see cref="ISqlDialect"/> abstraction is real, not theoretical:
/// switching to Postgres is a config swap (DI binding), not a compiler rewrite. This class
/// produces valid PG SQL for every method on the interface. It is unit-tested against expected
/// output strings; full end-to-end validation requires running against a Postgres instance,
/// which is the obvious next-week test once an integration environment exists.
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    public string Name => "postgres";

    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "\"\"";
        // Strip any existing double-quotes, plus T-SQL square brackets the planner may emit
        // when speaking through the compiler. PG escapes " by doubling it.
        var bare = identifier.Trim('"', '[', ']').Replace("\"", "\"\"");
        return "\"" + bare + "\"";
    }

    public string QuoteQualified(string table, string column) =>
        QuoteIdentifier(table) + "." + QuoteIdentifier(column);

    public char IdentifierQuoteOpen  => '"';
    public char IdentifierQuoteClose => '"';

    // ── LIMIT / OFFSET (PG style) ─────────────────────────────────────────────
    public bool LimitGoesBeforeColumns => false;     // PG uses trailing LIMIT clause

    /// <summary>PG has no SELECT-TOP — always returns empty; <see cref="LimitOffsetClause"/> carries the limit.</summary>
    public string TopClause(int limit) => "";

    public string LimitOffsetClause(int? limit, int? offset)
    {
        var sb = new System.Text.StringBuilder();
        if (limit is > 0) sb.Append("LIMIT ").Append(limit.Value);
        if (offset is > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("OFFSET ").Append(offset.Value);
        }
        return sb.ToString();
    }

    // ── Now / today ───────────────────────────────────────────────────────────
    public string NowExpression => "NOW()";
    public string CurrentDateExpression => "CURRENT_DATE";

    // PostgreSQL date-part keywords accepted by EXTRACT() / DATE_TRUNC() / INTERVAL.
    // Avoid mis-quoting these as column references inside date function calls.
    private static readonly System.Collections.Generic.HashSet<string> PgDateParts = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "century", "decade", "year", "quarter", "month", "week",
        "day", "doy", "dow", "isodow", "isoyear",
        "hour", "minute", "second", "millisecond", "microsecond",
        "epoch", "timezone", "timezone_hour", "timezone_minute",
    };
    public System.Collections.Generic.IReadOnlySet<string> DatePartKeywords => PgDateParts;

    // ── Date arithmetic ───────────────────────────────────────────────────────
    public string DateAdd(string unit, int offset, string baseExpr)
    {
        var u = Normalize(unit);
        // Postgres uses interval arithmetic: NOW() + INTERVAL '7 day'
        var n = offset.ToString(CultureInfo.InvariantCulture);
        return $"({baseExpr} + INTERVAL '{n} {u}')";
    }

    public string DateDiff(string unit, string startExpr, string endExpr)
    {
        // PG: there's no DATEDIFF. Closest idiom is EXTRACT(EPOCH FROM end - start) for seconds,
        // then divide for larger units. For day-level differences, casting to DATE first and
        // subtracting gives an integer day count.
        var u = Normalize(unit);
        return u switch
        {
            "day"   => $"({endExpr}::date - {startExpr}::date)",
            "hour"  => $"(EXTRACT(EPOCH FROM ({endExpr}::timestamp - {startExpr}::timestamp))::int / 3600)",
            "minute"=> $"(EXTRACT(EPOCH FROM ({endExpr}::timestamp - {startExpr}::timestamp))::int / 60)",
            "second"=> $"EXTRACT(EPOCH FROM ({endExpr}::timestamp - {startExpr}::timestamp))::int",
            "week"  => $"(({endExpr}::date - {startExpr}::date) / 7)",
            "month" => $"((EXTRACT(YEAR FROM {endExpr}) - EXTRACT(YEAR FROM {startExpr})) * 12 + EXTRACT(MONTH FROM {endExpr}) - EXTRACT(MONTH FROM {startExpr}))::int",
            "year"  => $"(EXTRACT(YEAR FROM {endExpr}) - EXTRACT(YEAR FROM {startExpr}))::int",
            "quarter"=>$"((EXTRACT(YEAR FROM {endExpr}) - EXTRACT(YEAR FROM {startExpr})) * 4 + EXTRACT(QUARTER FROM {endExpr}) - EXTRACT(QUARTER FROM {startExpr}))::int",
            _       => $"(EXTRACT(EPOCH FROM ({endExpr}::timestamp - {startExpr}::timestamp))::int / 86400)",
        };
    }

    public string DateFromParts(string yearExpr, string monthExpr, string dayExpr) =>
        $"MAKE_DATE({yearExpr}, {monthExpr}, {dayExpr})";

    public string DatePart(string unit, string expr) =>
        $"EXTRACT({Normalize(unit).ToUpperInvariant()} FROM {expr})::int";

    public string DateTrunc(string unit, string expr) =>
        $"DATE_TRUNC('{Normalize(unit)}', {expr})";

    public string CastAsDate(string expr) => $"({expr})::date";

    // ── Null handling ─────────────────────────────────────────────────────────
    public string NullCoalesce(string expr, string fallback) => $"COALESCE({expr}, {fallback})";

    // ── Casts ─────────────────────────────────────────────────────────────────
    public string CastAsString(string expr, int maxLength) =>
        maxLength > 0
            ? $"({expr})::varchar({maxLength})"
            : $"({expr})::text";

    public string CastAsInt(string expr) => $"({expr})::int";

    public string CastAsDecimal(string expr, int precision, int scale) =>
        $"({expr})::numeric({precision},{scale})";

    // ── Operators ─────────────────────────────────────────────────────────────
    public string LikeOperator => "LIKE";
    public string NotLikeOperator => "NOT LIKE";

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
        _ => "day",
    };
}
