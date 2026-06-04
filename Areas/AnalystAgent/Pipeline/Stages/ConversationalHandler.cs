namespace AnalystAgent.Pipeline.Stages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;

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
///
/// <para>Detection patterns are CONFIG-DRIVEN (<c>Configuration/conversational-cues.json</c>,
/// per-locale, hot-editable). The reply TEXT is separately config-driven via
/// <see cref="CopilotTextCatalog"/>. When the cues file is absent the in-code English fallback
/// applies — byte-identical to the pre-2026-06-02 hardcoded regexes.</para>
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

internal sealed class ConversationalHandler : IConversationalHandler
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IOptionsMonitor<CopilotTextCatalog> _textMonitor;
    private readonly ILogger<ConversationalHandler> _logger;
    private readonly Lazy<Cues> _cues;

    public ConversationalHandler(
        IOptionsMonitor<CopilotTextCatalog> textMonitor,
        IOptions<AnalystOptions> options,
        ILogger<ConversationalHandler> logger)
    {
        _textMonitor = textMonitor;
        _logger = logger;
        _cues = new Lazy<Cues>(() => Load(options.Value.ConversationalCuesPath, logger));
    }

    public string Name => "Conversational";

    public ConversationalReply? TryHandle(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var trimmed = question.Trim();

        // Cap length to avoid matching the lead of a real question. "Hi, list all tickets" is a
        // real question, not a greeting; we let it through to the planner.
        if (trimmed.Length > 80) return null;

        var c = _cues.Value;
        var text = _textMonitor.CurrentValue;

        // Order matters: capabilities first (highest-value intent), then greeting / thanks / farewell.
        if (Matches(c.Capabilities, trimmed))
        {
            _logger.LogDebug("[Conversational] capabilities match for '{Q}'.", trimmed);
            return new ConversationalReply(
                ConversationalKind.Capabilities,
                PickVariant(text.ConversationalCapabilitiesReplies, text.ConversationalCapabilities));
        }
        if (Matches(c.Greeting, trimmed))
            return new ConversationalReply(
                ConversationalKind.Greeting,
                PickVariant(text.ConversationalGreetingReplies, text.ConversationalGreeting));
        if (Matches(c.Thanks, trimmed))
            return new ConversationalReply(
                ConversationalKind.Thanks,
                PickVariant(text.ConversationalThanksReplies, text.ConversationalThanks));
        if (Matches(c.Farewell, trimmed))
            return new ConversationalReply(
                ConversationalKind.Farewell,
                PickVariant(text.ConversationalFarewellReplies, text.ConversationalFarewell));

        return null;
    }

    private static bool Matches(IReadOnlyList<Regex> patterns, string s)
    {
        foreach (var r in patterns)
            if (r.IsMatch(s)) return true;
        return false;
    }

    /// <summary>Compiled detection patterns, one list per conversational kind, merged across locales.</summary>
    private sealed record Cues(
        IReadOnlyList<Regex> Capabilities,
        IReadOnlyList<Regex> Greeting,
        IReadOnlyList<Regex> Thanks,
        IReadOnlyList<Regex> Farewell);

    private static Cues Load(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogInformation("[ConversationalHandler] {File} not found; using in-code English fallback patterns.", path);
            return FallbackEn;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CuesFile>(stream, JsonOpts);
            if (file is null || file.Locales.Count == 0) return FallbackEn;

            var cap = new List<Regex>(); var greet = new List<Regex>();
            var thx = new List<Regex>(); var bye = new List<Regex>();
            foreach (var locale in file.Locales.Values)
            {
                Compile(locale.Capabilities, cap);
                Compile(locale.Greeting, greet);
                Compile(locale.Thanks, thx);
                Compile(locale.Farewell, bye);
            }
            if (cap.Count == 0 && greet.Count == 0 && thx.Count == 0 && bye.Count == 0) return FallbackEn;

            logger.LogInformation("[ConversationalHandler] Loaded conversational cues from {File} ({L} locale(s)).", path, file.Locales.Count);
            return new Cues(cap, greet, thx, bye);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ConversationalHandler] Failed to load {File}; using in-code fallback.", path);
            return FallbackEn;
        }
    }

    private static void Compile(List<string>? patterns, List<Regex> into)
    {
        if (patterns is null) return;
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { into.Add(new Regex(p, Opts)); }
            catch (ArgumentException) { /* skip malformed; the English fallback list still covers detection */ }
        }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    /// <summary>In-code English fallback — byte-identical to the pre-2026-06-02 hardcoded regexes,
    /// checked in the order capabilities → greeting → thanks → farewell.</summary>
    private static readonly Cues FallbackEn = new(
        new[] { new Regex(@"^\s*(?:what\s+can\s+(?:you|i)\s+(?:do|ask)|how\s+(?:do\s+)?(?:you|this)\s+work|what\s+(?:are\s+)?(?:your|the)\s+(?:capabilities|features)|what\s+(?:do|can)\s+you\s+(?:know|support|handle)|show\s+me\s+(?:what\s+you\s+can\s+do|examples?|some\s+examples?|sample(?:\s+questions?)?)|give\s+me\s+(?:some\s+)?examples?|^help(?:\s+me(?:\s+please)?)?\s*$|who\s+are\s+you|introduce\s+yourself|tell\s+me\s+about\s+(?:yourself|this\s+(?:bot|copilot|tool|app)))[!?.\s]*$", Opts) },
        new[] { new Regex(@"^\s*(?:hi|hello|hey(?:\s+there)?|hiya|howdy|greetings|good\s+(?:morning|afternoon|evening|day))\s*[!.?]*\s*$", Opts) },
        new[] { new Regex(@"^\s*(?:thanks(?:\s+a\s+lot|\s+so\s+much)?|thank\s+you|thx|ty|appreciate(?:\s+it|d)?|cheers|nice(?:\s+job)?|cool|awesome|perfect|great)\s*[!.]*\s*$", Opts) },
        new[] { new Regex(@"^\s*(?:bye(?:\s+now)?|goodbye|good\s*bye|see\s+you(?:\s+(?:later|soon|around))?|see\s+ya|later|cya|farewell|take\s+care|catch\s+you\s+later|talk\s+to\s+you\s+later|ttyl)\s*[!.]*\s*$", Opts) });

    // JSON DTOs
    private sealed class CuesFile
    {
        public Dictionary<string, LocaleConvCues> Locales { get; set; } = new();
    }

    private sealed class LocaleConvCues
    {
        public List<string> Capabilities { get; set; } = new();
        public List<string> Greeting { get; set; } = new();
        public List<string> Thanks { get; set; } = new();
        public List<string> Farewell { get; set; } = new();
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
