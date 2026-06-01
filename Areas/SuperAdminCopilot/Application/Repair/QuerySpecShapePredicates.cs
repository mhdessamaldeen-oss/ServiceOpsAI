namespace SuperAdminCopilot.Application.Repair;

using System.Linq;
using SuperAdminCopilot.Models;

/// <summary>
/// Single source of truth for QuerySpec SHAPE predicates that the pipeline needs to check
/// from two layers: the <c>SqlIntentGuard</c> (post-compile) and SpecRepair rules (pre-compile).
/// Both call the same predicate on the same canonical mutable <see cref="QuerySpec"/> — no
/// drift possible.
///
/// <para>The previous v2 + v3 dual-overload pattern was eliminated by the 2026-06-01 single-spec
/// collapse. There is now ONE QuerySpec, so ONE predicate per shape.</para>
/// </summary>
internal static class QuerySpecShapePredicates
{
    /// <summary>
    /// True when the spec is a "pure COUNT" shape — no SELECT cols, no GROUP BY, every
    /// aggregation is a COUNT. Used to detect the LLM mistake where a LIST question is
    /// answered with a single-row count (e.g., "complaints in Damascus" → SELECT COUNT(*)).
    /// </summary>
    public static bool IsPureCountShape(QuerySpec spec)
    {
        if (spec is null) return false;
        if (spec.GroupBy.Count > 0) return false;
        if (spec.Select.Count > 0) return false;
        if (spec.Aggregations.Count == 0) return false;
        return spec.Aggregations.All(a =>
            string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the spec has distinct=true alongside any real aggregation function. DISTINCT
    /// on grouped/aggregated results is always degenerate; the contradiction is repaired by
    /// dropping distinct (aggregations win).
    /// </summary>
    public static bool IsDistinctWithAggregations(QuerySpec spec)
    {
        if (spec is null || !spec.Distinct) return false;
        return spec.Aggregations.Any(a => !string.IsNullOrEmpty(a.Function));
    }
}
