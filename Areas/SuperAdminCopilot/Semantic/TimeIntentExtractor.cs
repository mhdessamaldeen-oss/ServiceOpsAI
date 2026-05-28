namespace SuperAdminCopilot.Semantic;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Models;

/// <summary>
/// Phase 07 — the single owner of temporal-intent extraction. Consolidates the work that
/// previously lived in <c>InjectTemporalFilterFromQuestionPhase</c>,
/// <c>SpecificYearMonthFilterPhase</c>, parts of <c>RangeFilterFromQuestionPhase</c>, and the
/// year-anchored <c>@q{N}_start</c> tokens. Returns a structured <see cref="TimeIntent"/>
/// that the SQL compiler reads directly — no more "5 phases race each other to inject
/// duplicate date filters."
/// </summary>
public interface ITimeIntentExtractor
{
    /// <summary>
    /// Parse the question and return a populated <see cref="TimeIntent"/>. Always returns
    /// non-null — when no temporal phrase is present, <see cref="TimeIntent.Kind"/> =
    /// <see cref="TimeIntentKind.Unqualified"/>.
    /// </summary>
    TimeIntent Extract(string question);
}

internal sealed class TimeIntentExtractor : ITimeIntentExtractor
{
    private readonly ILogger<TimeIntentExtractor> _logger;
    private readonly ITemporalParser _parser;

    public TimeIntentExtractor(ITemporalParser parser, ILogger<TimeIntentExtractor> logger)
    {
        _parser = parser;
        _logger = logger;
    }

    // ── Patterns (ordered by specificity) ─────────────────────────────────────────────
    // Each pattern produces ONE TimeRange. Multiple matches in the same question fold into
    // MULTI_PERIOD intent. Order matters: more-specific patterns (Q1 2027) run before
    // less-specific ones (in 2027) so the more-specific match consumes the substring first.

