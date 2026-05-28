namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Filters;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Detect specific-year and specific-month patterns in the question text and inject a half-open
/// date range filter on the root entity's default date column. Patterns:
/// <list type="bullet">
///   <item><c>in 2025</c> / <c>in year 2025</c> / <c>during 2025</c> / <c>for 2025</c> →
///         <c>[2025-01-01, 2026-01-01)</c></item>
///   <item><c>in March</c> / <c>in March 2025</c> → month range of current year (or specified year)</item>
///   <item><c>in March 2025</c> with explicit year → that specific month range</item>
///   <item><c>since 2024</c> / <c>after 2024</c> → <c>&gt;= 2024-01-01</c></item>
///   <item><c>before 2025</c> / <c>prior to 2025</c> → <c>&lt; 2025-01-01</c></item>
/// </list>
///
/// <para>Skips when the spec already has a date filter on the resolved date column — defers to
/// whatever upstream (grounder / temporal / concept-patterns) already produced. This is
/// strictly an additive backstop for patterns the existing temporal phase doesn't recognise.</para>
/// </summary>
internal sealed class SpecificYearMonthFilterPhase : ISpecRepairPhase
{
    public string Name => "SpecificYearMonthFilter";
    public string Covers => "Specific year ('in 2025') / month ('in March 2025') / since/before year → range filter on root date column";

    private static readonly Regex MonthYearPattern = new(
        @"\bin\s+(january|february|march|april|may|june|july|august|september|october|november|december)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonthOnlyPattern = new(
        @"\bin\s+(january|february|march|april|may|june|july|august|september|october|november|december)\b(?!\s+\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearOnlyPattern = new(
        @"\b(?:in\s+(?:the\s+)?(?:year\s+)?|during|for|throughout)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SinceYearPattern = new(
        @"\b(?:since|after|from)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BeforeYearPattern = new(
        @"\b(?:before|prior\s+to|until)\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Collections.Generic.Dictionary<string, int> MonthNumber = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["february"] = 2, ["march"] = 3, ["april"] = 4,
        ["may"] = 5, ["june"] = 6, ["july"] = 7, ["august"] = 8,
        ["september"] = 9, ["october"] = 10, ["november"] = 11, ["december"] = 12,
    };

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        // Phase 07 — hard short-circuit when TimeIntent owns the temporal slot.
        if (spec.TimeIntent is { Kind: not SuperAdminCopilot.Models.TimeIntentKind.Unqualified }) return;

        var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        if (string.IsNullOrEmpty(dateCol) || !ctx.Catalog.ColumnExists(spec.Root, dateCol!)) return;
        var qualified = $"{spec.Root}.{dateCol}";

        // Skip if a date filter already exists.
        if (spec.Filters.Any(f => string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase)))
            return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Try in order: month+year > month-only > year-only > since > before.
        var monthYearMatch = MonthYearPattern.Match(q);
        if (monthYearMatch.Success
            && MonthNumber.TryGetValue(monthYearMatch.Groups[1].Value, out var m1)
            && int.TryParse(monthYearMatch.Groups[2].Value, out var y1))
        {
            EmitRange(spec, qualified, new System.DateTime(y1, m1, 1), new System.DateTime(y1, m1, 1).AddMonths(1));
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} in [{y1}-{m1:D2}-01, {y1}-{m1:D2}-01 + 1 month)"));
            return;
        }

        var monthOnlyMatch = MonthOnlyPattern.Match(q);
        if (monthOnlyMatch.Success
            && MonthNumber.TryGetValue(monthOnlyMatch.Groups[1].Value, out var m2))
        {
            // Anchor to current year.
            var y = System.DateTime.Now.Year;
            EmitRange(spec, qualified, new System.DateTime(y, m2, 1), new System.DateTime(y, m2, 1).AddMonths(1));
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} in [{y}-{m2:D2}-01, +1 month) (month-only, anchored to current year)"));
            return;
        }

        var yearOnlyMatch = YearOnlyPattern.Match(q);
        if (yearOnlyMatch.Success && int.TryParse(yearOnlyMatch.Groups[1].Value, out var y2)
            && y2 >= 1900 && y2 <= 2100)
        {
            EmitRange(spec, qualified, new System.DateTime(y2, 1, 1), new System.DateTime(y2 + 1, 1, 1));
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} in [{y2}-01-01, {y2 + 1}-01-01)"));
            return;
        }

        var sinceMatch = SinceYearPattern.Match(q);
        if (sinceMatch.Success && int.TryParse(sinceMatch.Groups[1].Value, out var y3)
            && y3 >= 1900 && y3 <= 2100)
        {
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.Gte, Value = new System.DateTime(y3, 1, 1) });
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} >= {y3}-01-01"));
            return;
        }

        var beforeMatch = BeforeYearPattern.Match(q);
        if (beforeMatch.Success && int.TryParse(beforeMatch.Groups[1].Value, out var y4)
            && y4 >= 1900 && y4 <= 2100)
        {
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.Lt, Value = new System.DateTime(y4, 1, 1) });
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} < {y4}-01-01"));
            return;
        }
    }

    private static void EmitRange(QuerySpec spec, string qualifiedCol, System.DateTime start, System.DateTime endExclusive)
    {
        spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = start });
        spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt,  Value = endExclusive });
    }
}
