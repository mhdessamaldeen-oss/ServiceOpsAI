namespace SuperAdminCopilot.Application.Repair;

using System.Collections.Generic;

/// <summary>
/// THE single source of truth for question-text vocabulary that Repair Rules consult.
/// See ADR-004. Repair Rules MUST NOT inline regex against question text — they call this.
/// An architecture test forbids regex literals containing non-ASCII letters outside the
/// implementation of <see cref="ILinguisticRegistry"/>.
/// </summary>
public interface ILinguisticRegistry
{
    /// <summary>True if any locale's <see cref="CueKind"/> regex matches the question.</summary>
    bool HasCue(string question, CueKind kind);

    /// <summary>Extract every temporal scope mentioned (today / this month / Q1 2025 / في 2025 / …).</summary>
    IReadOnlyList<TemporalSpan> ExtractTemporal(string question);

    /// <summary>Extract every status / severity mention with its canonical value.</summary>
    IReadOnlyList<StatusMention> ExtractStatus(string question);

    /// <summary>Extract entity mentions ("tickets" → Tickets table) via semantic-layer synonyms.</summary>
    IReadOnlyList<EntityMention> ExtractEntityMentions(string question);

    /// <summary>Extract a top-N / bottom-N intent with its captured size, if any.</summary>
    Superlative? ExtractSuperlative(string question);

    /// <summary>Extract an anti-join trigger and the captured target noun.</summary>
    AntiJoinMention? ExtractAntiJoin(string question);

    /// <summary>Extract a text-search trigger and the captured noun ("containing electricity").</summary>
    TextSearchMention? ExtractTextSearch(string question);

    /// <summary>Extract a numeric range ("between 5000 and 10000" / "more than 100000").</summary>
    NumericRange? ExtractRange(string question);

    /// <summary>Extract negation cues ("not in X" / "except Y" / "excluding Z").</summary>
    NegationMention? ExtractNegation(string question);

    /// <summary>Extract a time-series granularity ("by month" / "weekly" / "trend over time").</summary>
    TimeSeriesGranularity? ExtractTimeSeriesGranularity(string question);

    /// <summary>Extract a natural-key token from the question (e.g. "TKT-00050", "SA-100").
    /// Caller provides the catalog of (table, naturalKeyColumn, formatRegex) tuples.</summary>
    NaturalKeyTokenMention? ExtractNaturalKeyToken(string question, IReadOnlyList<NaturalKeyFormat> formats);

    /// <summary>Extract a lifecycle verb mention ("resolved", "issued", "paid") and return the
    /// implied date-role token the semantic layer maps it to.</summary>
    LifecycleVerbMention? ExtractLifecycleVerb(string question);

    /// <summary>True when the question starts with a knowledge-question verb ("what is X",
    /// "explain X", "ما هو X", "اشرح X") and binds a 1-3 token noun. Out parameter receives the
    /// captured term, empty when this returns false. Sourced from
    /// <c>linguistic-cues.json knowledgeQuestion.verbs</c> per locale.</summary>
    bool LooksLikeKnowledgeQuestion(string question, out string term);

    /// <summary>True when the question contains any aggregate-marker phrase ("average", "count",
    /// "how many", "متوسط", "عدد"). Sourced from <c>linguistic-cues.json aggregateMarkers</c> per
    /// locale.</summary>
    bool LooksLikeAggregateQuery(string question);
}

public enum CueKind { Absence, AllTime, Distinct, Negation, Comparison }

public sealed record TemporalSpan(string Label, string StartToken, string? EndToken, string Op);
public sealed record StatusMention(string Cue, string Column, string CanonicalValue);
public sealed record EntityMention(string Table, string MatchedSynonym, int Score);
public sealed record Superlative(string TriggerWord, int? Count, SuperlativeDirection Direction);
public enum SuperlativeDirection { Top, Bottom, MaxValue, MinValue }
public sealed record AntiJoinMention(string TriggerPhrase, string Noun);
public sealed record TextSearchMention(string TriggerPhrase, string Noun);
public sealed record NumericRange(decimal? Min, decimal? Max, string Op);
public sealed record NegationMention(string TriggerPhrase, int Position);
public sealed record TimeSeriesGranularity(string Bucket, string OriginalPhrase);   // Bucket = day/week/month/quarter/year
public sealed record NaturalKeyFormat(string Table, string NaturalKeyColumn, string FormatRegex);
public sealed record NaturalKeyTokenMention(string Table, string NaturalKeyColumn, string Token);
public sealed record LifecycleVerbMention(string Verb, string DateRoleHint);
