namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Aggregation;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// For COMPARE-shape questions ("X vs Y" / "compared to" / "open vs closed" / "Damascus vs Aleppo"),
/// the canonical SQL pattern is a single SELECT with multiple <c>SUM(CASE WHEN &lt;cond&gt; THEN 1
/// ELSE 0 END)</c> columns — one per comparison leg — over the SAME row set. The LLM frequently
/// emits two SEPARATE SELECT statements instead (multi-statement output, breaks the comparison).
///
/// <para>This phase detects: (a) <see cref="QuestionGroundingContext"/> classified the shape as
/// COMPARE OR the question matches a comparison cue regex, AND (b) the spec has no
/// PeriodComparisons (those are the time-comparison shape), AND (c) the LLM emitted multiple
/// aggregates that look like positional comparisons. The remedy is to leave the spec alone but
/// emit a diagnostic — the QuerySpec compiler already handles SUM(CASE WHEN) once the LLM
/// emits the right shape. The primary value of this phase is preventing the wrong shape; the
/// grounding context now does the heavy lifting by telling the LLM the COMPARE pattern up-front.</para>
///
/// <para>What this phase actively fixes: when the LLM produced a single MAX/MIN aggregation
/// but the question is clearly a comparison ("Damascus vs Aleppo"), we DROP the aggregation
/// and let the LLM-prompt-grounding inject SUM(CASE WHEN) bindings on the retry. For now, the
/// minimum safe action is to log + flag for diagnostics so we see real-world coverage.</para>
/// </summary>
internal sealed class CaseWhenComparisonPhase : ISpecRepairPhase
{
    public string Name => "CaseWhenComparison";
    public string Covers => "Comparison-shape question with wrong aggregate shape → flag (LLM grounding handles the rewrite up-front)";

    private static readonly Regex CompareCue = new(
        @"\b(?:vs\.?|versus|compared\s+to|compare(?:d)?|comparison\s+of|side\s+by\s+side|against)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        if (!CompareCue.IsMatch(q)) return;
        if (spec.PeriodComparisons.Count > 0) return;          // time-comparison path handles it

        // If the LLM emitted exactly one COUNT(*) aggregation and the question is COMPARE, the
        // result will be a single number instead of side-by-side. Add a diagnostic so we can see
        // how often this happens; the principled fix is the grounded prompt asking for
        // SUM(CASE WHEN) explicitly (Stage-1 grounding).
        if (spec.Aggregations.Count == 1
            && string.Equals(spec.Aggregations[0].Function, "COUNT", System.StringComparison.OrdinalIgnoreCase))
        {
            ctx.Diagnostics.Add(new(Name, "COMPARE shape detected but only one COUNT aggregation — expected SUM(CASE WHEN) per leg. Grounded prompt should emit two; if it didn't, the question phrasing may not have matched a value-link pair."));
        }
    }
}
