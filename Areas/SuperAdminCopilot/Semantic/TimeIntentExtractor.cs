namespace SuperAdminCopilot.Semantic;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Configuration;
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
    private readonly ILinguisticCuesProvider _cues;

    public TimeIntentExtractor(ITemporalParser parser, ILinguisticCuesProvider cues, ILogger<TimeIntentExtractor> logger)
    {
        _parser = parser;
        _cues = cues;
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

    // Arabic temporal patterns are NOT inlined here — they come from linguistic-cues.json
    // via ILinguisticCuesProvider. See LinkExternalLocaleTemporal() below.

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

        // 9. Non-English locale temporal phrases sourced from linguistic-cues.json
        //    (Arabic today, ar.temporal "اليوم", etc.). The English path above handles "en";
        //    here we iterate every OTHER locale's compiled patterns and map @-tokens → DateTime.
        LinkExternalLocaleTemporal(question, hits, sourcePhrases, consumed);

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

    // ── External-locale temporal extraction ──────────────────────────────────────────
    // Iterate every non-"en" locale block from linguistic-cues.json and apply its compiled
    // temporal regexes against the question. Each match's start/end @-tokens are resolved
    // to a concrete DateTime range via ResolveAtTokenRange. This keeps Arabic (and any future
    // dialect) vocab in JSON, not C#.
    private void LinkExternalLocaleTemporal(
        string question,
        List<TimeRange> hits,
        List<string> sourcePhrases,
        bool[] consumed)
    {
        var locales = _cues.Compiled.Locales;
        if (locales is null) return;
        foreach (var (code, locale) in locales)
        {
            if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) continue;   // English handled above by typed regexes
            if (locale?.Temporal is null) continue;
            foreach (var cue in locale.Temporal)
            {
                if (cue?.Pattern is null) continue;
                var m = cue.Pattern.Match(question);
                if (!m.Success) continue;
                if (RangeAlreadyConsumed(consumed, m.Index, m.Length)) continue;
                var captured = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : null;
                var range = ResolveAtTokenRange(cue.Start, cue.End, captured, cue.Label);
                if (range is null) continue;
                hits.Add(range);
                sourcePhrases.Add(m.Value);
                MarkConsumed(consumed, m.Index, m.Length);
            }
        }
    }

    // Map the planner's @-token vocabulary to concrete DateTime ranges. Same tokens that the
    // SQL compiler binds at execution time. Returns null when the token combo is unrecognized.
    private static TimeRange? ResolveAtTokenRange(string startToken, string? endToken, string? captured, string label)
    {
        if (string.IsNullOrEmpty(startToken)) return null;
        var start = ResolveAtToken(startToken, captured);
        if (start is null) return null;
        DateTime end;
        if (string.IsNullOrEmpty(endToken))
        {
            // Single-bound (e.g. @days:-7 with no end) → end at "now"
            end = DateTime.UtcNow;
        }
        else
        {
            var resolvedEnd = ResolveAtToken(endToken, captured);
            if (resolvedEnd is null) return null;
            end = resolvedEnd.Value;
        }
        return new TimeRange { Start = start.Value, End = end, Label = label ?? "" };
    }

    private static DateTime? ResolveAtToken(string token, string? captured)
    {
        // Numeric span tokens: @days:-{0}, @hours:-N, @months:-3 etc.
        // Boundary tokens: @today, @yesterday, @tomorrow, @week_start, @month_start, …
        if (token.StartsWith("@days:", StringComparison.Ordinal)) return ShiftToday(token, captured, d => DateTime.UtcNow.AddDays(d));
        if (token.StartsWith("@hours:", StringComparison.Ordinal)) return ShiftToday(token, captured, h => DateTime.UtcNow.AddHours(h));
        if (token.StartsWith("@weeks:", StringComparison.Ordinal)) return ShiftToday(token, captured, w => DateTime.UtcNow.AddDays(w * 7));
        if (token.StartsWith("@months:", StringComparison.Ordinal)) return ShiftToday(token, captured, m => DateTime.UtcNow.AddMonths(m));
        if (token.StartsWith("@years:", StringComparison.Ordinal)) return ShiftToday(token, captured, y => DateTime.UtcNow.AddYears(y));
        if (token.StartsWith("@quarters:", StringComparison.Ordinal)) return ShiftToday(token, captured, q => DateTime.UtcNow.AddMonths(q * 3));

        var today = DateTime.Today;
        return token switch
        {
            "@today"               => today,
            "@yesterday"           => today.AddDays(-1),
            "@tomorrow"            => today.AddDays(1),
            "@week_start"          => WeekStart(today, 0),
            "@last_week_start"     => WeekStart(today, -1),
            "@month_start"         => new DateTime(today.Year, today.Month, 1),
            "@last_month_start"    => new DateTime(today.Year, today.Month, 1).AddMonths(-1),
            "@year_start"          => new DateTime(today.Year, 1, 1),
            "@last_year_start"     => new DateTime(today.Year - 1, 1, 1),
            "@quarter_start"       => QuarterStart(today, 0),
            "@last_quarter_start"  => QuarterStart(today, -1),
            "@q1_start"            => new DateTime(today.Year, 1, 1),
            "@q2_start"            => new DateTime(today.Year, 4, 1),
            "@q3_start"            => new DateTime(today.Year, 7, 1),
            "@q4_start"            => new DateTime(today.Year, 10, 1),
            "@q1_end"              => new DateTime(today.Year, 4, 1),
            "@q2_end"              => new DateTime(today.Year, 7, 1),
            "@q3_end"              => new DateTime(today.Year, 10, 1),
            "@q4_end"              => new DateTime(today.Year + 1, 1, 1),
            _                      => null,
        };
    }

    private static DateTime? ShiftToday(string token, string? captured, Func<int, DateTime> apply)
    {
        // Token form: "@unit:-N" (literal N) or "@unit:-{0}" (with captured value).
        var colon = token.IndexOf(':');
        if (colon < 0) return null;
        var amount = token.Substring(colon + 1);
        if (amount.Contains("{0}"))
            amount = amount.Replace("{0}", captured ?? "0");
        if (!int.TryParse(amount, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return null;
        return apply(n);
    }

    private static DateTime WeekStart(DateTime today, int weekOffset)
    {
        int dow = ((int)today.DayOfWeek + 6) % 7;   // ISO: Monday-anchored
        return today.AddDays(-dow + (weekOffset * 7));
    }

    private static DateTime QuarterStart(DateTime today, int qOffset)
    {
        int q = (today.Month - 1) / 3;
        return new DateTime(today.Year, q * 3 + 1, 1).AddMonths(qOffset * 3);
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
