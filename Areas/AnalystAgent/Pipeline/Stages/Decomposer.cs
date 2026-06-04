namespace AnalystAgent.Pipeline.Stages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Models;

/// <summary>
/// §11 of the abstraction guide. Splits compound questions ("X and Y", "X versus Y", "X
/// compared to Y") into independent sub-questions so each can run through the pipeline as a
/// single-spec query. Returns null when the question is not compound — caller falls through
/// to the normal single-question flow.
///
/// This is intentionally conservative: false negatives (failing to split) are fine — the
/// single-spec planner still answers something — but false positives waste two LLM calls.
/// The threshold is set so only clear conjunction/comparison signals trigger.
///
/// <para>Split / sequential / no-decompose-guard patterns are CONFIG-DRIVEN
/// (<c>Configuration/decomposition-cues.json</c>, per locale, hot-editable). When the file is
/// absent the in-code English fallback applies — byte-identical to the pre-2026-06-02 hardcoded
/// regexes. The step caps + NormalizeAsQuestion stay in code.</para>
/// </summary>
public interface IDecomposer
{
    /// <summary>
    /// Inspects the question. Returns null when it should NOT be decomposed; otherwise returns
    /// the list of sub-questions in the order they should be answered. The orchestrator runs
    /// each through the full pipeline and concatenates the answers.
    /// </summary>
    DecompositionResult? Decompose(string question);

    /// <summary>Cheap deterministic pre-gate (no LLM): returns true when the question carries a
    /// compound/comparison/sequential/multi-clause signal per the SAME config-driven cues
    /// <see cref="Decompose"/> uses. RECALL-oriented — it errs toward true so genuinely compound shapes
    /// (incl. different-dimension "top N X by A and top N Y by B") still reach the LLM; it returns false
    /// only for confidently-atomic questions (incl. single grouped-comparisons). The orchestrator skips
    /// the LLM decomposer on a false, eliminating that round-trip for the large majority of atomic
    /// questions, at the cost of a few extra LLM calls on ambiguous multi-grouping questions (where the
    /// LLM then correctly returns atomic). Reuses the cues; no new vocabulary.</summary>
    bool MightBeCompound(string question);

    /// <summary>True when the text opens with a coordinating conjunction per the config-driven
    /// LeadingConjunction cue (EN + AR) — i.e. it is an amputated continuation ("and status open"),
    /// not a standalone sub-question. Exposed so the LLM decomposer can reject such fragments by
    /// REUSING this cue instead of hardcoding its own (which previously had no Arabic coverage).</summary>
    bool StartsWithLeadingConjunction(string text);
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
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Lazy<Cues> _cues;

    public HeuristicDecomposer(IOptions<AnalystOptions> options, ILogger<HeuristicDecomposer> logger)
    {
        _cues = new Lazy<Cues>(() => Load(options.Value.DecompositionCuesPath, logger));
    }

