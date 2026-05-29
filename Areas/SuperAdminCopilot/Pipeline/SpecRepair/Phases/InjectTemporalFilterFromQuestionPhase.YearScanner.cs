namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Year-qualified period scanner for <see cref="InjectTemporalFilterFromQuestionPhase"/>.
///
/// <para>Detects phrases that BIND a period to a specific year (rather than the current one):</para>
/// <list type="bullet">
///   <item>Q1 2027  /  q3 of 2025  /  first quarter 2026  /  third quarter of 2024</item>
///   <item>January 2025  /  Feb 2019  /  March of 2026</item>
///   <item>in 2027  /  during 2025  /  throughout 2026  /  for 2024  (entire year)</item>
///   <item>Arabic: في 2025 / خلال 2025 / لسنة 2025 / لعام 2025 / منذ YYYY / قبل YYYY</item>
/// </list>
///
/// <para>Emits <c>@yearmonth:YYYY:M</c> tokens for the half-open interval. Removes the
/// matched substring from the question so the year-anchored Q1..Q4 / month patterns don't
/// also fire on the same text. Multi-period inputs ("Q1 2027 vs Feb 2019") produce multiple
/// hits, which downstream becomes a UNION-ALL PeriodComparison.</para>
///
/// <para>This code is intentionally NOT migrated to linguistic-cues.json — the patterns are
/// PARAMETRIC (capture a year via <c>(\d{4})</c>, do arithmetic on it) rather than vocabulary,
/// so they belong in C# parser code, not declarative JSON.</para>
/// </summary>
internal sealed partial class InjectTemporalFilterFromQuestionPhase
{
    private static readonly Regex YearQuarterRx = new(
        @"\b(?:q([1-4])|(first|second|third|fourth)\s+quarter)\s+(?:of\s+|in\s+)?(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearMonthRx = new(
        @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?:of\s+|in\s+)?(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EntireYearRx = new(
        @"\b(?:in|during|throughout|for|year)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Arabic entire-year prepositions: في 2025 / خلال 2025 / لسنة 2025 / لعام 2025.
    // Anchored without \b — Arabic word boundaries behave differently when prepositions
    // attach as letters. The compiler's `@yearmonth:Y:M` token expander handles arithmetic.
    private static readonly Regex EntireYearRxAr = new(
        @"(?:في|خلال|لسنة|لعام|سنة|عام)\s+(\d{4})",
        RegexOptions.Compiled);

    // Arabic "since YYYY" / "before YYYY" — single-bound year filters.
    private static readonly Regex SinceYearRxAr = new(
        @"(?:منذ|من\s+سنة|من\s+عام)\s+(\d{4})",
        RegexOptions.Compiled);

    private static readonly Regex BeforeYearRxAr = new(
        @"(?:قبل|قبل\s+سنة|قبل\s+عام)\s+(\d{4})",
        RegexOptions.Compiled);

    private static readonly System.Collections.Generic.Dictionary<string, int> WordQuarter =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
        };

    private static readonly System.Collections.Generic.Dictionary<string, int> MonthIndex =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["january"] = 1,
            ["feb"] = 2, ["february"] = 2,
            ["mar"] = 3, ["march"] = 3,
            ["apr"] = 4, ["april"] = 4,
            ["may"] = 5,
            ["jun"] = 6, ["june"] = 6,
            ["jul"] = 7, ["july"] = 7,
            ["aug"] = 8, ["august"] = 8,
            ["sep"] = 9, ["september"] = 9,
            ["oct"] = 10, ["october"] = 10,
            ["nov"] = 11, ["november"] = 11,
            ["dec"] = 12, ["december"] = 12,
        };

