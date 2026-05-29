namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Aggregation;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
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
    private readonly ILinguisticCuesProvider _cues;

    public StripUnsolicitedStatusOnSuperlativePhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "StripUnsolicitedStatusOnSuperlative";
    public string Covers => "Superlative AGG ('largest X', 'smallest Y') with auto-added Status filter not justified by question → strip the Status filter";

    // Tier window override: weak-model crutch.
    // Superlative + status detection now ENTIRELY from linguistic-cues.json — NO hardcoded
    // vocab in this file. Strong NLU doesn't auto-add the filter, hence tier window.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Medium;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count != 1) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var fn = (spec.Aggregations[0].Function ?? "").ToUpperInvariant();
        if (fn != "MIN" && fn != "MAX" && fn != "AVG" && fn != "SUM") return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        if (!HasSuperlativeCue(q, _cues)) return;
        if (HasExplicitStatusCue(q, _cues)) return;     // user explicitly named a status — leave it

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

    /// <summary>
    /// True when ANY locale's compiled superlative/recency regex matches the question. Driven
    /// by linguistic-cues.json `superlative.{top,bottom,max,min}` + `recency.{desc,asc}`.
    /// </summary>
    private static bool HasSuperlativeCue(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return false;
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale is null) continue;
            if (locale.SuperlativeTopRegex?.IsMatch(question) == true) return true;
            if (locale.SuperlativeBottomRegex?.IsMatch(question) == true) return true;
            if (locale.SuperlativeMaxRegex?.IsMatch(question) == true) return true;
            if (locale.SuperlativeMinRegex?.IsMatch(question) == true) return true;
            if (locale.RecencyDescRegex?.IsMatch(question) == true) return true;
            if (locale.RecencyAscRegex?.IsMatch(question) == true) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the question contains any status / severity cue from
    /// linguistic-cues.json `statusValues[].cue` across any locale. Operator adds new dialect
    /// status words via JSON only.
    /// </summary>
    private static bool HasExplicitStatusCue(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return false;
        var qLower = question.ToLowerInvariant();
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale?.StatusValues is null) continue;
            foreach (var sv in locale.StatusValues)
            {
                if (string.IsNullOrEmpty(sv?.Cue)) continue;
                if (qLower.Contains(sv.Cue.ToLowerInvariant())) return true;
            }
        }
        return false;
    }
}