    public DecompositionResult? Decompose(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var c = _cues.Value;

        // Doc Phase 8: don't decompose grouped comparisons or by/per group-by questions —
        // they should compile as a single grouped query.
        if (c.GroupedComparisonShape.IsMatch(question)) return null;

        // Sequential chains take precedence over parallel "X and Y" — they're a stronger signal,
        // and the parallel split would mangle "Find the customer with most tickets, then show
        // their last 5 tickets" by anchoring on "and" inside "tickets, then…".
        if (c.SequentialChain.IsMatch(question))
        {
            var seqParts = c.SequentialSplit.Split(question)
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

        if (!c.StrongCompoundSignals.IsMatch(question)) return null;

        var parts = c.SplitDelimiter.Split(question)
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

    public bool MightBeCompound(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var c = _cues.Value;
        // "...in one list / combined / together / side by side" means the user wants ONE result set
        // (a UNION), NOT two separate answers. Keep it atomic so the emitter writes a single UNION ALL
        // statement; splitting it produces two disjoint queries. Checked FIRST so it wins over the
        // multi-clause compound signal below.
        if (c.CombineGuard.IsMatch(question)) return false;
        // TWO distinct ranking/grouping clauses joined by "and" ("top 3 regions by X AND top 3
        // departments by Y", "the 5 biggest customers by billing and the 5 busiest regions by outages")
        // is a genuine DIFFERENT-DIMENSION compound the LLM splits into two queries. Recognize it
        // BEFORE the grouped-comparison guard, whose greedy [\s\w,]+ would otherwise swallow the whole
        // thing as one query and wrongly mark it atomic.
        if (c.MultiClauseCompound.IsMatch(question)) return true;
        // A SINGLE "by/per" or vs grouped comparison is deliberately one grouped query, not a split.
        if (c.GroupedComparisonShape.IsMatch(question)) return false;
        return c.SequentialChain.IsMatch(question) || c.StrongCompoundSignals.IsMatch(question);
    }

    public bool StartsWithLeadingConjunction(string text) =>
        !string.IsNullOrWhiteSpace(text) && _cues.Value.LeadingConjunction.IsMatch(text);

    private string NormalizeAsQuestion(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return fragment;
        var trimmed = _cues.Value.LeadingConjunction.Replace(fragment.Trim(), "").Trim();
        if (trimmed.Length == 0) return trimmed;
        // Re-attach a question mark if the original split removed it.
        if (!trimmed.EndsWith('?') && !trimmed.EndsWith('.')) trimmed += "?";
        return trimmed;
    }

    /// <summary>One compiled regex per decomposition concern (OR-joined across locales).</summary>
    private sealed record Cues(
        Regex StrongCompoundSignals,
        Regex SplitDelimiter,
        Regex SequentialChain,
        Regex SequentialSplit,
        Regex GroupedComparisonShape,
        Regex MultiClauseCompound,
        Regex CombineGuard,
        Regex LeadingConjunction);

    private static Cues Load(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogInformation("[Decomposer] {File} not found; using in-code English fallback patterns.", path);
            return FallbackEn;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CuesFile>(stream, JsonOpts);
            if (file is null || file.Locales.Count == 0) return FallbackEn;
            var locales = file.Locales.Values.ToList();
            logger.LogInformation("[Decomposer] Loaded decomposition cues from {File} ({L} locale(s)).", path, locales.Count);
            return new Cues(
                Combine(locales, l => l.StrongCompoundSignals, FallbackEn.StrongCompoundSignals),
                Combine(locales, l => l.SplitDelimiter,         FallbackEn.SplitDelimiter),
                Combine(locales, l => l.SequentialChain,        FallbackEn.SequentialChain),
                Combine(locales, l => l.SequentialSplit,        FallbackEn.SequentialSplit),
                Combine(locales, l => l.GroupedComparisonShape, FallbackEn.GroupedComparisonShape),
                Combine(locales, l => l.MultiClauseCompound,    FallbackEn.MultiClauseCompound),
                Combine(locales, l => l.CombineGuard,           FallbackEn.CombineGuard),
                Combine(locales, l => l.LeadingConjunction,     FallbackEn.LeadingConjunction));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Decomposer] Failed to load {File}; using in-code fallback.", path);
            return FallbackEn;
        }
    }

