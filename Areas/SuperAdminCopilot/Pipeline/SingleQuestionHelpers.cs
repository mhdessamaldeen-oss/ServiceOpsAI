namespace SuperAdminCopilot.Pipeline;

using System;
using System.Linq;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Pure helpers extracted from <c>CopilotOrchestrator.RunSingle.cs</c> as part of the 2026-06-01
/// de-couple pass. These are stateless functions over <see cref="QuerySpec"/> — they belong in
/// a static helper rather than spreading through a 487-LOC partial of the orchestrator.
///
/// <para>The wider <c>SingleQuestionExecutor</c> extraction (moving <c>RunSingleAsync</c> + its
/// instance helpers into a dedicated class) is genuinely larger work that touches the 54-dep DI
/// graph — deferred for a focused session. This file is the no-risk slice of that refactor.</para>
/// </summary>
internal static class SingleQuestionHelpers
{
    /// <summary>
    /// Returns true when an empty result is suspicious enough to warrant a coverage check:
    /// top-N queries, aggregate-without-group, or any filtered list that isn't an anti-join.
    /// Anti-joins are EXPECTED to be empty when no orphans exist — not suspicious.
    /// </summary>
    public static bool IsSuspiciousEmptyResult(QuerySpec spec)
    {
        if (spec.Limit is > 0) return true;
        if (spec.Aggregations.Count > 0 && spec.GroupBy.Count == 0) return true;
        var hasFilters = spec.Filters.Count > 0;
        var hasAntiJoin = spec.Joins.Any(j =>
            string.Equals(j.Kind, SpecConst.JoinKinds.Anti, StringComparison.OrdinalIgnoreCase));
        if (hasFilters && !hasAntiJoin) return true;
        return false;
    }

    /// <summary>Short shape label used in user-facing error / status messages.</summary>
    public static string DescribeShape(QuerySpec spec)
    {
        if (spec.Limit is > 0) return $"top {spec.Limit}";
        if (spec.Aggregations.Count > 0) return "aggregation";
        if (spec.Filters.Count > 0) return "filtered list";
        return "list";
    }

    /// <summary>
    /// Single-root, no-join spec with filter / aggregation counts within the configured thresholds.
    /// The CoverageChecker's two failure modes (compound half-answers, wrong-FK joins) can't
    /// manifest in this shape, so skipping it saves an LLM call without quality cost. Thresholds
    /// live on <see cref="Configuration.CopilotOptions.TrivialAnswerMaxFilters"/> /
    /// <c>TrivialAnswerMaxAggregations</c> so operators can tune without recompiling.
    /// </summary>
    public static bool IsTrivialAnswer(QuerySpec spec, int maxFilters, int maxAggregations)
    {
        if (spec.Joins.Count > 0) return false;
        if (spec.GroupBy.Count > 0) return false;
        if (spec.Filters.Count > maxFilters) return false;
        if (spec.Aggregations.Count > maxAggregations) return false;
        return true;
    }
}
