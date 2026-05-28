namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Defensive phase: when the question contains a temporal scope keyword ("today", "this week",
/// "last 30 days", "Q1 of this year", etc.) and the spec has NO filter on the root entity's
/// default date column, inject one. Catches the common silent-failure where the LLM drops the
/// time constraint entirely (e.g. "how many bills issued this week" → WHERE Status = 'Issued'
/// with no date filter, returning every Issued bill ever).
///
/// <para>The injected filter uses planner '@'-tokens (<c>@today</c>, <c>@week_start</c>,
/// <c>@days:-30</c>) which <c>SqlCompiler.TryExpandTemporalToken</c> already expands to inline
/// T-SQL. No new compiler surface required.</para>
///
/// <para>Skip conditions: spec already has a filter on the date column; question lacks any
/// temporal keyword; root entity has no resolved date column.</para>
/// </summary>
internal sealed class InjectTemporalFilterFromQuestionPhase : ISpecRepairPhase
{
    public string Name => "InjectTemporalFilterFromQuestion";
    public string Covers => "Temporal keyword in question (today, this week, last 30 days, Q1, …) without a date filter → inject WHERE";

    // Each pattern maps to either (a) a single @-token, or (b) a pair of @-tokens defining a
    // half-open range [start, end). The compiler handles single-token via gte/lt; range
    // patterns emit two filters.
    private sealed record TemporalPattern(Regex Match, string Token, string? RangeEnd = null, string Op = "gte");

    private static readonly Regex Ar_Today = new(@"اليوم\s*$|اليوم\b", RegexOptions.Compiled);
    private static readonly Regex Ar_Yesterday = new(@"أمس|امس", RegexOptions.Compiled);
    private static readonly Regex Ar_ThisWeek = new(@"هذا\s*الأسبوع|هذا\s*الاسبوع|هالأسبوع", RegexOptions.Compiled);
    private static readonly Regex Ar_ThisMonth = new(@"هذا\s*الشهر|هالشهر", RegexOptions.Compiled);
    private static readonly Regex Ar_ThisYear = new(@"هذا\s*العام|هذه\s*السنة|هالسنة", RegexOptions.Compiled);

    private static readonly TemporalPattern[] Patterns = new[]
    {
        // Relative recent N units: "last 24 hours", "in the last 30 days", "past 7 days"
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+hours?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@hours:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+days?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@days:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+weeks?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@weeks:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+months?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@months:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+years?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@years:-{0}"),

        // Fixed period boundaries — half-open range [period_start, next_period_start).
        new TemporalPattern(new Regex(@"\bthis\s+week\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@week_start", "@weeks:1"),
        new TemporalPattern(new Regex(@"\blast\s+week\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_week_start", "@week_start"),
        new TemporalPattern(new Regex(@"\bthis\s+month\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@month_start", "@months:1"),
        new TemporalPattern(new Regex(@"\blast\s+month\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_month_start", "@month_start"),
        new TemporalPattern(new Regex(@"\bthis\s+quarter\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@quarter_start", "@quarters:1"),
        new TemporalPattern(new Regex(@"\blast\s+quarter\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_quarter_start", "@quarter_start"),
        new TemporalPattern(new Regex(@"\bthis\s+year\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@year_start", "@years:1"),
        new TemporalPattern(new Regex(@"\blast\s+year\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_year_start", "@year_start"),

        // Single-day anchors.
        new TemporalPattern(new Regex(@"\btoday\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@today", "@tomorrow"),
        new TemporalPattern(new Regex(@"\byesterday\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@yesterday", "@today"),

        // Named quarter shortcuts — "Q1 of this year" / "in Q3 last year" / "first quarter".
        // The compiler's temporal-token vocabulary has q1_start..q4_end (DATEFROMPARTS-based,
        // anchored to YEAR(GETDATE())). Half-open interval [start, end).
        new TemporalPattern(new Regex(@"\b(?:q1|first\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q1_start", "@q1_end"),
        new TemporalPattern(new Regex(@"\b(?:q2|second\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q2_start", "@q2_end"),
        new TemporalPattern(new Regex(@"\b(?:q3|third\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q3_start", "@q3_end"),
        new TemporalPattern(new Regex(@"\b(?:q4|fourth\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q4_start", "@q4_end"),

        // Arabic — same anchors. Half-open ranges where applicable.
        new TemporalPattern(Ar_Today, "@today", "@tomorrow"),
        new TemporalPattern(Ar_Yesterday, "@yesterday", "@today"),
        new TemporalPattern(Ar_ThisWeek, "@week_start", "@weeks:1"),
        new TemporalPattern(Ar_ThisMonth, "@month_start", "@months:1"),
        new TemporalPattern(Ar_ThisYear, "@year_start", "@years:1"),
    };

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        // Phase 07 — hard short-circuit. When TimeIntent has populated the temporal slot
        // (single range or multi-period), this legacy phase is OBSOLETE for that question.
        // TimeIntent is the single source of truth — no competing injectors.
        if (spec.TimeIntent is { Kind: not SuperAdminCopilot.Models.TimeIntentKind.Unqualified }) return;

        var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        if (string.IsNullOrEmpty(dateCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, dateCol)) return;

        var qualifiedCol = $"{spec.Root}.{dateCol}";

        // Skip if a filter already exists on the resolved date column — don't double-up. We
        // explicitly allow filters on *different* date columns (a question can mention "this week"
        // but the LLM already filtered on ResolvedAt; we'd add a constraint on CreatedAt too).
        //
        // The comparison normalises both sides:
        //   • Planner often emits `IssuedAt` (unqualified) → StripQualifier is a no-op.
        //   • This phase or SpecificYearMonthFilterPhase emits `Bills.IssuedAt` (qualified).
        //   • An adjacent peer phase may even emit `dbo.Bills.IssuedAt` — StripQualifier
        //     takes the last dot-segment.
        // We ALSO trip the early-return if the filter's column ENDS with the bare dateCol —
        // catches casing variants the StripQualifier didn't normalise.
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            var bareF = StripQualifier(f.Column);
            if (string.Equals(bareF, dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
            if (f.Column.EndsWith(dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
        }
        // Also skip if PeriodComparisons already covers the date column — UNION ALL legs each
        // carry their own date filter, so re-injecting would double-up under each leg.
        if (spec.PeriodComparisons.Count > 0)
        {
            foreach (var p in spec.PeriodComparisons)
            {
                foreach (var f in p.Filters)
                {
                    if (string.IsNullOrEmpty(f.Column)) continue;
                    if (StripQualifier(f.Column).Equals(dateCol, System.StringComparison.OrdinalIgnoreCase)
                        || f.Column.EndsWith(dateCol, System.StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }
        }

        // Strip the "-- requested columns: ..." trailing hint the structural-cue parser injects.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Collect ALL matching patterns (not just the first) so multi-period questions like
        // "Q1 and Q2" / "this month vs last month" / "today and yesterday" produce one
        // filter-leg per matched period. Patterns are arranged most-specific-first; we
        // intentionally let multiple match — a "last 30 days" wouldn't collide with "Q1".
        // De-duplicate by token pair (start, end) so the same period mentioned twice in the
        // question text doesn't produce duplicate legs.
        var hits = new List<(string Label, string StartToken, string? EndToken, string Op)>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Year-qualified periods FIRST ("Q1 2027", "February 2019", "in 2025"). When matched,
        // emit @yearmonth:Y:M tokens (year-specific, no GETDATE() drift) and remove the
        // matched substring from `q` so the year-anchored Q1..Q4 / month patterns below don't
        // double-match. This is the fix for the "Q1 2027 vs Feb 2019" example where the old
        // year-anchored tokens silently used YEAR(GETDATE()) and collapsed both periods.
        q = ScanYearQualifiedPeriods(q, hits, seen);

        foreach (var p in Patterns)
        {
            var m = p.Match.Match(q);
            if (!m.Success) continue;

            string startToken = p.Token;
            string? endToken = p.RangeEnd;
            if (m.Groups.Count > 1 && m.Groups[1].Success)
                startToken = string.Format(System.Globalization.CultureInfo.InvariantCulture, startToken, m.Groups[1].Value);

            var key = startToken + "|" + (endToken ?? "");
            if (!seen.Add(key)) continue;
            hits.Add((m.Value.Trim(), startToken, endToken, p.Op));
        }

        if (hits.Count == 0) return;

        // Single hit → emit as a plain filter on the spec (existing behaviour). A range becomes
        // gte+lt; a single-bound stays op=gte (default Op on TemporalPattern).
        if (hits.Count == 1)
        {
            var (label, startToken, endToken, op) = hits[0];
            if (endToken is null)
            {
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = op, Value = startToken });
                ctx.Diagnostics.Add(new(Name, $"injected {qualifiedCol} {op} '{startToken}' (matched '{label}')"));
            }
            else
            {
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = startToken });
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt,  Value = endToken });
                ctx.Diagnostics.Add(new(Name, $"injected {qualifiedCol} in ['{startToken}', '{endToken}') (matched '{label}')"));
            }
            return;
        }

        // Multiple hits → use the spec's PeriodComparisons mechanism. The compiler emits one
        // SELECT leg per PeriodSpec, UNION ALL'd, with the Label as a literal projected column —
        // so the result identifies which period each row belongs to ("Q1" / "Q2" / "Last Month").
        // Base spec filters apply to every leg; each leg layers its date range on top.
        // Skip if PeriodComparisons is already populated (LLM or another phase set it up).
        if (spec.PeriodComparisons.Count > 0) return;

        foreach (var (label, startToken, endToken, op) in hits)
        {
            var leg = new PeriodSpec { Label = NormaliseLabel(label) };
            if (endToken is null)
            {
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = op, Value = startToken });
            }
            else
            {
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = startToken });
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt,  Value = endToken });
            }
            spec.PeriodComparisons.Add(leg);
        }
        ctx.Diagnostics.Add(new(Name, $"detected {hits.Count} temporal periods in question → injected as PeriodComparisons (UNION ALL legs): {string.Join(", ", hits.Select(h => h.Label))}"));
    }

    private static string NormaliseLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        // Trim, take first letter uppercase. "q1" → "Q1", "last month" → "Last month".
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }

    /// <summary>Strip a <c>Table.Column</c> qualifier so we can compare against a bare column
    /// name. <c>"Bills.IssuedAt"</c> → <c>"IssuedAt"</c>; <c>"IssuedAt"</c> → <c>"IssuedAt"</c>.</summary>
    private static string StripQualifier(string column)
    {
        if (string.IsNullOrEmpty(column)) return column;
        var dot = column.LastIndexOf('.');
        return dot >= 0 && dot < column.Length - 1 ? column[(dot + 1)..] : column;
    }

    // ── Year-qualified period scanner ─────────────────────────────────────────────
    //
    // Detects phrases that BIND a period to a specific year (rather than the current one):
    //   • Q1 2027  /  q3 of 2025  /  first quarter 2026  /  third quarter of 2024
    //   • January 2025  /  Feb 2019  /  March of 2026
    //   • in 2027  /  during 2025  /  throughout 2026  /  for 2024  (entire year)
    //
    // Emits `@yearmonth:YYYY:M` tokens for the half-open interval. Removes the matched
    // substring from the question so the year-anchored Q1..Q4 / month patterns below don't
    // also fire on the same text. Multi-period inputs ("Q1 2027 vs Feb 2019") produce
    // multiple hits, which downstream becomes a UNION-ALL PeriodComparison.

    private static readonly System.Text.RegularExpressions.Regex YearQuarterRx = new(
        @"\b(?:q([1-4])|(first|second|third|fourth)\s+quarter)\s+(?:of\s+|in\s+)?(\d{4})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex YearMonthRx = new(
        @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?:of\s+|in\s+)?(\d{4})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EntireYearRx = new(
        @"\b(?:in|during|throughout|for|year)\s+(\d{4})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Collections.Generic.Dictionary<string, int> WordQuarter = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4,
    };

    private static readonly System.Collections.Generic.Dictionary<string, int> MonthIndex = new(System.StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Scans for year-qualified periods, mutates <paramref name="hits"/> with one entry
    /// per detected period using <c>@yearmonth</c> tokens, and returns the question with
    /// matched substrings blanked out (to prevent the year-anchored patterns from re-matching).</summary>
    private static string ScanYearQualifiedPeriods(
        string q,
        System.Collections.Generic.List<(string Label, string StartToken, string? EndToken, string Op)> hits,
        System.Collections.Generic.HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var sb = new System.Text.StringBuilder(q);

        // Quarter+year — most specific first.
        foreach (System.Text.RegularExpressions.Match m in YearQuarterRx.Matches(q))
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
        foreach (System.Text.RegularExpressions.Match m in YearMonthRx.Matches(q))
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

        // Bare year — most general, run last. "in 2027" / "for year 2024".
        foreach (System.Text.RegularExpressions.Match m in EntireYearRx.Matches(q))
        {
            if (!int.TryParse(m.Groups[1].Value, out var year) || year < 1900 || year > 2200) continue;
            var startTok = $"@yearmonth:{year}:1";
            var endTok = $"@yearmonth:{year + 1}:1";
            var key = startTok + "|" + endTok;
            if (!seen.Add(key)) continue;
            hits.Add(($"Year {year}", startTok, endTok, SpecConst.FilterOps.Gte));
            BlankRange(sb, m.Index, m.Length);
        }

        return sb.ToString();
    }

    /// <summary>Replace [start, start+length) with spaces so downstream regex patterns
    /// don't re-match. Keeping the string length identical preserves any index-based
    /// reasoning the caller might do later.</summary>
    private static void BlankRange(System.Text.StringBuilder sb, int start, int length)
    {
        for (int i = 0; i < length && start + i < sb.Length; i++) sb[start + i] = ' ';
    }
}
