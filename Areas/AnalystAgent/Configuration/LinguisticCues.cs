namespace AnalystAgent.Configuration;

using System.Text.Json.Serialization;

/// <summary>
/// Top-level container for all linguistic cues used by the question-understanding pipeline.
/// Locale-keyed (en, ar, …) so a new language requires only a JSON edit, not code. Loaded from
/// <c>linguistic-cues.json</c> at startup and exposed via <see cref="ILinguisticCuesProvider"/>.
/// </summary>
/// <remarks>
/// <para>This config replaces the hardcoded regex sets that were previously embedded across
/// SpecRepair phases (Negation / Range / SpecificYearMonth / InjectTemporal / Strip*All* /
/// ApplyDerivedMetricHint / etc.). The principle: <i>linguistic patterns are data, not code</i>.
/// Operators add a new dialect or synonym by editing the JSON; pipeline behaviour follows
/// without recompilation.</para>
///
/// <para>Versioning: bump <see cref="Version"/> when the schema shape changes. The
/// <c>SchemaConfigValidator</c> (Day 4) refuses to start if the file version is unsupported.</para>
/// </remarks>
public sealed class LinguisticCues
{
    /// <summary>Schema version. Currently 1. Bumped on breaking shape changes.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Map of locale code (<c>"en"</c>, <c>"ar"</c>, …) to the cues for that locale. Every locale
    /// MUST define every section (even if empty arrays) so consumers can rely on non-null lists.
    /// </summary>
    public Dictionary<string, LocaleCues> Locales { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// All cues for a single locale (typically "en" or "ar"). Every section is optional at the JSON
/// level (missing → empty list) but the validator warns on completely-empty locales since they
/// would silently disable that language.
/// </summary>
public sealed class LocaleCues
{
    /// <summary>Temporal range patterns (<c>"today"</c>, <c>"this week"</c>, <c>"Q1"</c>, …).</summary>
    public List<TemporalCue> Temporal { get; set; } = new();

    /// <summary>Absence cues (<c>"no email"</c>, <c>"without comments"</c>) — drive IS NULL filter rewrites.</summary>
    public List<string> Absence { get; set; } = new();

    /// <summary>All-time cues (<c>"ever"</c>, <c>"of all time"</c>) — strip default temporal filter.</summary>
    public List<string> AllTime { get; set; } = new();

    /// <summary>Distinctness cues (<c>"how many distinct"</c>, <c>"unique"</c>) — force COUNT(DISTINCT col).</summary>
    public List<string> Distinct { get; set; } = new();

    /// <summary>Negation cues (<c>"not in"</c>, <c>"except"</c>, <c>"excluding"</c>) — flip filter polarity.</summary>
    public List<string> Negation { get; set; } = new();

    /// <summary>
    /// Recency cues for TOPN routing. DESC = "newest first" (most recent / latest); ASC = "oldest first".
    /// </summary>
    public RecencyCues Recency { get; set; } = new();

    /// <summary>Superlative cues for aggregate-vs-list shape detection.</summary>
    public SuperlativeCues Superlative { get; set; } = new();

    /// <summary>Numeric range cues. Each entry is a regex with one or two capture groups for the numeric values.</summary>
    public RangeCues Range { get; set; } = new();

    /// <summary>
    /// Aggregate-verb vocabulary (count / sum / total / avg / mean / max / highest / min / oldest / …).
    /// Each entry maps a question-text token to the SQL aggregate function the planner should emit.
    /// Used by ForceNonCountAggregationPhase + ForceAggregationOnCountQuestionPhase.
    /// </summary>
    public List<AggregateVerbCue> AggregateVerbs { get; set; } = new();

    /// <summary>
    /// Possessive / definite / plain conjunction markers used to anchor "X and their Y" root inference.
    /// Tiered: possessive markers win over definite, which win over plain. Used by InferRootFromQuestionPhase.
    /// </summary>
    public PossessiveCues Possessive { get; set; } = new();

    /// <summary>
    /// Intent-verb vocabulary for question shape detection (count / list / sum). Drives the
    /// ArabicQuestionDispatchPhase (and its English equivalent if added later).
    /// </summary>
    public IntentVerbCues IntentVerbs { get; set; } = new();

    /// <summary>
    /// Vocabulary → canonical-value map for lifecycle / severity words.
    /// e.g. "النشط" → (column=Status, value=Active). Used by ArabicQuestionDispatchPhase.
    /// </summary>
    public List<StatusValueCue> StatusValues { get; set; } = new();

    /// <summary>
    /// Anti-join trigger phrases ("without any X", "with no Y", "بدون X", "ليس لديهم X").
    /// Used by InjectAntiJoinFromQuestionPhase.
    /// </summary>
    public List<string> AntiJoin { get; set; } = new();

    /// <summary>
    /// Ordering-intent markers (" first ", " ordered ", " sorted ", " list ", " show ").
    /// Used by ForceNonCountAggregationPhase + ForceAggregationOnCountQuestionPhase to
    /// distinguish "newest tickets first" (LIST) from "newest ticket date" (MAX). Markers
    /// are space-padded substring tokens so a JSON entry of <c>" first "</c> only matches
    /// the standalone word.
    /// </summary>
    public List<string> OrderingIntent { get; set; } = new();

    /// <summary>
    /// Compare-shape vocabulary used by VerifiedQueryStore to gate compare-shape verified
    /// queries (don't return a "compare A vs B" template for a plain list query). Each
    /// entry is a regex fragment compiled into a single alternation. Phrases without regex
    /// metacharacters are wrapped with <c>\b</c> word boundaries.
    /// </summary>
    public List<string> CompareMarkers { get; set; } = new();

    /// <summary>
    /// Text-search trigger regexes. Each entry MUST contain a named capture group
    /// <c>(?&lt;noun&gt;...)</c> for the search term. Used by InjectTextSearchFilterPhase
    /// to detect "X containing Y" / "X about Y" / "X mentioning Y" / Arabic equivalents
    /// and extract the noun (Y). The phase then emits a single FilterSpec with
    /// <c>op=text_search</c> over the entity's <c>searchableColumns</c>.
    /// </summary>
    public List<string> TextSearchTriggers { get; set; } = new();

    /// <summary>
    /// Knowledge-question verbs ("what is", "explain", "tell me about", "ما هو", "اشرح"). When the
    /// question STARTS with one of these and is followed by a 1-3 token noun, the request is a
    /// knowledge / glossary lookup rather than a data query. Consumed by KnowledgeMatchHandler via
    /// ILinguisticRegistry.LooksLikeKnowledgeQuestion.
    /// </summary>
    public KnowledgeQuestionCues KnowledgeQuestion { get; set; } = new();

    /// <summary>
    /// Aggregate-marker phrases ("average", "count", "how many", "متوسط", "عدد"). When ANY appears
    /// in the question, the request is a data query — used by KnowledgeMatchHandler to refuse
    /// knowledge-routing and by SqlIntentGuard to confirm SQL intent. Consumed via
    /// ILinguisticRegistry.LooksLikeAggregateQuery.
    /// </summary>
    public List<string> AggregateMarkers { get; set; } = new();

    /// <summary>
    /// Date-column NAME tokens (case-insensitive substring match against a GROUP BY column
    /// identifier) used by ChartTypeSuggester to pick a time-series (line) chart. Advisory UI hint
    /// only — never affects SQL or answers. e.g. "date", "createdat", "month", "year".
    /// </summary>
    public List<string> DateColumnTokens { get; set; } = new();
}

public sealed class KnowledgeQuestionCues
{
    /// <summary>Verb / verb-phrase prefixes that introduce a knowledge question.</summary>
    public List<string> Verbs { get; set; } = new();
}

/// <summary>One aggregate-verb cue: a question-text trigger → the SQL aggregate to force.</summary>
public sealed class AggregateVerbCue
{
    /// <summary>Trigger phrase (case-insensitive contains). Trailing spaces preserved as a word boundary if present in source.</summary>
    public string Verb { get; set; } = "";

    /// <summary>SQL aggregate function: COUNT / SUM / AVG / MAX / MIN.</summary>
    public string Function { get; set; } = "";

    /// <summary>
    /// True when this verb is ambiguous between aggregation and ordering (e.g. "newest" can mean
    /// MAX(date) OR ORDER BY date DESC). When set, ForceNonCountAggregationPhase skips it if the
    /// question also contains an ordering marker (first / sorted / list / show).
    /// </summary>
    public bool AmbiguousWithOrderBy { get; set; } = false;
}

public sealed class PossessiveCues
{
    /// <summary>Strongest: "X and their Y" / "X with their Y" / "X and its Y" / "X with its Y".</summary>
    public List<string> Possessive { get; set; } = new();
    /// <summary>Medium: "X and the Y" / "X with the Y".</summary>
    public List<string> Definite { get; set; } = new();
    /// <summary>Weakest fallback: "X and Y" / "X with Y".</summary>
    public List<string> Plain { get; set; } = new();
}

public sealed class IntentVerbCues
{
    /// <summary>Count-form triggers (regex). e.g. "كم\\s*عدد", "\\bhow\\s+many\\b".</summary>
    public List<string> Count { get; set; } = new();
    /// <summary>List-form triggers (regex). e.g. "اظهر|اعرض|show|list".</summary>
    public List<string> List { get; set; } = new();
    /// <summary>Sum-form triggers (regex). e.g. "إجمالي|مجموع|total\\s+of".</summary>
    public List<string> Sum { get; set; } = new();
}

/// <summary>One status / severity / payment-state cue: a question-text trigger → (column, canonical value).</summary>
public sealed class StatusValueCue
{
    /// <summary>Trigger phrase. Substring match (case-insensitive). Inflection-tolerant: "النشط" matches "النشطين"/"النشطون"/"النشطة".</summary>
    public string Cue { get; set; } = "";

    /// <summary>Target column on the root entity (Status / Severity / State).</summary>
    public string Column { get; set; } = "";

    /// <summary>Canonical English value to inject (Active / Open / Critical / …).</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// One temporal cue. Either <see cref="EndToken"/> is non-null (half-open range
/// <c>[Start, End)</c>) or it's null (single-bound filter using <see cref="Op"/>).
/// </summary>
public sealed class TemporalCue
{
    /// <summary>
    /// Regex pattern (case-insensitive). May use one capture group for "last N units" patterns
    /// (e.g. <c>last 30 days</c>) — the captured number is substituted into <see cref="Start"/>
    /// via <c>{0}</c> placeholder.
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>Compiler temporal token for the range start (e.g. <c>@week_start</c>, <c>@days:-{0}</c>).</summary>
    public string Start { get; set; } = "";

    /// <summary>Compiler temporal token for the range end. Null = single-bound filter.</summary>
    public string? End { get; set; }

    /// <summary>Single-bound op (gte / gt / lte / lt). Ignored when <see cref="End"/> is non-null.</summary>
    public string Op { get; set; } = "gte";

    /// <summary>Friendly label used as the PeriodSpec label when emitting UNION-ALL multi-period spec.</summary>
    public string Label { get; set; } = "";
}

public sealed class RecencyCues
{
    /// <summary>Cues that imply DESC ordering by date (most recent / newest / latest).</summary>
    public List<string> Desc { get; set; } = new();
    /// <summary>Cues that imply ASC ordering by date (oldest / earliest / first).</summary>
    public List<string> Asc { get; set; } = new();
}

public sealed class SuperlativeCues
{
    /// <summary>MAX-direction cues (largest, biggest, highest, longest).</summary>
    public List<string> Max { get; set; } = new();
    /// <summary>MIN-direction cues (smallest, lowest, shortest).</summary>
    public List<string> Min { get; set; } = new();
    /// <summary>TOP-N cues (top, best, highest-N).</summary>
    public List<string> Top { get; set; } = new();
    /// <summary>BOTTOM-N cues (bottom, worst, least, fewest).</summary>
    public List<string> Bottom { get; set; } = new();
}

public sealed class RangeCues
{
    /// <summary>Between-pattern regex with TWO numeric capture groups (e.g. <c>between (\d+) and (\d+)</c>).</summary>
    public List<string> Between { get; set; } = new();
    /// <summary>Greater-than (gt) pattern regexes, single capture group.</summary>
    public List<string> Gt { get; set; } = new();
    /// <summary>Greater-than-or-equal (gte) pattern regexes.</summary>
    public List<string> Gte { get; set; } = new();
    /// <summary>Less-than (lt).</summary>
    public List<string> Lt { get; set; } = new();
    /// <summary>Less-than-or-equal (lte).</summary>
    public List<string> Lte { get; set; } = new();
    /// <summary>Equality (= N) patterns.</summary>
    public List<string> Eq { get; set; } = new();
}
