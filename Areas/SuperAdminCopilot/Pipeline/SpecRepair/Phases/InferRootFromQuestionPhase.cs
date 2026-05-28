namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Score candidates against question text + entity synonyms; set or override root when multi-word match beats LLM choice.</summary>
internal sealed class InferRootFromQuestionPhase : ISpecRepairPhase
{
    public string Name => "InferRootFromQuestion";
    public string Covers => "Missing or weakly-matched root vs better question-text match";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (ctx.CandidateTables.Count == 0 || string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip annotation lines appended by the retriever — they pollute scoring.
        var cleaned = ctx.Question;
        var dashIdx = cleaned.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) cleaned = cleaned.Substring(0, dashIdx);
        var qLower = cleaned.ToLowerInvariant();

        string? best = null;
        int bestScore = 0;
        bool bestIsMultiWord = false;
        foreach (var t in ctx.CandidateTables)
        {
            var (score, isMulti) = ScoreCandidate(t.Name, qLower, ctx);
            if (score > bestScore) { bestScore = score; best = t.Name; bestIsMultiWord = isMulti; }
        }
        if (best is null) return;

        if (string.IsNullOrWhiteSpace(spec.Root))
        {
            spec.Root = best;
            ctx.Diagnostics.Add(new(Name, $"set root={best} (score={bestScore})"));
            return;
        }

        // Override only when alternative is multi-word AND meaningfully better.
        if (string.Equals(spec.Root, best, System.StringComparison.OrdinalIgnoreCase)) return;
        if (!bestIsMultiWord) return;
        var (currentScore, _) = ScoreCandidate(spec.Root, qLower, ctx);
        if (bestScore <= currentScore) return;
        var previous = spec.Root;
        spec.Root = best;
        ctx.Diagnostics.Add(new(Name, $"overrode root {previous}→{best} (score {currentScore}→{bestScore})"));
    }

    private static (int Score, bool IsMultiWord) ScoreCandidate(string? name, string qLower, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(name)) return (0, false);
        var lower = name.ToLowerInvariant();
        // 1. Multi-word PascalCase phrase match (strongest signal)
        var pascalSplit = SplitPascalCase(name).ToLowerInvariant();
        if (pascalSplit.Contains(' ') && qLower.Contains(pascalSplit))
            return (pascalSplit.Length + 100, true);

        // 2. Semantic-layer synonyms (multi-word strong, single-word regular)
        var entity = ctx.SemanticLayer.GetEntityForTable(name);
        if (entity is not null)
        {
            int bestSynScore = 0;
            bool bestSynMulti = false;
            foreach (var syn in entity.Synonyms)
            {
                if (string.IsNullOrEmpty(syn)) continue;
                var sLower = syn.ToLowerInvariant();
                if (!qLower.Contains(sLower)) continue;
                bool multi = sLower.Contains(' ');
                int score = multi ? sLower.Length + 100 : sLower.Length + 1;
                if (score > bestSynScore) { bestSynScore = score; bestSynMulti = multi; }
            }
            if (bestSynScore > 0) return (bestSynScore, bestSynMulti);
        }

        if (qLower.Contains(lower)) return (lower.Length + 1, false);
        if (lower.EndsWith("s") && qLower.Contains(lower.Substring(0, lower.Length - 1)))
            return (lower.Length, false);
        return (0, false);
    }

    private static string SplitPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
