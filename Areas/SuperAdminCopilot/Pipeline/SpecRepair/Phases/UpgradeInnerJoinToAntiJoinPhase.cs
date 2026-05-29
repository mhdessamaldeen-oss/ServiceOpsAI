namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>Question matches anti-intent ("X with no Y") + spec has INNER JOIN → flip kind to "anti".</summary>
internal sealed class UpgradeInnerJoinToAntiJoinPhase : ISpecRepairPhase
{
    public string Name => "UpgradeInnerJoinToAntiJoin";
    public string Covers => "LLM emits INNER JOIN for anti-join phrasings";

    // Tier window override: weak-model crutch.
    // Heuristic to flip INNER JOIN to anti-join. Strong NLU emits the anti-join directly.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Joins.Count == 0) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var patterns = ctx.Options.AntiJoinIntentPatterns;
        if (patterns is null || patterns.Count == 0) return;

        var qLower = ctx.Question.ToLowerInvariant();
        var matched = false;
        foreach (var pat in patterns)
        {
            if (string.IsNullOrWhiteSpace(pat)) continue;
            try { if (Regex.IsMatch(qLower, pat, RegexOptions.IgnoreCase)) { matched = true; break; } }
            catch { /* skip malformed config regex */ }
        }
        if (!matched) return;

        int upgraded = 0;
        foreach (var j in spec.Joins.NotNull())
        {
            if (string.IsNullOrEmpty(j.Table)) continue;
            if (string.Equals(j.Kind, "anti", System.StringComparison.OrdinalIgnoreCase)) continue;
            j.Kind = "anti";
            upgraded++;
        }
        if (upgraded > 0)
            ctx.Diagnostics.Add(new(Name, $"upgraded {upgraded} join(s) to anti"));
    }
}
