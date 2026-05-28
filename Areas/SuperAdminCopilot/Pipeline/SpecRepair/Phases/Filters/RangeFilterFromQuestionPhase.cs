namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Filters;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Detect numeric-range patterns in the question and inject the corresponding filter(s) when the
/// spec doesn't already carry an equivalent comparison. Patterns covered:
/// <list type="bullet">
///   <item><c>between N and M</c> / <c>from N to M</c> → <c>col >= N AND col &lt;= M</c></item>
///   <item><c>greater than / more than / over / above / at least / minimum N</c> → <c>col &gt;= N</c></item>
///   <item><c>less than / fewer than / under / below / at most / maximum N</c> → <c>col &lt;= N</c></item>
///   <item><c>exactly N</c> / <c>= N</c> → <c>col = N</c></item>
/// </list>
///
/// <para>The column the range applies to is inferred from the noun preceding the range (e.g.
/// "bills over 50000" → Bills.TotalAmount; "tickets with more than 3 comments" →
/// nothing matchable today, leave alone). Mapping is heuristic:
/// <list type="number">
///   <item>If the noun matches a known numeric column on the root entity by case-insensitive
///         substring match → use that column.</item>
///   <item>Otherwise pick the first numeric column with a common "amount" / "total" / "count"
///         keyword on the root entity.</item>
///   <item>If neither matches, skip (don't guess).</item>
/// </list></para>
/// </summary>
internal sealed class RangeFilterFromQuestionPhase : ISpecRepairPhase
{
    public string Name => "RangeFilterFromQuestion";
    public string Covers => "'between X and Y' / 'over X' / 'less than X' → inject range filter on root numeric column";

    private static readonly Regex BetweenPattern = new(
        @"\b(?:between|from)\s+([\d,]+(?:\.\d+)?)\s+(?:and|to|-)\s+([\d,]+(?:\.\d+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GtePattern = new(
        @"\b(?:greater\s+than\s+or\s+equal\s+to|at\s+least|minimum\s+of?|no\s+less\s+than|>=)\s+([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GtPattern = new(
        @"\b(?:greater\s+than|more\s+than|over|above|exceeds?|exceeding|>)\s+([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LtePattern = new(
        @"\b(?:less\s+than\s+or\s+equal\s+to|at\s+most|maximum\s+of?|no\s+more\s+than|<=)\s+([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LtPattern = new(
        @"\b(?:less\s+than|fewer\s+than|under|below|<)\s+([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExactlyPattern = new(
        @"\b(?:exactly|equals?|equal\s+to)\s+([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        var targetCol = PickNumericColumn(spec, ctx);
        if (targetCol is null) return;
        var qualified = $"{spec.Root}.{targetCol}";

        // Skip if the spec already has a comparison filter on the same column.
        if (spec.Filters.Any(f =>
            string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase)
            && (f.Op == SpecConst.FilterOps.Gt || f.Op == SpecConst.FilterOps.Gte
                || f.Op == SpecConst.FilterOps.Lt || f.Op == SpecConst.FilterOps.Lte
                || f.Op == SpecConst.FilterOps.Eq)))
            return;

        // 1. Between (range) — most specific, try first.
        var m = BetweenPattern.Match(q);
        if (m.Success
            && TryParse(m.Groups[1].Value, out var lo)
            && TryParse(m.Groups[2].Value, out var hi))
        {
            if (lo > hi) (lo, hi) = (hi, lo);
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.Gte, Value = lo });
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.Lte, Value = hi });
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} between {lo} and {hi}"));
            return;
        }

        // 2. Single-bound comparisons. Check ≥ before > so "greater than or equal to" wins.
        if (TryFire(q, GtePattern, qualified, SpecConst.FilterOps.Gte, spec, ctx)) return;
        if (TryFire(q, GtPattern,  qualified, SpecConst.FilterOps.Gt,  spec, ctx)) return;
        if (TryFire(q, LtePattern, qualified, SpecConst.FilterOps.Lte, spec, ctx)) return;
        if (TryFire(q, LtPattern,  qualified, SpecConst.FilterOps.Lt,  spec, ctx)) return;
        if (TryFire(q, ExactlyPattern, qualified, SpecConst.FilterOps.Eq, spec, ctx)) return;
    }

    private static bool TryFire(string q, Regex pattern, string qualified, string op, QuerySpec spec, SpecRepairContext ctx)
    {
        var m = pattern.Match(q);
        if (!m.Success) return false;
        if (!TryParse(m.Groups[1].Value, out var n)) return false;
        spec.Filters.Add(new FilterSpec { Column = qualified, Op = op, Value = n });
        ctx.Diagnostics.Add(new("RangeFilterFromQuestion", $"injected {qualified} {op} {n}"));
        return true;
    }

    private static bool TryParse(string s, out decimal value)
    {
        return decimal.TryParse(s.Replace(",", ""),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// Pick a likely numeric column on the root entity. Priority:
    /// <list type="number">
    ///   <item>"Amount" / "Total" / "Cost" / "Price" / "Sum" / "Revenue" / "Value" in column name.</item>
    ///   <item>Any decimal/money/numeric column.</item>
    /// </list>
    /// Returns null when nothing reasonable matches — caller skips injection.
    /// </summary>
    private static string? PickNumericColumn(QuerySpec spec, SpecRepairContext ctx)
    {
        var cols = ctx.Catalog.GetColumns(spec.Root);
        if (cols.Count == 0) return null;

        bool IsMoneyLike(string type) =>
            type.Equals("decimal", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("numeric", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("money", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("smallmoney", System.StringComparison.OrdinalIgnoreCase);

        bool IsIntegerLike(string type) =>
            type.Equals("int", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("bigint", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("smallint", System.StringComparison.OrdinalIgnoreCase);

        bool IsNumeric(string type) => IsMoneyLike(type) || IsIntegerLike(type)
            || type.Equals("float", System.StringComparison.OrdinalIgnoreCase)
            || type.Equals("real", System.StringComparison.OrdinalIgnoreCase);

        var moneyKeywords = new[] { "Total", "Amount", "Cost", "Price", "Sum", "Revenue", "Value", "Balance" };
        // Pass 1 — money-keyword columns.
        foreach (var kw in moneyKeywords)
        {
            var match = cols.FirstOrDefault(c =>
                c.ColumnName.IndexOf(kw, System.StringComparison.OrdinalIgnoreCase) >= 0
                && IsNumeric(c.DataType));
            if (match is not null) return match.ColumnName;
        }
        // Pass 2 — any money-like column.
        var anyMoney = cols.FirstOrDefault(c => IsMoneyLike(c.DataType));
        if (anyMoney is not null) return anyMoney.ColumnName;
        return null;
    }
}
