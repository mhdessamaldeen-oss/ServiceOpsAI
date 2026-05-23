namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// §11 of the abstraction guide. Splits compound questions ("X and Y", "X versus Y", "X
/// compared to Y") into independent sub-questions so each can run through the pipeline as a
/// single-spec query. Returns null when the question is not compound — caller falls through
/// to the normal single-question flow.
///
/// This is intentionally conservative: false negatives (failing to split) are fine — the
/// single-spec planner still answers something — but false positives waste two LLM calls.
/// The threshold is set so only clear conjunction/comparison signals trigger.
/// </summary>
public interface IDecomposer
{
    /// <summary>
    /// Inspects the question. Returns null when it should NOT be decomposed; otherwise returns
    /// the list of sub-questions in the order they should be answered. The orchestrator runs
    /// each through the full pipeline and concatenates the answers.
    /// </summary>
    DecompositionResult? Decompose(string question);
}

public sealed record DecompositionResult(
    IReadOnlyList<string> SubQuestions,
    string Joiner,
    /// <summary>
    /// <c>Independent</c> = sub-questions run in parallel-style fan-out (the legacy "X and Y"
    /// behavior; each query is self-contained). <c>Sequential</c> = step-N's row IDs are
    /// threaded into step-(N+1)'s question via a context line, so chains like "Find the
    /// customer with most tickets, then show their last 5 tickets" work — step 2 receives
    /// the customer ID without the user repeating it. C.5 multi-step results threading.
    /// </summary>
    DecompositionDependency Dependency = DecompositionDependency.Independent);

public enum DecompositionDependency
{
    Independent,
    Sequential,
}

