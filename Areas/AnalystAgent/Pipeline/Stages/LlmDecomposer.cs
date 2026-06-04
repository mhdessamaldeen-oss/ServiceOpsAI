namespace AnalystAgent.Pipeline.Stages;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Services.AI.Providers.Roles;
using AnalystAgent.Abstractions;

/// <summary>
/// LLM-based fallback decomposer. Used by the new orchestrator AFTER the existing
/// <see cref="HeuristicDecomposer"/> regex returns null — catches compound-question phrasings
/// the regex misses (e.g. mixed "and / then / also / plus" connectors, multi-sentence inputs).
/// Returns null when the question is atomic.
/// </summary>
public interface ILlmDecomposer
{
    /// <summary>Returns a list of sub-questions when the input is compound; null when atomic
    /// (or when the LLM call fails — caller should treat as atomic in that case).</summary>
    Task<IReadOnlyList<string>?> SplitAsync(string question, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="SplitAsync"/> but also returns the prompt + raw LLM output
    /// so the orchestrator can stamp them into the Decomposer step's TechnicalData. Lets the
    /// investigation panel show <em>Input</em> / <em>Output</em> / <em>Reason</em> for the
    /// decomposer just like SpecExtractor does.</summary>
    Task<DecomposerCallResult> SplitWithTraceAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Full result envelope from <see cref="ILlmDecomposer.SplitWithTraceAsync"/>.</summary>
/// <param name="SubQuestions">Sub-questions when the input was compound; null when atomic.</param>
/// <param name="Prompt">The full user prompt sent to the LLM (system prompt is fixed; not included).</param>
/// <param name="RawLlmOutput">Raw LLM response, before JSON-array extraction.</param>
/// <param name="Error">Non-null when the LLM call threw or the output couldn't be parsed.</param>
public sealed record DecomposerCallResult(
    IReadOnlyList<string>? SubQuestions,
    string? Prompt,
    string? RawLlmOutput,
    string? Error);

internal sealed class LlmDecomposer : ILlmDecomposer
{
    private readonly ILlmClient _llm;
    private readonly IDecomposer _regexDecomposer;
    private readonly ILogger<LlmDecomposer> _logger;

    public LlmDecomposer(IRoleBoundLlmClientFactory llmFactory, IDecomposer regexDecomposer, ILogger<LlmDecomposer> logger)
    {
        _llm = llmFactory.For(AiRole.Decomposer);
        _regexDecomposer = regexDecomposer;
        _logger = logger;
    }

    private const string SystemPrompt =
        "You split a user's data question into independent sub-questions ONLY when each part needs a DIFFERENT SQL query. " +
        "Each sub-question must be self-contained — runnable on its own as standalone SQL. " +
        "Return ONLY a JSON array of strings. " +
        "DEFAULT: do NOT split. A question that produces ONE SELECT statement (even with multiple columns or filters) MUST stay as one item. " +
        "Split when the parts are different ENTITIES, different AGGREGATIONS, or rank/group by DIFFERENT DIMENSIONS that cannot share one GROUP BY " +
        "(e.g. 'top 3 regions by ticket count AND top 3 departments by ticket count' → two queries, because one groups by region and the other by department). " +
        "Do NOT split when the parts share the SAME dimension and differ only by a lookup value (status / priority / period) — those become one GROUP BY. " +
        "When in doubt, do NOT split.";

    private const string UserPromptTemplate =
        "Examples:\n" +
        "Q: \"how many users\"\n" +
        "→ [\"how many users\"]\n\n" +
        "Q: \"how many users and how many roles\"\n" +
        "→ [\"how many users\", \"how many roles\"]\n\n" +
        "Q: \"open tickets created last week\"\n" +
        "→ [\"open tickets created last week\"]\n\n" +
        "Q: \"tickets, comments, and attachments — give counts for each\"\n" +
        "→ [\"count tickets\", \"count comments\", \"count attachments\"]\n\n" +
        "// PROJECTION COMPOUNDS — stay as ONE query, NOT split:\n" +
        "Q: \"for each priority, show ticket count and average age in days\"\n" +
        "→ [\"for each priority, show ticket count and average age in days\"]\n\n" +
        "Q: \"show me the 5 oldest still-open tickets with their age in days\"\n" +
        "→ [\"show me the 5 oldest still-open tickets with their age in days\"]\n\n" +
        "Q: \"list users with their entity name and ticket count\"\n" +
        "→ [\"list users with their entity name and ticket count\"]\n\n" +
        "Q: \"show pending tickets along with their pending reason\"\n" +
        "→ [\"show pending tickets along with their pending reason\"]\n\n" +
        "Q: \"show me tickets per user — full name, their entity, and the number of tickets they created\"\n" +
        "→ [\"show me tickets per user — full name, their entity, and the number of tickets they created\"]\n\n" +
        "Q: \"for each agent, show assigned, resolved, and resolution rate\"\n" +
        "→ [\"for each agent, show assigned, resolved, and resolution rate\"]\n\n" +
        "Q: \"top 5 users by tickets created\"\n" +
        "→ [\"top 5 users by tickets created\"]\n\n" +
        "// DIFFERENT-DIMENSION rankings — each needs its OWN GROUP BY + TOP, so DO split:\n" +
        "Q: \"top 3 regions by ticket count and the top 3 departments by ticket count\"\n" +
        "→ [\"top 3 regions by ticket count\", \"top 3 departments by ticket count\"]\n\n" +
        "Q: \"the 5 biggest customers by billing and the 5 busiest regions by outages\"\n" +
        "→ [\"the 5 biggest customers by billing\", \"the 5 busiest regions by outages\"]\n\n" +
        "// PERIOD COMPARISONS — stay as ONE conditional-aggregation query, NOT split:\n" +
        "Q: \"show ticket counts for today, yesterday, and two days ago\"\n" +
        "→ [\"show ticket counts for today, yesterday, and two days ago\"]\n\n" +
        "Q: \"compare ticket counts this month vs last month\"\n" +
        "→ [\"compare ticket counts this month vs last month\"]\n\n" +
        "// STATUS / LOOKUP-VALUE COMPARISONS — stay as ONE query with a single GROUP BY status\n" +
        "// (the user wants a side-by-side breakdown, NOT two independent runs that lose the shared dimension):\n" +
        "Q: \"show me open tickets by category and closed\"\n" +
        "→ [\"show me open and closed tickets by category\"]\n\n" +
        "Q: \"open and closed tickets per priority\"\n" +
        "→ [\"open and closed tickets per priority\"]\n\n" +
        "Q: \"how many tickets are critical and how many are high\"\n" +
        "→ [\"how many tickets are critical and how many are high\"]\n\n" +
        "// CRITICAL: when the second clause is a SHORT FRAGMENT (\"and closed\", \"and last month\",\n" +
        "// \"and high\") that only differs from the first by a SINGLE lookup-value (status, priority,\n" +
        "// category, period), DO NOT split — return a single normalised question that promotes the\n" +
        "// shared dimension to a GROUP BY and the two values to an IN-filter or conditional aggregation.\n\n" +
        "Q: \"{0}\"\n→ ";

    public async Task<IReadOnlyList<string>?> SplitAsync(string question, CancellationToken cancellationToken = default)
    {
        var traced = await SplitWithTraceAsync(question, cancellationToken);
        return traced.SubQuestions;
    }

    public async Task<DecomposerCallResult> SplitWithTraceAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return new(null, null, null, "empty question");
        var userPrompt = string.Format(UserPromptTemplate, question);
        string? raw = null;
        try
        {
            using var hint = Abstractions.LlmCallStageHint.Use("Decomposer");
            raw = await _llm.GenerateJsonAsync(SystemPrompt, userPrompt, cancellationToken);
            var parts = ParseArray(raw);
            // Atomic: 0 or 1 entry — treat as "don't decompose."
            if (parts is null || parts.Count <= 1) return new(null, userPrompt, raw, null);
            // Reject pieces that can't stand alone as a question. A one-word fragment ("closed",
            // "high", "yesterday") almost always means the LLM split a "X by Y and <value>"
            // phrase, dropping the noun and the dimension on the second side. Reverting to the
            // single original question is safer than running a sub-query that lost context.
            parts = parts
                .Where(p => !string.IsNullOrWhiteSpace(p)
                            && p.Length >= 8
                            && p.Trim().Contains(' ')                       // must be multi-word
                            && !_regexDecomposer.StartsWithLeadingConjunction(p)) // not an amputated continuation
                .ToList();
            if (parts.Count <= 1) return new(null, userPrompt, raw, null);
            // Cap at 4 — anything deeper is fragile.
            if (parts.Count > 4) parts = parts.Take(4).ToList();
            // Hallucination guard: each sub-question must share at least 1 content-word (length ≥ 4) with the original. The LLM has been observed inventing sub-questions like "how many bills are still outstanding" from "bills issued in the last 90 days" — they share 'bills' but the second carries new tokens (outstanding, still) the user never typed. Require >= 50% of the sub-question's content words to appear in the original.
            var originalContent = ContentTokens(question);
            if (originalContent.Count > 0)
            {
                foreach (var p in parts)
                {
                    var sub = ContentTokens(p);
                    if (sub.Count == 0) continue;
                    var overlap = sub.Count(t => originalContent.Contains(t));
                    if (overlap * 2 < sub.Count)  // <50% overlap → hallucinated tokens
                    {
                        _logger.LogInformation("[LlmDecomposer] rejecting split: sub-question '{Sub}' shares only {Overlap}/{Total} content words with original — likely LLM hallucination", p, overlap, sub.Count);
                        return new(null, userPrompt, raw, null);
                    }
                }
            }
            return new(parts, userPrompt, raw, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LlmDecomposer] split failed for '{Q}'.", question);
            return new(null, userPrompt, raw, ex.Message);
        }
    }

    // Lowercase content words length ≥ 4 — drops stopwords and short connectives, keeps domain nouns/verbs.
    private static HashSet<string> ContentTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return new();
        return new HashSet<string>(
            text.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r', '?', '.', ',', ';', ':', '\'', '"', '(', ')', '[', ']', '/', '\\', '-' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4),
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<string>? ParseArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Find the first JSON array in the raw output. Local models sometimes wrap with prose
        // even when asked for JSON only.
        var trimmed = raw.Trim();
        int start = trimmed.IndexOf('[');
        int end = trimmed.LastIndexOf(']');
        if (start < 0 || end < 0 || end <= start) return null;
        var jsonSlice = trimmed.Substring(start, end - start + 1);
        try
        {
            return JsonSerializer.Deserialize<List<string>>(jsonSlice);
        }
        catch { return null; }
    }
}