    /// <summary>OR-join the non-empty per-locale bodies for one concern into a single compiled regex
    /// (each wrapped in a non-capturing group to preserve alternation precedence). Falls back to the
    /// in-code regex when no locale declares the concern.</summary>
    private static Regex Combine(List<LocaleDecompCues> locales, Func<LocaleDecompCues, string?> select, Regex fallback)
    {
        var bodies = locales.Select(select)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => $"(?:{b})")
            .ToList();
        if (bodies.Count == 0) return fallback;
        try { return new Regex(string.Join("|", bodies), Opts); }
        catch (ArgumentException) { return fallback; }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    /// <summary>In-code English fallback — byte-identical to the pre-2026-06-02 hardcoded regexes.</summary>
    private static readonly Cues FallbackEn = new(
        new Regex(@"\b(?:vs\.?|versus|compared\s+to|as\s+well\s+as|and\s+also|;\s*also)\b|(?:\?\s*and\s+)|(?:\.\s*also[, ])|(?:\bcompare\s+\b)|(?:\band\s+how\s+(?:many|much|long|often)\b)|(?:\band\s+what\s+(?:is|are|was|were)\b)|(?:\band\s+which\s+\w+\b)", Opts),
        new Regex(@"(?:\?\s*and\s+)|(?:\.\s*also[, ])|(?:;\s*(?:and\s+)?(?:also[, ])?)|(?:\bversus\b)|(?:\bvs\.?\s+)|(?:\bcompared\s+to\b)|(?:\bas\s+well\s+as\b)|(?:\band\s+also\b)|(?=\band\s+how\s+(?:many|much|long|often)\b)|(?=\band\s+what\s+(?:is|are|was|were)\b)|(?=\band\s+which\s+\w+\b)", Opts),
        new Regex(@"(?:,\s*then\s+(?:show|list|find|give|return|count|display|tell))|(?:\bthen\s+show\s+(?:me\s+)?their\b)|(?:\bthen\s+list\s+(?:their|those)\b)|(?:\bfor\s+(?:those|these|that)\s+\w+)|(?:\band\s+then\s+(?:show|list|find|count|display)\b)|(?:\bbased\s+on\s+(?:those|that|these)\b)", Opts),
        new Regex(@"(?:,\s*then\s+)|(?:\.\s*then\s+)|(?=\bthen\s+show\s+(?:me\s+)?their\b)|(?=\bthen\s+list\s+(?:their|those)\b)|(?=\band\s+then\s+(?:show|list|find|count|display)\b)|(?=\bbased\s+on\s+(?:those|that|these)\b)", Opts),
        new Regex(@"\b(?:how\s+many|count\s+of|number\s+of)\s+\w+(?:\s+are)?\s+\w+\s+(?:versus|vs\.?)\s+\w+\s*\??\s*\.?$|^[\s\w,]+(?:\s+per\s+\w+|\s+by\s+\w+|\s+grouped\s+by\s+\w+)(?:\s+and\s+(?:per\s+|by\s+)?\w+)?\s*\??\s*\.?$|\b(?:compare|comparison\s+of)\b.+\b(?:vs\.?|versus|compared\s+to|to)\b.+\b(?:today|yesterday|this\s+(?:week|month|quarter|year)|last\s+(?:week|month|quarter|year)|previous\s+(?:week|month|quarter|year)|prior\s+(?:week|month|quarter|year)|year\s+over\s+year|month\s+over\s+month|week\s+over\s+week)\b", Opts),
        new Regex(@"(?:\btop\s+\d+\b.*\band\b.*\btop\s+\d+\b)|(?:\bby\s+\w+\b.*\band\b.*\bby\s+\w+\b)|(?:\bper\s+\w+\b.*\band\b.*\bper\s+\w+\b)", Opts),
        new Regex(@"\bin\s+(?:one|a\s+single)\s+(?:list|table|result|query|output)\b|\bcombined\b|\btogether\b|\bas\s+(?:one|a\s+single)\b|\bside\s+by\s+side\b|\bin\s+the\s+same\s+(?:list|table|result)\b", Opts),
        new Regex(@"^\s*(?:and|also|or|then)\s+", Opts));

    // JSON DTOs
    private sealed class CuesFile
    {
        public Dictionary<string, LocaleDecompCues> Locales { get; set; } = new();
    }

    private sealed class LocaleDecompCues
    {
        public string? StrongCompoundSignals { get; set; }
        public string? SplitDelimiter { get; set; }
        public string? SequentialChain { get; set; }
        public string? SequentialSplit { get; set; }
        public string? GroupedComparisonShape { get; set; }
        public string? MultiClauseCompound { get; set; }
        public string? CombineGuard { get; set; }
        public string? LeadingConjunction { get; set; }
    }
}
