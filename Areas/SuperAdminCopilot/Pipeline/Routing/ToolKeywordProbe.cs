namespace SuperAdminCopilot.Pipeline.Routing;

using System.Text.RegularExpressions;
using FuzzierSharp;
using Humanizer;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Semantic;
using SuperAdminCopilot.Tools;

/// <summary>
/// Registry-driven probe for external tool questions. It never owns a static catalog of tool
/// domains; enabled tools are read from <see cref="IToolRegistry"/> and scored by title,
/// description, keyword hints, tool key, and test prompt.
/// </summary>
internal sealed class ToolKeywordProbe : IRoutingProbe
{
    private const int StrongScore = 8;
    private const int MinimumScore = 4;
    private const int DbShapedQuestionToolGate = 12;

    private readonly IToolRegistry _registry;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textMonitor;
    private readonly ISemanticLayer _semanticLayer;

    public ToolKeywordProbe(
        IToolRegistry registry,
        IOptionsMonitor<CopilotTextCatalog> textMonitor,
        ISemanticLayer semanticLayer)
    {
        _registry = registry;
        _textMonitor = textMonitor;
        _semanticLayer = semanticLayer;
    }

    public string Name => "ToolRegistry";

    public async Task<RouterDecision?> ProbeAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (!_registry.IsAvailable) return null;

        var tools = await _registry.GetEnabledAsync(cancellationToken);
        if (tools.Count == 0) return null;

        var stopwords = new HashSet<string>(
            _textMonitor.CurrentValue.ToolRoutingStopwords ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var questionNorm = Normalize(question);
        var questionTokens = Tokenize(questionNorm, stopwords);
        var best = tools
            .Select(t => ScoreTool(t, questionNorm, questionTokens, stopwords))
            .Where(s => s.Score >= MinimumScore)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Tool.ToolKey)
            .ToList();

        if (best.Count == 0) return null;

        var top = best[0];
        var second = best.Count > 1 ? best[1].Score : 0;
        if (IsDbShaped(questionNorm) && top.Score < DbShapedQuestionToolGate)
            return null;

        var confidence = top.Score >= StrongScore && top.Score - second >= 2 ? 0.9 : 0.72;
        return new RouterDecision(
            IntentLabel.Tool,
            confidence,
            Name,
            $"tool registry score {top.Score} for '{top.Tool.ToolKey}'");
    }

    private bool IsDbShaped(string questionNorm)
    {
        foreach (var marker in _textMonitor.CurrentValue.DbShapeMarkers ?? Enumerable.Empty<string>())
        {
            var normalized = Normalize(marker);
            if (normalized.Length >= 3 && ContainsWholeWord(questionNorm, normalized))
                return true;
        }

        // Semantic-layer entity check: use the configured synonym dictionary for O(1) lookup,
        // then fall back to word-boundary matching against entity names, table names, and
        // synonyms. This catches paraphrases via the synonym dictionary and morphological
        // variants via Humanizer without relying on raw substring matching.
        var tokens = questionNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .ToList();
        foreach (var token in tokens)
        {
            // O(1) synonym dictionary check — handles "case" → Ticket, "reply" → TicketComment
            if (_semanticLayer.GetEntityByNameOrSynonym(token) is not null) return true;
            // Humanizer morphological variants: "cases" → "case", "uploads" → "upload"
            var singular = token.Singularize(inputIsKnownToBePlural: false);
            if (!string.Equals(singular, token, StringComparison.OrdinalIgnoreCase)
                && _semanticLayer.GetEntityByNameOrSynonym(singular) is not null) return true;
        }

        // Fallback: whole-word containment against entity names/tables for multi-word entity
        // names that tokenization would split (e.g. "ai analysis" → "TicketAiAnalysis").
        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (ContainsEntityLabel(questionNorm, entity.Name) || ContainsEntityLabel(questionNorm, entity.Table))
                return true;
            foreach (var synonym in entity.Synonyms)
                if (synonym.Contains(' ') && ContainsEntityLabel(questionNorm, synonym)) return true;
        }

        return false;
    }

    private static bool ContainsEntityLabel(string questionNorm, string label)
    {
        var normalized = Normalize(label);
        return normalized.Length >= 3 && ContainsWholeWord(questionNorm, normalized);
    }

    private static ToolProbeScore ScoreTool(
        ToolDefinition tool,
        string questionNorm,
        HashSet<string> questionTokens,
        HashSet<string> stopwords)
    {
        var score = 0;
        var titleNorm = Normalize(tool.Title);
        var keyNorm = Normalize(tool.ToolKey.Replace('_', ' '));
        var testNorm = Normalize(tool.TestPrompt ?? "");

        // Fuzzy title/key matching — Fuzz.PartialRatio catches morphological variants and
        // paraphrases that raw .Contains() misses. Threshold 80 = high confidence, prevents
        // "exchange" matching "change" while allowing "exchange rates" ≈ "exchange rate".
        if (!string.IsNullOrWhiteSpace(titleNorm))
        {
            if (ContainsWholeWord(questionNorm, titleNorm))
                score += 8;
            else if (Fuzz.PartialRatio(questionNorm, titleNorm) >= 80)
                score += 5;
        }
        if (!string.IsNullOrWhiteSpace(keyNorm))
        {
            if (ContainsWholeWord(questionNorm, keyNorm))
                score += 8;
            else if (Fuzz.PartialRatio(questionNorm, keyNorm) >= 80)
                score += 5;
        }
        if (!string.IsNullOrWhiteSpace(testNorm) && string.Equals(questionNorm, testNorm, StringComparison.OrdinalIgnoreCase))
            score += 10;

        foreach (var kw in SplitTerms(tool.KeywordHints))
        {
            var normalized = Normalize(kw);
            if (normalized.Length == 0) continue;
            if (ContainsWholeWord(questionNorm, normalized))
                score += normalized.Contains(' ') ? 7 : 5;
            else if (normalized.Length >= 4 && Fuzz.PartialRatio(questionNorm, normalized) >= 85)
                score += normalized.Contains(' ') ? 5 : 3;
        }

        score += CountOverlap(questionTokens, Tokenize(tool.Title, stopwords), 4);
        score += CountOverlap(questionTokens, Tokenize(tool.Description ?? "", stopwords), 3);
        score += CountOverlap(questionTokens, Tokenize(tool.TestPrompt ?? "", stopwords), 4);
        score += CountOverlap(questionTokens, Tokenize(tool.ToolKey.Replace('_', ' '), stopwords), 3);

        return new ToolProbeScore(tool, score);
    }

    private sealed record ToolProbeScore(ToolDefinition Tool, int Score);

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static HashSet<string> Tokenize(string text, HashSet<string> stopwords)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Regex.Split(text, @"[^\w]+")
            .Where(t => t.Length >= 3 && !stopwords.Contains(t))
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitTerms(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Enumerable.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(t => t.Length >= 2);

    private static int CountOverlap(HashSet<string> a, HashSet<string> b, int maxWeight)
    {
        var hits = 0;
        foreach (var t in a) if (b.Contains(t)) hits++;
        return Math.Min(hits, maxWeight);
    }

    /// <summary>
    /// Check whether <paramref name="needle"/> appears in <paramref name="haystack"/> at word
    /// boundaries — prevents "no" matching "notifications", "top" matching "stop", etc.
    /// </summary>
    private static bool ContainsWholeWord(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
        var idx = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            var afterIdx = idx + needle.Length;
            var afterOk = afterIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIdx]);
            if (beforeOk && afterOk) return true;
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
