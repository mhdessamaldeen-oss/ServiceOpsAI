namespace SuperAdminCopilot.Pipeline.Stages;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Decides whether a new question is a refinement of the previous turn's spec — when true,
/// the orchestrator calls <c>ISpecExtractor.RefineAsync</c> with the prior spec instead of
/// generating from scratch. Heuristic-only; cheap.
///
/// <para>Cues are CONFIG-DRIVEN (<c>Configuration/refinement-cues.json</c>, per-locale, hot-editable):</para>
/// <list type="bullet">
///   <item>Leading connectors (prefix match): "actually", "and", "but", "now", … / Arabic equivalents.</item>
///   <item>Anaphora + refinement phrases (substring match): "those", "the previous", "sort them by", …</item>
///   <item>Short follow-up: word-count ≤ a configurable threshold (language-agnostic).</item>
/// </list>
/// When the config file is absent the in-code English fallback applies — byte-identical to the
/// pre-2026-06-02 hardcoded behavior. To tune cues or add a language, edit the JSON; no recompile.
/// </summary>
public interface IRefinementDetector
{
    bool LooksLikeRefinement(string question);
}

internal sealed class RefinementDetector : IRefinementDetector
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lazy<Cues> _cues;

    public RefinementDetector(IOptions<CopilotOptions> options, ILogger<RefinementDetector> logger)
    {
        _cues = new Lazy<Cues>(() => Load(options.Value.RefinementCuesPath, logger));
    }

    public bool LooksLikeRefinement(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var lower = question.Trim().ToLowerInvariant();
        var c = _cues.Value;
        foreach (var lead in c.LeadingConnectors)
            if (lower.StartsWith(lead, StringComparison.Ordinal)) return true;
        foreach (var sub in c.Substrings)
            if (lower.Contains(sub, StringComparison.Ordinal)) return true;
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount <= c.MaxWords;
    }

    // Flattened, ready-to-scan cue set (leading-connector prefixes; anaphora+phrase substrings; short-reply threshold).
    private sealed record Cues(IReadOnlyList<string> LeadingConnectors, IReadOnlyList<string> Substrings, int MaxWords);

    private static Cues Load(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogInformation("[RefinementDetector] {File} not found; using in-code English fallback cues.", path);
            return FallbackEn;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CuesFile>(stream, JsonOpts);
            if (file is null || file.Locales.Count == 0) return FallbackEn;
            var leads = new List<string>();
            var subs = new List<string>();
            foreach (var locale in file.Locales.Values)
            {
                leads.AddRange(locale.LeadingConnectors);
                subs.AddRange(locale.Anaphora);
                subs.AddRange(locale.Phrases);
            }
            var maxWords = file.ShortFollowUpMaxWords > 0 ? file.ShortFollowUpMaxWords : FallbackEn.MaxWords;
            logger.LogInformation("[RefinementDetector] Loaded refinement cues from {File} ({L} leading, {S} substring, ≤{W} words).",
                path, leads.Count, subs.Count, maxWords);
            return new Cues(leads, subs, maxWords);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RefinementDetector] Failed to load {File}; using in-code fallback.", path);
            return FallbackEn;
        }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    /// <summary>In-code English fallback — byte-identical to the pre-2026-06-02 hardcoded arrays
    /// (leading connectors as prefixes; anaphora + refinement phrases merged as substrings; ≤5 words).</summary>
    private static readonly Cues FallbackEn = new(
        new[]
        {
            "actually", "actually,", "but ", "but,", "and ", "and,", "also ", "also,",
            "now ", "now,", "just ", "only ", "instead ", "instead,", "wait ", "wait,",
            "no, ", "no,",
        },
        new[]
        {
            " those", " these", " that", " them", "the previous", "the earlier",
            "previous query", "previous result", "the same", "the above",
            "break that down by", "break it down by", "broken down by",
            "show me the", "actually show me", "actually show", "show me instead",
            "sort them by", "sort that by", "sort by", "order them by", "order by",
            "filter that by", "filter them by", "filter to", "filter on",
            "group by", "group them by", "grouped by",
            "now by", "now per", "now show", "what about",
        },
        5);

    // JSON DTOs
    private sealed class CuesFile
    {
        public int ShortFollowUpMaxWords { get; set; } = 5;
        public Dictionary<string, LocaleCues> Locales { get; set; } = new();
    }
    private sealed class LocaleCues
    {
        public List<string> LeadingConnectors { get; set; } = new();
        public List<string> Anaphora { get; set; } = new();
        public List<string> Phrases { get; set; } = new();
    }
}
