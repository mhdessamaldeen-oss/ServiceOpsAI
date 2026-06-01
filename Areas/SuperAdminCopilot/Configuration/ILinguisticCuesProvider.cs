namespace SuperAdminCopilot.Configuration;

using System.Text.RegularExpressions;

/// <summary>
/// Singleton-scope provider exposing the loaded + pre-compiled <see cref="LinguisticCues"/>.
/// Loads <c>Configuration/linguistic-cues.json</c> at startup. Consumers receive a
/// <see cref="CompiledLinguisticCues"/> with regexes already compiled — no per-call recompilation,
/// no per-call JSON parsing.
/// </summary>
public interface ILinguisticCuesProvider
{
    /// <summary>The compiled cue set. Never null; empty when the file is missing.</summary>
    CompiledLinguisticCues Compiled { get; }
}

/// <summary>
/// Pre-compiled cue set. All <see cref="Regex"/> instances are constructed once at load and
/// reused across requests. Consumers look up cues by locale (<c>"en"</c> / <c>"ar"</c>).
/// </summary>
public sealed class CompiledLinguisticCues
{
    public int Version { get; init; } = 1;

    /// <summary>Per-locale compiled cues. Always non-null; key is the locale code.</summary>
    public IReadOnlyDictionary<string, CompiledLocaleCues> Locales { get; init; }
        = new Dictionary<string, CompiledLocaleCues>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty singleton — returned when the file is missing or invalid.</summary>
    public static CompiledLinguisticCues Empty { get; } = new();
}

public sealed class CompiledLocaleCues
{
    public IReadOnlyList<CompiledTemporalCue> Temporal { get; init; } = System.Array.Empty<CompiledTemporalCue>();
    public Regex? AbsenceRegex { get; init; }
    public Regex? AllTimeRegex { get; init; }
    public Regex? DistinctRegex { get; init; }
    public Regex? NegationRegex { get; init; }
    public Regex? RecencyDescRegex { get; init; }
    public Regex? RecencyAscRegex { get; init; }
    public Regex? SuperlativeMaxRegex { get; init; }
    public Regex? SuperlativeMinRegex { get; init; }
    public Regex? SuperlativeTopRegex { get; init; }
    public Regex? SuperlativeBottomRegex { get; init; }
    public IReadOnlyList<Regex> RangeBetween { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> RangeGt   { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> RangeGte  { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> RangeLt   { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> RangeLte  { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> RangeEq   { get; init; } = System.Array.Empty<Regex>();

    /// <summary>Raw aggregate-verb entries (no regex compilation needed — phases do Contains/StartsWith).</summary>
    public IReadOnlyList<AggregateVerbCue> AggregateVerbs { get; init; } = System.Array.Empty<AggregateVerbCue>();

    /// <summary>Possessive markers (tiered: possessive / definite / plain).</summary>
    public PossessiveCues Possessive { get; init; } = new();

    /// <summary>Compiled intent-verb regexes.</summary>
    public IReadOnlyList<Regex> IntentCount { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> IntentList  { get; init; } = System.Array.Empty<Regex>();
    public IReadOnlyList<Regex> IntentSum   { get; init; } = System.Array.Empty<Regex>();

    /// <summary>Raw status-value cues. Phases do substring Contains against the cue.</summary>
    public IReadOnlyList<StatusValueCue> StatusValues { get; init; } = System.Array.Empty<StatusValueCue>();

    /// <summary>Anti-join trigger phrases (plain strings; phases do Contains).</summary>
    public IReadOnlyList<string> AntiJoin { get; init; } = System.Array.Empty<string>();

    /// <summary>Ordering-intent markers — space-padded substring tokens.</summary>
    public IReadOnlyList<string> OrderingIntent { get; init; } = System.Array.Empty<string>();

    /// <summary>Compiled compare-shape vocabulary (single alternation regex). Null when no entries.</summary>
    public Regex? CompareMarkersRegex { get; init; }

    /// <summary>
    /// Compiled text-search trigger regexes — each MUST have a named capture group <c>noun</c>.
    /// Phases iterate this list and use the captured value as the search term.
    /// </summary>
    public IReadOnlyList<Regex> TextSearchTriggers { get; init; } = System.Array.Empty<Regex>();

    /// <summary>
    /// Compiled "knowledge question" regex with a single named capture group <c>term</c>.
    /// Null when this locale declares no knowledge-question verbs. Used by KnowledgeMatchHandler
    /// via ILinguisticRegistry.LooksLikeKnowledgeQuestion.
    /// </summary>
    public Regex? KnowledgeQuestionRegex { get; init; }

    /// <summary>
    /// Compiled aggregate-marker alternation. Null when no markers declared.
    /// Used by KnowledgeMatchHandler (refuse knowledge routing) and SqlIntentGuard (confirm SQL intent).
    /// </summary>
    public Regex? AggregateMarkerRegex { get; init; }
}

/// <summary>One compiled temporal cue. Identical shape to the JSON entry but with <see cref="Pattern"/> precompiled.</summary>
public sealed record CompiledTemporalCue(
    Regex Pattern,
    string Start,
    string? End,
    string Op,
    string Label);
