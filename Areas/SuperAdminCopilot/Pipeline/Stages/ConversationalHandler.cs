namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Routing;

/// <summary>
/// Deterministic short-circuit for casual / human-conversation questions: greetings, thanks,
/// "what can you do", "help", farewells. Without this stage these questions hit the LLM planner
/// and eventually get refused as out-of-scope, which feels rude. Now we recognize them upfront
/// and answer with a friendly canned reply + suggested follow-up prompts so the user has a
/// concrete next step.
///
/// <para><b>Returns null</b> when the question isn't conversational — caller falls through to the
/// rest of the pipeline. Conservative on purpose: false positives (treating a real question as
/// chitchat) are worse than false negatives (letting the planner answer a "thanks" — slow but
/// not wrong).</para>
/// </summary>
public interface IConversationalHandler
{
    ConversationalReply? TryHandle(string question);
}

/// <summary>The four kinds of conversational hits. Each kind drives a different reply tone +
/// suggested-prompt set in the orchestrator. <see cref="Capabilities"/> is the highest-value
/// because it's the one users actually ask ("what can you do?") and a good answer is what makes
/// them stay.</summary>
public enum ConversationalKind
{
    Greeting,
    Capabilities,
    Thanks,
    Farewell,
}

public sealed record ConversationalReply(ConversationalKind Kind, string Reply);

internal sealed class ConversationalHandler : IConversationalHandler, IRoutingProbe
{
    private readonly IOptionsMonitor<CopilotTextCatalog> _textMonitor;
    private readonly ILogger<ConversationalHandler> _logger;

    public ConversationalHandler(IOptionsMonitor<CopilotTextCatalog> textMonitor, ILogger<ConversationalHandler> logger)
    {
        _textMonitor = textMonitor;
        _logger = logger;
    }

    public string Name => "Conversational";

    /// <summary>Probe — match-only, no text-catalog read, no side effects. Mirrors the same
    /// regex bank TryHandle uses so the router and the handler agree on every claim.</summary>
    public Task<RouterDecision?> ProbeAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return Task.FromResult<RouterDecision?>(null);
        var trimmed = question.Trim();
        if (trimmed.Length > 80) return Task.FromResult<RouterDecision?>(null);

        string? reason = null;
        if (CapabilitiesPhrase.IsMatch(trimmed)) reason = "capabilities";
        else if (GreetingExact.IsMatch(trimmed)) reason = "greeting";
        else if (ThanksPhrase.IsMatch(trimmed)) reason = "thanks";
        else if (FarewellPhrase.IsMatch(trimmed)) reason = "farewell";

        return Task.FromResult<RouterDecision?>(reason is null
            ? null
            : new RouterDecision(IntentLabel.Greeting, 1.0, Name, reason));
    }

    /// <summary>"hi" / "hello" / "hey" / "good morning" — bare greetings. Bound by length so
    /// "Hi, how many tickets…" doesn't match (only the leading word is the greeting; the question
    /// follows). We require the question to be ≤ 20 chars OR to BE just a greeting.</summary>
    private static readonly Regex GreetingExact = new(
        @"^\s*(?:hi|hello|hey(?:\s+there)?|hiya|howdy|greetings|good\s+(?:morning|afternoon|evening|day))\s*[!.?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"what can you do" / "how do you work" / "help" / "what are you capable of" /
    /// "show me what you can do" / "your capabilities" / "what's possible" / "give me examples".</summary>
    private static readonly Regex CapabilitiesPhrase = new(
        @"^\s*(?:" +
        @"what\s+can\s+(?:you|i)\s+(?:do|ask)|" +
        @"how\s+(?:do\s+)?(?:you|this)\s+work|" +
        @"what\s+(?:are\s+)?(?:your|the)\s+(?:capabilities|features)|" +
        @"what\s+(?:do|can)\s+you\s+(?:know|support|handle)|" +
        @"show\s+me\s+(?:what\s+you\s+can\s+do|examples?|some\s+examples?|sample(?:\s+questions?)?)|" +
        @"give\s+me\s+(?:some\s+)?examples?|" +
        @"^help(?:\s+me(?:\s+please)?)?\s*$|" +
        @"who\s+are\s+you|" +
        @"introduce\s+yourself|" +
        @"tell\s+me\s+about\s+(?:yourself|this\s+(?:bot|copilot|tool|app))" +
        @")[!?.\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"thanks" / "thank you" / "thx" / "ty" / "appreciate it" — acknowledgment.</summary>
    private static readonly Regex ThanksPhrase = new(
        @"^\s*(?:thanks(?:\s+a\s+lot|\s+so\s+much)?|thank\s+you|thx|ty|appreciate(?:\s+it|d)?|cheers|nice(?:\s+job)?|cool|awesome|perfect|great)\s*[!.]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"bye" / "goodbye" / "see you" / "later" — farewell.</summary>
    private static readonly Regex FarewellPhrase = new(
        @"^\s*(?:bye(?:\s+now)?|goodbye|good\s*bye|see\s+you(?:\s+(?:later|soon|around))?|see\s+ya|later|cya|farewell|take\s+care|catch\s+you\s+later|talk\s+to\s+you\s+later|ttyl)\s*[!.]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ConversationalReply? TryHandle(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var trimmed = question.Trim();

        // Cap length to avoid matching the lead of a real question. "Hi, list all tickets" is a
        // real question, not a greeting; we let it through to the planner.
        if (trimmed.Length > 80) return null;

        var text = _textMonitor.CurrentValue;
        if (CapabilitiesPhrase.IsMatch(trimmed))
        {
            _logger.LogDebug("[Conversational] capabilities match for '{Q}'.", trimmed);
            return new ConversationalReply(
                ConversationalKind.Capabilities,
                PickVariant(text.ConversationalCapabilitiesReplies, text.ConversationalCapabilities));
        }
        if (GreetingExact.IsMatch(trimmed))
            return new ConversationalReply(
                ConversationalKind.Greeting,
                PickVariant(text.ConversationalGreetingReplies, text.ConversationalGreeting));
        if (ThanksPhrase.IsMatch(trimmed))
            return new ConversationalReply(
                ConversationalKind.Thanks,
                PickVariant(text.ConversationalThanksReplies, text.ConversationalThanks));
        if (FarewellPhrase.IsMatch(trimmed))
            return new ConversationalReply(
                ConversationalKind.Farewell,
                PickVariant(text.ConversationalFarewellReplies, text.ConversationalFarewell));

        return null;
    }

    /// <summary>Picks one variant at random from <paramref name="variants"/>. Falls back to
    /// <paramref name="fallback"/> when the list is null / empty (legacy catalog JSON without
    /// the multi-variant fields). Uses <see cref="Random.Shared"/> — thread-safe, no state.</summary>
    private static string PickVariant(IReadOnlyList<string>? variants, string fallback)
    {
        if (variants is not { Count: > 0 }) return fallback;
        if (variants.Count == 1) return variants[0] ?? fallback;
        return variants[Random.Shared.Next(variants.Count)] ?? fallback;
    }
}
