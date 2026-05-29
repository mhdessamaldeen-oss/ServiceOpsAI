namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question contains a distinctness cue ("how many distinct X", "number of unique X",
/// "count of different X", "distinct/unique count of X") and the spec doesn't already emit
/// <c>COUNT(DISTINCT col)</c>, rewrite the aggregation to do so. Otherwise the LLM commonly
/// produces a bare <c>COUNT(*)</c> (which over-counts) or a GROUP BY (which returns per-group
/// counts instead of a single number).
///
/// <para>Selecting the column to apply DISTINCT to: prefer (in order) (a) an aggregation column
/// the LLM already named that isn't "*", (b) the entity's natural-key column from semantic-layer,
/// (c) the entity's primary key. Skip if no clear column can be picked — over-firing COUNT(DISTINCT *)
/// would degrade behaviour.</para>
/// </summary>
internal sealed class ForceCountDistinctOnDistinctQuestionPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public ForceCountDistinctOnDistinctQuestionPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "ForceCountDistinctOnDistinctQuestion";
    public string Covers => "'how many distinct/unique X' → ensure COUNT(DISTINCT col), not COUNT(*) / GROUP BY";

    // Tier window override: weak-model crutch.
    // Distinct/unique cues come from linguistic-cues.json `distinct` block per locale.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        if (!QuestionHasDistinctCue(q, _cues)) return;

        // Already correct? Bail.
        if (spec.Aggregations.Any(a => a.Distinct
                                    && string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase)))
            return;

        // Choose the target column.
        string? targetCol = null;
        // Pick existing COUNT aggregation if the LLM put a non-star column there.
        var existingCount = spec.Aggregations.FirstOrDefault(a =>
            string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase));
        if (existingCount is not null
            && !string.IsNullOrEmpty(existingCount.Column)
            && existingCount.Column != "*"
            && ctx.Catalog.ColumnExists(StripTable(existingCount.Column), StripColumn(existingCount.Column)))
        {
            targetCol = existingCount.Column;
        }

        // Fall back to the root entity's natural key.
        if (targetCol is null)
        {
            var entity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
            if (entity is not null && !string.IsNullOrEmpty(entity.NaturalKeyColumn)
                && ctx.Catalog.ColumnExists(spec.Root, entity.NaturalKeyColumn!))
            {
                targetCol = $"{spec.Root}.{entity.NaturalKeyColumn}";
            }
        }

        // Fall back to "Id" if it exists.
        if (targetCol is null && ctx.Catalog.ColumnExists(spec.Root, "Id"))
        {
            targetCol = $"{spec.Root}.Id";
        }

        // If we still couldn't pick a column, bail rather than guessing.
        if (targetCol is null) return;

        // Drop any GROUP BY on entity columns — "distinct X" should be a single count, not
        // per-group counts. (Keep GROUP BYs on date buckets so "monthly distinct customers"
        // still works.)
        var droppedGroupBy = spec.GroupBy.RemoveAll(g =>
            !g.Contains("DATEADD", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("DATEPART", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("YEAR(", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("MONTH(", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("CAST(", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("FORMAT(", System.StringComparison.OrdinalIgnoreCase));

        // Drop any SELECT items that aren't the target column (we just want a single number).
        spec.Select.RemoveAll(s => !string.Equals(s.Trim('[', ']'), targetCol.Trim('[', ']'), System.StringComparison.OrdinalIgnoreCase));

        // Replace or add the COUNT(DISTINCT col) aggregation.
        spec.Aggregations.RemoveAll(a => string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase));
        spec.Aggregations.Add(new AggregateSpec
        {
            Function = "COUNT",
            Column = targetCol,
            Distinct = true,
            Alias = "DistinctCount",
        });
        // Clear spec.Distinct — having BOTH spec.Distinct=true AND aggregations[*].Distinct=true is
        // a degenerate state (DISTINCT on a single-row aggregate result is meaningless). The
        // validator rejects it with "Spec has distinct:true and aggregations together — DISTINCT
        // on grouped results is degenerate." Reproduced on session 109 case A-COUNT-018.
        spec.Distinct = false;

        ctx.Diagnostics.Add(new(Name, $"forced COUNT(DISTINCT {targetCol}); dropped {droppedGroupBy} non-bucket GroupBy row(s)"));
    }

    /// <summary>
    /// True when ANY locale's compiled distinct-cue regex matches the question. Vocab lives
    /// entirely in <c>linguistic-cues.json.locales[*].distinct</c>; no hardcoded English or
    /// Arabic words in this file. Operator adds dialects via JSON.
    /// </summary>
    private static bool QuestionHasDistinctCue(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return false;
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale?.DistinctRegex is null) continue;
            if (locale.DistinctRegex.IsMatch(question)) return true;
        }
        return false;
    }

    private static string StripTable(string qualified)
    {
        var idx = qualified.IndexOf('.');
        return idx <= 0 ? qualified : qualified.Substring(0, idx).Trim('[', ']');
    }
    private static string StripColumn(string qualified)
    {
        var idx = qualified.IndexOf('.');
        return idx <= 0 ? qualified : qualified.Substring(idx + 1).Trim('[', ']');
    }
}
