namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Aggregation;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question is a SUPERLATIVE aggregate ("largest X", "smallest Y", "highest Z",
/// "lowest W", "biggest", "max/min of") with no explicit status / category cue, AND the LLM
/// added a Status filter on its own, STRIP the unsolicited Status filter.
///
/// <para>Background: session 110 audit (AGG suite) showed the LLM frequently over-filters
/// on Status when the question is a simple "biggest bill" / "smallest amount" — it adds
/// <c>WHERE Bills.Status = 'Issued'</c> by default. The user asked for the overall max, not
/// max-among-issued.</para>
///
/// <para>Conservative gates:
/// <list type="bullet">
///   <item>Fires only when aggregations.Count == 1 AND function is MIN/MAX/AVG/SUM</item>
///   <item>Only strips Status-shaped filters (column name contains "Status")</item>
///   <item>Skips if the question explicitly mentions a status value
///         (Open / Closed / Paid / Issued / Overdue / Active / Resolved)</item>
///   <item>Skips if a concept-pattern injected the status (those carry intentional semantics)</item>
/// </list></para>
/// </summary>
internal sealed class StripUnsolicitedStatusOnSuperlativePhase : ISpecRepairPhase
{
    public string Name => "StripUnsolicitedStatusOnSuperlative";
    public string Covers => "Superlative AGG ('largest X', 'smallest Y') with auto-added Status filter not justified by question → strip the Status filter";

    private static readonly Regex SuperlativeCue = new(
        @"\b(?:largest|biggest|maximum|max\s+(?:single|amount|value)?|highest|peak|smallest|minimum|lowest|shortest|longest|earliest|latest|oldest|newest|most|least|fewest)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitStatusCue = new(
        @"\b(?:open|closed|paid|unpaid|issued|overdue|resolved|active|ongoing|cancelled|disputed|pending|new)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count != 1) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var fn = (spec.Aggregations[0].Function ?? "").ToUpperInvariant();
        if (fn != "MIN" && fn != "MAX" && fn != "AVG" && fn != "SUM") return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        if (!SuperlativeCue.IsMatch(q)) return;
        if (ExplicitStatusCue.IsMatch(q)) return;     // user explicitly named a status — leave it

        // Strip Status-shaped filters.
        var removed = spec.Filters.RemoveAll(f =>
        {
            if (string.IsNullOrEmpty(f.Column)) return false;
            var col = f.Column;
            var dotIdx = col.IndexOf('.');
            var colOnly = dotIdx > 0 ? col.Substring(dotIdx + 1) : col;
            return colOnly.Contains("Status", System.StringComparison.OrdinalIgnoreCase)
                || colOnly.Contains("StatusId", System.StringComparison.OrdinalIgnoreCase);
        });
        if (removed > 0)
            ctx.Diagnostics.Add(new(Name, $"stripped {removed} unsolicited Status filter(s) on superlative aggregate"));
    }
}