internal sealed class HeuristicDecomposer : IDecomposer
{
    // High-signal compound markers. Keep this tight: a generic "and" appears in too many simple
    // questions ("tickets created and closed today" is one query, not two). The "and how many" /
    // "and what" / "and which" forms are added because users naturally chain count questions
    // without a "?" between them ("...this week and how many last week?").
    private static readonly Regex StrongCompoundSignals = new(
        @"\b(?:vs\.?|versus|compared\s+to|as\s+well\s+as|and\s+also|;\s*also)\b" +
        @"|(?:\?\s*and\s+)" +                                       // "X? and Y?"
        @"|(?:\.\s*also[, ])" +                                    // ". Also, ..."
        @"|(?:\bcompare\s+\b)" +                                    // "compare X with Y"
        @"|(?:\band\s+how\s+(?:many|much|long|often)\b)" +          // "X and how many Y"
        @"|(?:\band\s+what\s+(?:is|are|was|were)\b)" +              // "X and what are Y"
        @"|(?:\band\s+which\s+\w+\b)",                              // "X and which Y"
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Question conjunction split. Keep the split anchored on the conjunction itself so the
    // following clause keeps its noun ("how many last week" stays whole, not split on "how").
    private static readonly Regex SplitDelimiter = new(
        @"(?:\?\s*and\s+)|(?:\.\s*also[, ])|(?:;\s*(?:and\s+)?(?:also[, ])?)|(?:\bversus\b)|(?:\bvs\.?\s+)|(?:\bcompared\s+to\b)|(?:\bas\s+well\s+as\b)|(?:\band\s+also\b)" +
        @"|(?=\band\s+how\s+(?:many|much|long|often)\b)" +
        @"|(?=\band\s+what\s+(?:is|are|was|were)\b)" +
        @"|(?=\band\s+which\s+\w+\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Sequential / chain markers. A SECOND clause that explicitly references the first's
    // result by phrase ("then show their X" / "for those Y" / "based on those" / ", then list…").
    // These are stronger than the parallel "and" markers above — when they fire we know the
    // user expects step 2 to consume step 1's output, not a parallel answer. C.5 wires this so
    // the orchestrator threads step-1's row IDs into step-2's question.
    private static readonly Regex SequentialChain = new(
        @"(?:,\s*then\s+(?:show|list|find|give|return|count|display|tell))" +
        @"|(?:\bthen\s+show\s+(?:me\s+)?their\b)" +
        @"|(?:\bthen\s+list\s+(?:their|those)\b)" +
        @"|(?:\bfor\s+(?:those|these|that)\s+\w+)" +
        @"|(?:\band\s+then\s+(?:show|list|find|count|display)\b)" +
        @"|(?:\bbased\s+on\s+(?:those|that|these)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Sequential chain split. Anchors the split AT (not after) the chain marker so clause-2
    // retains its referring phrase ("their last 5 tickets" stays whole, not "last 5 tickets").
    private static readonly Regex SequentialSplit = new(
        @"(?:,\s*then\s+)" +
        @"|(?:\.\s*then\s+)" +
        @"|(?=\bthen\s+show\s+(?:me\s+)?their\b)" +
        @"|(?=\bthen\s+list\s+(?:their|those)\b)" +
        @"|(?=\band\s+then\s+(?:show|list|find|count|display)\b)" +
        @"|(?=\bbased\s+on\s+(?:those|that|these)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Conservative skip-decomposition guard (doc Phase 8). When the question is a GROUPED
    /// comparison ("how many tickets are open versus closed"), splitting into "open?" + "closed?"
    /// destroys the subject and produces two count-all queries with the same answer. These should
    /// compile as a single grouped query (SELECT status, COUNT(*) GROUP BY status), not as two
    /// independent sub-questions. Returns true when the question matches a known no-decompose
    /// shape.
    /// </summary>
    private static readonly Regex GroupedComparisonShape = new(
        // "how many X are A versus B" / "X A vs B" — short value words around versus/vs
        @"\b(?:how\s+many|count\s+of|number\s+of)\s+\w+(?:\s+are)?\s+\w+\s+(?:versus|vs\.?)\s+\w+\s*\??\s*\.?$" +
        // "X by Y" / "X per Y" — group-by-shaped questions that don't decompose
        @"|^[\s\w,]+(?:\s+per\s+\w+|\s+by\s+\w+|\s+grouped\s+by\s+\w+)(?:\s+and\s+(?:per\s+|by\s+)?\w+)?\s*\??\s*\.?$" +
        // "compare X this <period> vs|versus|with last <period>" — period comparisons map to
        // a single SUM(CASE) conditional-aggregation query, NOT two separate sub-questions.
        // Time-period words are universal across domains; no table-specific vocabulary here.
        @"|\b(?:compare|comparison\s+of)\b.+\b(?:vs\.?|versus|compared\s+to|to)\b.+\b(?:today|yesterday|this\s+(?:week|month|quarter|year)|last\s+(?:week|month|quarter|year)|previous\s+(?:week|month|quarter|year)|prior\s+(?:week|month|quarter|year)|year\s+over\s+year|month\s+over\s+month|week\s+over\s+week)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DecompositionResult? Decompose(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;

        // Doc Phase 8: don't decompose grouped comparisons or by/per group-by questions —
        // they should compile as a single grouped query.
        if (GroupedComparisonShape.IsMatch(question)) return null;

        // Sequential chains take precedence over parallel "X and Y" — they're a stronger signal,
        // and the parallel split would mangle "Find the customer with most tickets, then show
        // their last 5 tickets" by anchoring on "and" inside "tickets, then…".
        if (SequentialChain.IsMatch(question))
        {
            var seqParts = SequentialSplit.Split(question)
                .Select(p => p.Trim().Trim(';', '.', ',', ' '))
                .Where(p => p.Length >= 4)
                .Select(NormalizeAsQuestion)
                .ToList();
            // Sequential chains we cap at 3 steps — anything deeper is fragile and the user
            // is better served asking one step at a time so they can see intermediate results.
            if (seqParts.Count >= 2)
            {
                if (seqParts.Count > 3) seqParts = seqParts.Take(3).ToList();
                return new DecompositionResult(seqParts, "then", DecompositionDependency.Sequential);
            }
        }

        if (!StrongCompoundSignals.IsMatch(question)) return null;

        var parts = SplitDelimiter.Split(question)
            .Select(p => p.Trim().Trim(';', '.', ' '))
            .Where(p => p.Length >= 4)               // skip noise fragments
            .Select(NormalizeAsQuestion)
            .ToList();

        if (parts.Count < 2) return null;

        // Cap at 4 — if the user asks more than that in one breath, decomposition is the wrong
        // tool; they should ask separately. Run the first 4 to bound cost.
        if (parts.Count > 4) parts = parts.Take(4).ToList();

        return new DecompositionResult(parts, "and", DecompositionDependency.Independent);
    }

    /// <summary>Strip a leading conjunction the lookahead-split left behind ("and how many..." → "how many...").</summary>
    private static readonly Regex LeadingConjunction = new(
        @"^\s*(?:and|also|or|then)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormalizeAsQuestion(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return fragment;
        var trimmed = LeadingConjunction.Replace(fragment.Trim(), "").Trim();
        if (trimmed.Length == 0) return trimmed;
        // Re-attach a question mark if the original split removed it.
        if (!trimmed.EndsWith('?') && !trimmed.EndsWith('.')) trimmed += "?";
        return trimmed;
    }
}
