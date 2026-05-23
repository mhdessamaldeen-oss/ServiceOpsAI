namespace SuperAdminCopilot.Pipeline.Stages;

/// <summary>
/// Decides whether a new question is a refinement of the previous turn's spec — when true,
/// the orchestrator calls <c>ISpecExtractor.RefineAsync</c> with the prior spec instead of
/// generating from scratch. Heuristic-only; cheap.
///
/// <para>Triggers (any of):</para>
/// <list type="bullet">
///   <item>Leading connectors: "actually", "and", "but", "also", "now", "just", "only", "instead", "wait", "no,"</item>
///   <item>Anaphoric references: "those", "these", "that", "them", "the previous", "the earlier"</item>
///   <item>Short follow-up: ≤ 5 words (when a previous spec exists, the orchestrator is asked
///         once and the model can still emit a fresh spec if the words don't match the context)</item>
/// </list>
/// </summary>
public interface IRefinementDetector
{
    bool LooksLikeRefinement(string question);
}

internal sealed class RefinementDetector : IRefinementDetector
{
    private static readonly string[] LeadingConnectors =
    {
        "actually", "actually,", "but ", "but,", "and ", "and,", "also ", "also,",
        "now ", "now,", "just ", "only ", "instead ", "instead,", "wait ", "wait,",
        "no, ", "no,",
    };

    private static readonly string[] Anaphora =
    {
        " those", " these", " that", " them", "the previous", "the earlier",
        "previous query", "previous result", "the same", "the above",
    };

    /// <summary>Refinement compound phrases. Added 2026-05-20 after observing the multi-turn
    /// baseline run miss them. The patterns are intentionally schema-agnostic — no specific
    /// table names, just the verbal scaffolding the user emits when they're modifying a prior
    /// query rather than starting fresh.</summary>
    private static readonly string[] RefinementPhrases =
    {
        "break that down by", "break it down by", "broken down by",
        "show me the", "actually show me", "actually show", "show me instead",
        "sort them by", "sort that by", "sort by", "order them by", "order by",
        "filter that by", "filter them by", "filter to", "filter on",
        "group by", "group them by", "grouped by",
        "now by", "now per", "now show", "what about",
    };

    public bool LooksLikeRefinement(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var lower = question.Trim().ToLowerInvariant();
        foreach (var lead in LeadingConnectors)
            if (lower.StartsWith(lead, StringComparison.Ordinal)) return true;
        foreach (var a in Anaphora)
            if (lower.Contains(a, StringComparison.Ordinal)) return true;
        foreach (var p in RefinementPhrases)
            if (lower.Contains(p, StringComparison.Ordinal)) return true;
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount <= 5;
    }
}