    private static readonly Regex YearQuarterRx = new(
        @"\b(?:q([1-4])|(first|second|third|fourth)\s+quarter)\s+(?:of\s+|in\s+)?(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearMonthRx = new(
        @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?:of\s+|in\s+)?(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EntireYearRx = new(
        @"\b(?:in|during|throughout|for|year)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SinceYearRx = new(
        @"\b(?:since|after|from)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BeforeYearRx = new(
        @"\b(?:before|prior\s+to|until)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Relative patterns — yield Kind.Relative ranges anchored to DateTime.UtcNow.

    private static readonly Regex LastNDaysRx = new(
        @"\b(?:in\s+the\s+|over\s+the\s+|during\s+the\s+|for\s+the\s+)?(?:last|past)\s+(\d+)\s+(hour|day|week|month|year)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentBoundaryRx = new(
        @"\b(today|yesterday|tomorrow)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentPeriodRx = new(
        @"\b(this|last)\s+(week|month|quarter|year)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentQuarterRx = new(
        @"\b(?:q([1-4])|(first|second|third|fourth)\s+quarter)\b(?!\s+(?:of\s+|in\s+)?\d{4})",  // not followed by a year (those go to YearQuarterRx)
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Multi-period composition pattern — detects "X and Y" / "X vs Y" / "X compared to Y" /
    // Arabic "X و Y" / "X مقابل Y". Used to split a single question into multiple TimeRanges.
    private static readonly Regex MultiPeriodSplitRx = new(
        @"\b(?:and|or|vs|versus|compared\s+to|against)\b|و|مقابل",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Arabic temporal phrases. Map to relative ranges anchored to "now".
    private static readonly (Regex Match, Func<TimeRange> BuildRange)[] ArabicRelativePatterns =
    {
        (new Regex(@"اليوم", RegexOptions.Compiled), () => DayRange(0, "اليوم")),
        (new Regex(@"(?:أمس|امس)", RegexOptions.Compiled), () => DayRange(-1, "أمس")),
        (new Regex(@"(?:هذا\s*الأسبوع|هذا\s*الاسبوع|هالأسبوع)", RegexOptions.Compiled), () => WeekRange(0, "هذا الأسبوع")),
        (new Regex(@"الأسبوع\s*الماضي|الاسبوع\s*الماضي", RegexOptions.Compiled), () => WeekRange(-1, "الأسبوع الماضي")),
        (new Regex(@"(?:هذا\s*الشهر|هالشهر)", RegexOptions.Compiled), () => MonthRange(0, "هذا الشهر")),
        (new Regex(@"الشهر\s*الماضي", RegexOptions.Compiled), () => MonthRange(-1, "الشهر الماضي")),
        (new Regex(@"(?:هذا\s*العام|هذه\s*السنة|هالسنة)", RegexOptions.Compiled), () => YearRange(0, "هذه السنة")),
        (new Regex(@"العام\s*الماضي|السنة\s*الماضية", RegexOptions.Compiled), () => YearRange(-1, "العام الماضي")),
    };

    private static readonly Dictionary<string, int> WordQuarter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
    };

    private static readonly Dictionary<string, int> MonthIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1,    ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3,      ["apr"] = 4, ["april"] = 4,
        ["may"] = 5,
        ["jun"] = 6, ["june"] = 6,       ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8,     ["sep"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10,  ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    public TimeIntent Extract(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new TimeIntent { Kind = TimeIntentKind.Unqualified };

        var hits = new List<TimeRange>();
        var sourcePhrases = new List<string>();
        var consumed = new bool[question.Length];

        // 1. Year-qualified quarter ("Q1 2027").
        foreach (Match m in YearQuarterRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            var q = m.Groups[1].Success ? int.Parse(m.Groups[1].Value)
                   : WordQuarter.TryGetValue(m.Groups[2].Value, out var qi) ? qi : 0;
            if (q < 1 || q > 4) continue;
            if (!int.TryParse(m.Groups[3].Value, out var y) || y < 1900 || y > 2200) continue;
            var startMonth = (q - 1) * 3 + 1;
            hits.Add(new TimeRange
            {
                Start = new DateTime(y, startMonth, 1),
                End = AddMonths(new DateTime(y, startMonth, 1), 3),
                Label = $"Q{q} {y}",
            });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 2. Year-qualified month ("February 2019").
        foreach (Match m in YearMonthRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            if (!MonthIndex.TryGetValue(m.Groups[1].Value, out var month)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var y) || y < 1900 || y > 2200) continue;
            hits.Add(new TimeRange
            {
                Start = new DateTime(y, month, 1),
                End = AddMonths(new DateTime(y, month, 1), 1),
                Label = $"{Capitalize(m.Groups[1].Value)} {y}",
            });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 3. Entire year ("in 2027", "during 2025", "year 2024").
        foreach (Match m in EntireYearRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            if (!int.TryParse(m.Groups[1].Value, out var y) || y < 1900 || y > 2200) continue;
            hits.Add(new TimeRange
            {
                Start = new DateTime(y, 1, 1),
                End = new DateTime(y + 1, 1, 1),
                Label = $"Year {y}",
            });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 4. Since YEAR / Before YEAR — open-ended.
        foreach (Match m in SinceYearRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            if (!int.TryParse(m.Groups[1].Value, out var y) || y < 1900 || y > 2200) continue;
            hits.Add(new TimeRange { Start = new DateTime(y, 1, 1), End = null, Label = $"Since {y}" });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }
        foreach (Match m in BeforeYearRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            if (!int.TryParse(m.Groups[1].Value, out var y) || y < 1900 || y > 2200) continue;
            hits.Add(new TimeRange { Start = null, End = new DateTime(y, 1, 1), Label = $"Before {y}" });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 5. Relative: "last N {hour|day|week|month|year}".
        var now = DateTime.UtcNow;
        foreach (Match m in LastNDaysRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            if (!int.TryParse(m.Groups[1].Value, out var n) || n <= 0) continue;
            var unit = m.Groups[2].Value.ToLowerInvariant();
            DateTime start = unit switch
            {
                "hour"  => now.AddHours(-n),
                "day"   => now.AddDays(-n),
                "week"  => now.AddDays(-n * 7),
                "month" => now.AddMonths(-n),
                "year"  => now.AddYears(-n),
                _ => now.AddDays(-n),
            };
            hits.Add(new TimeRange { Start = start, End = now, Label = $"Last {n} {unit}{(n > 1 ? "s" : "")}" });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 6. Boundary days: today / yesterday / tomorrow.
        foreach (Match m in CurrentBoundaryRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            int delta = m.Value.ToLowerInvariant() switch { "today" => 0, "yesterday" => -1, "tomorrow" => 1, _ => 0 };
            hits.Add(DayRange(delta, Capitalize(m.Value)));
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 7. "this/last week|month|quarter|year".
        foreach (Match m in CurrentPeriodRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            int offset = m.Groups[1].Value.Equals("last", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
            var unit = m.Groups[2].Value.ToLowerInvariant();
            TimeRange? range = unit switch
            {
                "week"    => WeekRange(offset, $"{Capitalize(m.Groups[1].Value)} week"),
                "month"   => MonthRange(offset, $"{Capitalize(m.Groups[1].Value)} month"),
                "quarter" => QuarterRange(offset, $"{Capitalize(m.Groups[1].Value)} quarter"),
                "year"    => YearRange(offset, $"{Capitalize(m.Groups[1].Value)} year"),
                _ => null,
            };
            if (range is null) continue;
            hits.Add(range);
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 8. Current-year quarter ("Q1" without explicit year).
        foreach (Match m in CurrentQuarterRx.Matches(question))
        {
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            var q = m.Groups[1].Success ? int.Parse(m.Groups[1].Value)
                   : WordQuarter.TryGetValue(m.Groups[2].Value, out var qi) ? qi : 0;
            if (q < 1 || q > 4) continue;
            var thisYear = DateTime.UtcNow.Year;
            var startMonth = (q - 1) * 3 + 1;
            hits.Add(new TimeRange
            {
                Start = new DateTime(thisYear, startMonth, 1),
                End = AddMonths(new DateTime(thisYear, startMonth, 1), 3),
                Label = $"Q{q}",
            });
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // 9. Arabic relative phrases.
        foreach (var (pat, build) in ArabicRelativePatterns)
        {
            var m = pat.Match(question);
            if (!m.Success) continue;
            if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
            hits.Add(build());
            sourcePhrases.Add(m.Value);
            MarkConsumed(consumed, m.Index, m.Length);
        }

        // Deduplicate by (Start, End, Label) so a phrase that matches via two paths only
        // produces one range. Ordering: keep first-occurrence order so the explainer's
        // SourcePhrase narrative tracks the question's left-to-right flow.
        hits = hits
            .GroupBy(r => $"{r.Start:O}|{r.End:O}|{r.Label}")
            .Select(g => g.First())
            .ToList();

        // No hits → unqualified intent. Compiler emits no temporal WHERE.
        if (hits.Count == 0)
        {
            return new TimeIntent { Kind = TimeIntentKind.Unqualified };
        }

        // One hit → single range. Mark ABSOLUTE if start/end are concrete dates from a year
        // mention, RELATIVE otherwise.
        if (hits.Count == 1)
        {
            var range = hits[0];
            var isAbsolute = range.Label.Contains("Q") && range.Label.Contains(' ')   // "Q1 2025"
                          || range.Label.StartsWith("Year ") || range.Label.StartsWith("Since ") || range.Label.StartsWith("Before ")
                          || (range.Label.Length > 0 && char.IsUpper(range.Label[0]) && range.Label.Contains(' ')
                              && (range.Label.EndsWith("2019") || range.Label.EndsWith("2020") || range.Label.EndsWith("2021")
                               || range.Label.EndsWith("2022") || range.Label.EndsWith("2023") || range.Label.EndsWith("2024")
                               || range.Label.EndsWith("2025") || range.Label.EndsWith("2026") || range.Label.EndsWith("2027")
                               || range.Label.EndsWith("2028") || range.Label.EndsWith("2029") || range.Label.EndsWith("2030")));
            return new TimeIntent
            {
                Kind = isAbsolute ? TimeIntentKind.Absolute : TimeIntentKind.Relative,
                Range = range,
                SourcePhrase = sourcePhrases.FirstOrDefault(),
            };
        }

        // Multiple hits → MULTI_PERIOD. Compiler emits one UNION-ALL leg per range.
        return new TimeIntent
        {
            Kind = TimeIntentKind.MultiPeriod,
            Periods = hits,
            SourcePhrase = string.Join(" + ", sourcePhrases),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static bool RangeAlreadyConsumed(bool[] consumed, int start, int length)
    {
        for (int i = 0; i < length && start + i < consumed.Length; i++)
            if (consumed[start + i]) return true;
        return false;
    }

    private static void MarkConsumed(bool[] consumed, int start, int length)
    {
        for (int i = 0; i < length && start + i < consumed.Length; i++) consumed[start + i] = true;
    }

    private static DateTime AddMonths(DateTime d, int m) => d.AddMonths(m);

    private static TimeRange DayRange(int dayOffset, string label)
    {
        var d = DateTime.Today.AddDays(dayOffset);
        return new TimeRange { Start = d, End = d.AddDays(1), Label = label };
    }

    private static TimeRange WeekRange(int weekOffset, string label)
    {
        var today = DateTime.Today;
        // ISO-week-start: Monday. Compute start of THIS week, then shift.
        int dow = ((int)today.DayOfWeek + 6) % 7;
        var thisWeekStart = today.AddDays(-dow);
        var start = thisWeekStart.AddDays(weekOffset * 7);
        return new TimeRange { Start = start, End = start.AddDays(7), Label = label };
    }

    private static TimeRange MonthRange(int monthOffset, string label)
    {
        var today = DateTime.Today;
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var start = firstOfThisMonth.AddMonths(monthOffset);
        return new TimeRange { Start = start, End = start.AddMonths(1), Label = label };
    }

    private static TimeRange QuarterRange(int qOffset, string label)
    {
        var today = DateTime.Today;
        int q = (today.Month - 1) / 3;                      // 0..3
        var startOfThisQ = new DateTime(today.Year, q * 3 + 1, 1);
        var start = startOfThisQ.AddMonths(qOffset * 3);
        return new TimeRange { Start = start, End = start.AddMonths(3), Label = label };
    }

    private static TimeRange YearRange(int yearOffset, string label)
    {
        var today = DateTime.Today;
        var start = new DateTime(today.Year + yearOffset, 1, 1);
        return new TimeRange { Start = start, End = start.AddYears(1), Label = label };
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
}
