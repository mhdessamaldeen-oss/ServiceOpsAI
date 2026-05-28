namespace SuperAdminCopilot.Configuration;

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
