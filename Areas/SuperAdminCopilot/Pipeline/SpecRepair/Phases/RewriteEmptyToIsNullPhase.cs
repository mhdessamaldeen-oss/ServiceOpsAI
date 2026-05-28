namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// When the question says "no X" / "without X" / "missing X" / "has no X" / "lacks X" and the LLM
/// emitted a filter of shape <c>column = '' </c> (or <c>column = null-string</c>) on a nullable
/// column, rewrite the filter to <c>column IS NULL</c>. Mirrors the same fix for absence-of-value
/// questions in Arabic ("بدون", "ما عنده").
///
/// <para>Why: real-world database "missing" values are NULL, not empty string. The LLM
/// frequently emits <c>WHERE Email = @p0</c> with @p0='' for "customers with no email" — that
/// matches zero rows when the column is NULL for missing emails. The semantic intent is
/// IS NULL.</para>
///
/// <para>Also detects the inverse case where NO filter was emitted at all but the question
/// asks about absence — injects <c>WHERE column IS NULL</c> on the most likely candidate
/// column. The candidate is matched from the noun after "no" / "without" against root entity's
/// nullable columns. Conservative: only fires when exactly one nullable column matches.</para>
/// </summary>
internal sealed class RewriteEmptyToIsNullPhase : ISpecRepairPhase
{
    public string Name => "RewriteEmptyToIsNull";
    public string Covers => "'no X' / 'without X' / 'missing X' → rewrite column='' to column IS NULL (or inject the IS NULL filter when missing)";

    private static readonly Regex AbsenceCueEn = new(
        @"\b(?:no|without|missing|lacks?|lacking|has\s+no|have\s+no|do\s+not\s+have|don'?t\s+have)\s+([a-z][a-z_\s]{2,30})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AbsenceCueAr = new(
        @"بدون|ما\s+عنده|بلا\s+|من\s+غير",
        RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip the "-- requested columns: ..." trailing hint.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        var enMatches = AbsenceCueEn.Matches(q);
        var hasAbsenceCue = enMatches.Count > 0 || AbsenceCueAr.IsMatch(q);
        if (!hasAbsenceCue) return;

        // Pass 1 — rewrite any existing filter that's `column = ''` (or empty/null literal) to IS NULL,
        // when the column is nullable. Independent of the noun matching below: even if we can't
        // find the column from the question, an empty-string filter on a nullable column is almost
        // always a mistake.
        var rewriteCount = 0;
        foreach (var f in spec.Filters)
        {
            if (f is null) continue;
            var op = (f.Op ?? "eq").ToLowerInvariant();
            if (op != "eq") continue;
            if (!IsEmptyOrNullValue(f.Value)) continue;
            if (!IsNullableColumn(f.Column, ctx)) continue;
            f.Op = SpecConst.FilterOps.IsNullAlt;
            f.Value = null;
            rewriteCount++;
        }
        if (rewriteCount > 0)
            ctx.Diagnostics.Add(new(Name, $"rewrote {rewriteCount} '= empty' filter(s) to IS NULL (absence cue in question)"));

        // Pass 2 — for each absence noun captured from the question (English only — Arabic
        // capture is harder; rely on Pass 1 for AR), look up a nullable column on spec.Root
        // whose name "looks like" the noun. Inject IS NULL when no filter on that column exists.
        if (enMatches.Count == 0) return;
        var existingCols = new HashSet<string>(
            spec.Filters.Select(f => (f.Column ?? "").ToLowerInvariant()),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (Match m in enMatches)
        {
            var noun = m.Groups[1].Value.Trim().Trim('s');         // crude singularisation
            if (noun.Length < 3) continue;
            var nounNormalised = noun.Replace(" ", "").ToLowerInvariant();
            // Candidate columns on root that match the noun (case-insensitive contains).
            var cols = ctx.Catalog.GetColumns(spec.Root);
            var candidates = cols.Where(c =>
                c.IsNullable
                && c.ColumnName.ToLowerInvariant().Contains(nounNormalised, System.StringComparison.Ordinal)).ToList();

            // Be conservative: only act if exactly one nullable column matches. Multiple matches
            // = ambiguous; better to do nothing than guess.
            if (candidates.Count != 1) continue;
            var col = candidates[0];
            var qualified = $"{spec.Root}.{col.ColumnName}";
            if (existingCols.Contains(qualified.ToLowerInvariant())) continue;
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.IsNullAlt, Value = null });
            existingCols.Add(qualified.ToLowerInvariant());
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} IS NULL (absence cue '{m.Value.Trim()}')"));
        }
    }

    private static bool IsEmptyOrNullValue(object? v)
    {
        if (v is null) return true;
        if (v is string s) return s.Length == 0 || string.Equals(s, "null", System.StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool IsNullableColumn(string? column, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(column)) return false;
        var dotIdx = column.IndexOf('.');
        if (dotIdx <= 0) return false;
        var tbl = column.Substring(0, dotIdx).Trim('[', ']');
        var col = column.Substring(dotIdx + 1).Trim('[', ']');
        if (!ctx.Catalog.TableExists(tbl)) return false;
        var match = ctx.Catalog.GetColumns(tbl).FirstOrDefault(c =>
            string.Equals(c.ColumnName, col, System.StringComparison.OrdinalIgnoreCase));
        return match?.IsNullable == true;
    }
}