    /// <summary>
    /// Scans for year-qualified periods, mutates <paramref name="hits"/> with one entry per
    /// detected period using <c>@yearmonth</c> tokens, and returns the question with matched
    /// substrings blanked out (to prevent the year-anchored patterns from re-matching).
    /// </summary>
    private static string ScanYearQualifiedPeriods(
        string q,
        System.Collections.Generic.List<(string Label, string StartToken, string? EndToken, string Op)> hits,
        System.Collections.Generic.HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var sb = new System.Text.StringBuilder(q);

        // Quarter+year — most specific first.
        foreach (Match m in YearQuarterRx.Matches(q))
        {
            int quarter = m.Groups[1].Success ? int.Parse(m.Groups[1].Value)
                          : WordQuarter.TryGetValue(m.Groups[2].Value, out var qi) ? qi : 0;
            if (quarter < 1 || quarter > 4) continue;
            if (!int.TryParse(m.Groups[3].Value, out var year) || year < 1900 || year > 2200) continue;
            int startMonth = (quarter - 1) * 3 + 1;
            int endMonth = startMonth + 3;                       // exclusive upper
            int endYear = year;
            if (endMonth > 12) { endMonth = 1; endYear = year + 1; }
            var startTok = $"@yearmonth:{year}:{startMonth}";
            var endTok = $"@yearmonth:{endYear}:{endMonth}";
            var key = startTok + "|" + endTok;
            if (!seen.Add(key)) continue;
            hits.Add(($"Q{quarter} {year}", startTok, endTok, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        // Month+year.
        foreach (Match m in YearMonthRx.Matches(q))
        {
            if (!MonthIndex.TryGetValue(m.Groups[1].Value, out var month)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var year) || year < 1900 || year > 2200) continue;
            int endMonth = month + 1;
            int endYear = year;
            if (endMonth > 12) { endMonth = 1; endYear = year + 1; }
            var startTok = $"@yearmonth:{year}:{month}";
            var endTok = $"@yearmonth:{endYear}:{endMonth}";
            var key = startTok + "|" + endTok;
            if (!seen.Add(key)) continue;
            hits.Add(($"{m.Groups[1].Value} {year}", startTok, endTok, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        // Bare year (EN) — most general, run last. "in 2027" / "for year 2024".
        foreach (Match m in EntireYearRx.Matches(q))
        {
            if (!int.TryParse(m.Groups[1].Value, out var year) || year < 1900 || year > 2200) continue;
            var startTok = $"@yearmonth:{year}:1";
            var endTok = $"@yearmonth:{year + 1}:1";
            var key = startTok + "|" + endTok;
            if (!seen.Add(key)) continue;
            hits.Add(($"Year {year}", startTok, endTok, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        // Arabic entire-year — "في 2025" / "خلال 2025" / "لسنة 2025".
        foreach (Match m in EntireYearRxAr.Matches(q))
        {
            if (!int.TryParse(m.Groups[1].Value, out var year) || year < 1900 || year > 2200) continue;
            var startTok = $"@yearmonth:{year}:1";
            var endTok = $"@yearmonth:{year + 1}:1";
            var key = startTok + "|" + endTok;
            if (!seen.Add(key)) continue;
            hits.Add(($"عام {year}", startTok, endTok, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        // Arabic "since YYYY" — single-bound gte filter.
        foreach (Match m in SinceYearRxAr.Matches(q))
        {
            if (!int.TryParse(m.Groups[1].Value, out var year) || year < 1900 || year > 2200) continue;
            var startTok = $"@yearmonth:{year}:1";
            var key = startTok + "|gte-since";
            if (!seen.Add(key)) continue;
            hits.Add(($"منذ {year}", startTok, null, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        // Arabic "before YYYY" — single-bound lt filter.
        foreach (Match m in BeforeYearRxAr.Matches(q))
        {
            if (!int.TryParse(m.Groups[1].Value, out var year) || year < 1900 || year > 2200) continue;
            var startTok = $"@yearmonth:{year}:1";
            var key = startTok + "|lt-before";
            if (!seen.Add(key)) continue;
            hits.Add(($"قبل {year}", startTok, null, SpecConst.FilterOps.Lt));
            BlankRange(sb, m.Index, m.Length);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Replace [start, start+length) with spaces so downstream regex patterns don't
    /// re-match. Keeping the string length identical preserves index-based reasoning.
    /// </summary>
    private static void BlankRange(System.Text.StringBuilder sb, int start, int length)
    {
        for (int i = 0; i < length && start + i < sb.Length; i++) sb[start + i] = ' ';
    }
}
