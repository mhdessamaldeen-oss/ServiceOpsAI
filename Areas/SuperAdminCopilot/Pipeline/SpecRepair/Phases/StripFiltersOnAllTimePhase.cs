namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question contains an "all time" cue — "ever", "of all time", "in history", "all
/// time", "since the beginning", or Arabic equivalents — strip filters on the root entity's
/// default date column AND remove status filters that would arbitrarily narrow the result.
///
/// <para>Catches the silent-failure where the LLM adds default filters that contradict the
/// user's "any/all" intent. Example: "largest single bill amount ever issued" → spec emits
/// <c>WHERE Status='Issued'</c> (over-narrows; the user said "ever"); strip the status filter.</para>
///
/// <para>Conservative scope: only strips (a) date filters on the entity's default date column
/// and (b) status filters on a column whose name contains "status". Other filters (region,
/// customer, etc.) remain — those are usually deliberate scoping the user wrote.</para>
/// </summary>
internal sealed class StripFiltersOnAllTimePhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public StripFiltersOnAllTimePhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "StripFiltersOnAllTime";
    public string Covers => "'ever' / 'all time' / 'in history' → strip date filter on root date column + status narrowing";

    // Tier window override: weak-model crutch.
    // All-time cues come from linguistic-cues.json `allTime` block per locale.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        if (!QuestionHasAllTimeCue(q, _cues)) return;

        var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        var dateColQualified = string.IsNullOrEmpty(dateCol) ? null : $"{spec.Root}.{dateCol}";

        var removed = spec.Filters.RemoveAll(f =>
        {
            if (string.IsNullOrEmpty(f.Column)) return false;
            // Strip filters on the root date column.
            if (dateColQualified is not null
                && string.Equals(f.Column, dateColQualified, System.StringComparison.OrdinalIgnoreCase))
                return true;
            // Strip status-narrowing filters (any column whose name contains "status").
            var (_, col) = SplitQualified(f.Column);
            return !string.IsNullOrEmpty(col)
                && col!.Contains("status", System.StringComparison.OrdinalIgnoreCase);
        });
        if (removed > 0)
            ctx.Diagnostics.Add(new(Name, $"stripped {removed} filter(s) — 'all-time' cue in question (date column and/or status narrowing removed)"));
    }

    private static bool QuestionHasAllTimeCue(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return false;
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale?.AllTimeRegex is null) continue;
            if (locale.AllTimeRegex.IsMatch(question)) return true;
        }
        return false;
    }

    private static (string table, string column) SplitQualified(string qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx <= 0) return ("", qualified);
        return (qualified.Substring(0, idx).Trim('[', ']'),
                qualified.Substring(idx + 1).Trim('[', ']'));
    }
}
