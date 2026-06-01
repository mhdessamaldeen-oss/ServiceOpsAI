namespace SuperAdminCopilot.Compilation;

/// <summary>
/// Expand planner-emitted '@'-tokens (<c>@today</c>, <c>@yesterday</c>, <c>@week_start</c>,
/// <c>@days:-7</c>, <c>@yearmonth:2027:1</c>, etc.) into inline dialect-specific date expressions.
///
/// <para>Extracted from <c>SqlCompiler.Where.cs</c> as part of the 2026-06-01 de-couple pass.
/// Previously the grammar lived in a 100-line static <c>TryExpandTemporalToken</c> inside
/// the compiler — but both the WHERE-builder AND <c>QualifyColumnsInExpression</c> need to
/// resolve these tokens, and a second caller had just been added. Centralising the grammar
/// here turns it into a single collaborator that any future compiler stage (or external
/// tool) can reuse, and a future externalization to JSON (Cat 4 LOW item in the review plan)
/// touches one method instead of an embedded switch.</para>
///
/// <para>Every emission routes through <see cref="Dialects.ISqlDialect"/>, so the same token
/// expands to T-SQL on MSSQL (<c>DATEADD(day, -1, CAST(GETDATE() AS DATE))</c>) and Postgres
/// on PG (<c>(CURRENT_DATE + INTERVAL '-1 day')</c>) without changing this interface.</para>
/// </summary>
public interface ITemporalTokenizer
{
    /// <summary>
    /// Try to expand <paramref name="token"/> into an inline SQL expression.
    /// Returns false when the token is not recognised — caller should fall back to its own
    /// handling (parameterise as string, leave verbatim, warn, etc.).
    /// </summary>
    bool TryExpand(string token, Dialects.ISqlDialect dialect, out string sqlExpr);
}

internal sealed class TemporalTokenizer : ITemporalTokenizer
{
    public bool TryExpand(string token, Dialects.ISqlDialect dialect, out string sqlExpr)
    {
        sqlExpr = "";
        if (string.IsNullOrEmpty(token) || token[0] != '@') return false;
        var t = token.AsSpan(1).Trim().ToString().ToLowerInvariant();

        var now = dialect.NowExpression;
        var today = dialect.CurrentDateExpression;

        switch (t)
        {
            case "now":           sqlExpr = now; return true;
            case "today":         sqlExpr = today; return true;
            case "yesterday":     sqlExpr = dialect.DateAdd("day", -1, today); return true;
            case "tomorrow":      sqlExpr = dialect.DateAdd("day",  1, today); return true;
            case "today_start":
            case "day_start":     sqlExpr = today; return true;

            case "week_start":    sqlExpr = dialect.DateTrunc("week",    now); return true;
            case "month_start":   sqlExpr = dialect.DateTrunc("month",   now); return true;
            case "year_start":    sqlExpr = dialect.DateTrunc("year",    now); return true;
            case "quarter_start": sqlExpr = dialect.DateTrunc("quarter", now); return true;

            case "last_week_start":    sqlExpr = dialect.DateAdd("week",    -1, dialect.DateTrunc("week",    now)); return true;
            case "last_month_start":   sqlExpr = dialect.DateAdd("month",   -1, dialect.DateTrunc("month",   now)); return true;
            case "last_year_start":    sqlExpr = dialect.DateAdd("year",    -1, dialect.DateTrunc("year",    now)); return true;
            case "last_quarter_start": sqlExpr = dialect.DateAdd("quarter", -1, dialect.DateTrunc("quarter", now)); return true;

            // Named quarter starts/ends, anchored to the current year. q{N}_start = first day
            // of quarter N this year. q{N}_end = first day of quarter N+1 (half-open interval).
            // Q4_end rolls into the next year so a "Q4" range remains valid.
            case "q1_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "1",  "1"); return true;
            case "q1_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "4",  "1"); return true;
            case "q2_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "4",  "1"); return true;
            case "q2_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "7",  "1"); return true;
            case "q3_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "7",  "1"); return true;
            case "q3_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "10", "1"); return true;
            case "q4_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "10", "1"); return true;
            case "q4_end":   sqlExpr = dialect.DateFromParts($"({dialect.DatePart("year", now)} + 1)", "1", "1"); return true;
        }

        // Year-specific anchor: "@yearmonth:YYYY:M" → first day of month M in year YYYY.
        // Used by InjectTemporalFilterFromQuestion when the question carries an explicit year.
        if (t.StartsWith("yearmonth:", System.StringComparison.Ordinal))
        {
            var parts = t.Substring("yearmonth:".Length).Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var ym_year)
                && int.TryParse(parts[1], out var ym_month)
                && ym_year >= 1900 && ym_year <= 2200
                && ym_month >= 1 && ym_month <= 12)
            {
                sqlExpr = dialect.DateFromParts(ym_year.ToString(), ym_month.ToString(), "1");
                return true;
            }
        }

        // "days:-7" / "hours:-24" / "weeks:-2" / "months:-3" / "years:-1"
        var colonIdx = t.IndexOf(':');
        if (colonIdx > 0 && colonIdx < t.Length - 1)
        {
            var unit = t[..colonIdx];
            if (int.TryParse(t[(colonIdx + 1)..], out var offset))
            {
                var sqlUnit = unit switch
                {
                    "second" or "seconds" or "sec" => "second",
                    "minute" or "minutes" or "min" => "minute",
                    "hour"   or "hours"   or "hr"  => "hour",
                    "day"    or "days"             => "day",
                    "week"   or "weeks"   or "wk"  => "week",
                    "month"  or "months"  or "mo"  => "month",
                    "year"   or "years"   or "yr"  => "year",
                    _ => null
                };
                if (sqlUnit is not null)
                {
                    // Day-or-coarser offsets anchor on midnight so range comparisons against
                    // datetime columns include the full day.
                    var baseExpr = sqlUnit is "second" or "minute" or "hour" ? now : today;
                    sqlExpr = dialect.DateAdd(sqlUnit, offset, baseExpr);
                    return true;
                }
            }
        }

        return false;
    }
}
