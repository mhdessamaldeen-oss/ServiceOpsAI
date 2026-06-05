namespace AnalystAgent.Tests.Grounding;

using System.Collections.Generic;
using AnalystAgent.Grounding;

/// <summary>
/// Minimal <see cref="ILinguisticRegistry"/> stub for ValueLinker integration tests. Only
/// <see cref="GetVerbContextCues"/> is exercised by those tests; it returns the English closed-class
/// verb-context sets (byte-identical to the production English fallback) so the inline-enum pass behaves
/// exactly as it did before the cue sets were externalised. Every other member returns an empty / null
/// result — none are reached by the ValueLinker paths under test.
/// </summary>
internal sealed class FakeLinguisticRegistry : ILinguisticRegistry
{
    private static readonly IReadOnlySet<string> Prepositions =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "in", "by", "on", "during", "over", "since", "between", "within", "before", "after", "from" };

    private static readonly IReadOnlySet<string> TimeCues =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "this", "last", "next", "so", "today", "yesterday", "tomorrow", "ago", "ytd", "now", "currently",
          "recently", "lately", "yet", "already", "still", "when", "while", "until", "till" };

    public static FakeLinguisticRegistry WithEnglishVerbCues() => new();

    public VerbContextCues GetVerbContextCues(string languageCode) => new(Prepositions, TimeCues);

    // Unused by the ValueLinker tests — safe defaults.
    public bool HasCue(string question, CueKind kind) => false;
    public IReadOnlyList<TemporalSpan> ExtractTemporal(string question) => System.Array.Empty<TemporalSpan>();
    public IReadOnlyList<StatusMention> ExtractStatus(string question) => System.Array.Empty<StatusMention>();
    public IReadOnlyList<EntityMention> ExtractEntityMentions(string question) => System.Array.Empty<EntityMention>();
    public Superlative? ExtractSuperlative(string question) => null;
    public AntiJoinMention? ExtractAntiJoin(string question) => null;
    public TextSearchMention? ExtractTextSearch(string question) => null;
    public NumericRange? ExtractRange(string question) => null;
    public NegationMention? ExtractNegation(string question) => null;
    public TimeSeriesGranularity? ExtractTimeSeriesGranularity(string question) => null;
    public NaturalKeyTokenMention? ExtractNaturalKeyToken(string question, IReadOnlyList<NaturalKeyFormat> formats) => null;
    public LifecycleVerbMention? ExtractLifecycleVerb(string question) => null;
    public bool LooksLikeKnowledgeQuestion(string question, out string term) { term = ""; return false; }
    public bool LooksLikeAggregateQuery(string question) => false;
}
