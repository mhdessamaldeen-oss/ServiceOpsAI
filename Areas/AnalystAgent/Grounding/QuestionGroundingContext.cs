namespace AnalystAgent.Grounding;

/// <summary>
/// Pre-LLM grounding context — populated by <see cref="IQuestionGrounder"/> before any LLM call,
/// then included in the SpecExtractor prompt as ground truth the LLM must use (not guess).
///
/// <para>This is the centrepiece of the principled redesign: instead of letting the LLM emit
/// a QuerySpec from scratch and then patching its mistakes with 24 SpecRepair phases, we
/// pre-resolve everything we can deterministically (values, dates, natural keys, intent,
/// cardinality) and TELL the LLM the answers. The LLM only has to wire them into the spec
/// shape.</para>
///
/// <para>Conceptually maps to the "Schema Linking + Value Linking + Shape Classification"
/// stages in DIN-SQL / CHESS / E-SQL / ValueNet.</para>
/// </summary>
public sealed class QuestionGroundingContext
{
    /// <summary>
    /// The original user question (post-strip of "-- requested columns: …" annotations).
    /// </summary>
    public string Question { get; init; } = "";

    /// <summary>
    /// Intent shape classified by <c>IPromptShapeClassifier</c> — COUNT, TOPN, AGGREGATE,
    /// TIMESERIES, COMPARE, LOOKUP, JOIN, or FILTER. The LLM is told which shape to emit and
    /// the prompt is augmented with shape-specific examples.
    /// </summary>
    public string PromptShape { get; init; } = "";

    /// <summary>
    /// Schema-linked tables: the candidate tables (root + neighbors) the planner should
    /// consider. Populated by retriever + keyword fallback + neighbor expansion.
    /// </summary>
    public IReadOnlyList<string> LinkedTables { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Value-linked bindings: question tokens that match real DB content. Each entry is a
    /// "you should filter <code>Table.Column = 'Value'</code>" recommendation. The LLM is
    /// told to use these verbatim in the WHERE clause.
    /// </summary>
    public IReadOnlyList<ValueLinkBinding> LinkedValues { get; init; } = System.Array.Empty<ValueLinkBinding>();

    /// <summary>
    /// Resolved temporal slots: each is a (column, start, end?, op) tuple where start/end are
    /// the compiler's <c>@</c>-tokens (<c>@week_start</c>, <c>@q1_start</c>, <c>@days:-30</c>).
    /// Multi-period questions ("Q1 and Q2") produce multiple bindings — the LLM is told to
    /// emit them as PeriodComparisons.
    /// </summary>
    public IReadOnlyList<TemporalBinding> LinkedTemporal { get; init; } = System.Array.Empty<TemporalBinding>();

    /// <summary>
    /// Natural-key bindings: tokens in the question matching an entity's natural-key format
    /// (TKT-00050, BIL-12345). The LLM is told the entity AND the natural-key value AND the
    /// column to filter on. This also fixes wrong-root cases where the LLM might pick the
    /// adjacent lookup (TicketStatuses) instead of the fact (Tickets).
    /// </summary>
    public IReadOnlyList<NaturalKeyBinding> LinkedNaturalKeys { get; init; } = System.Array.Empty<NaturalKeyBinding>();

    /// <summary>
    /// True if the question contains an "all time" / "ever" / "in history" cue. The LLM is told
    /// NOT to add a default date filter; the user wants the full history.
    /// </summary>
    public bool IsAllTimeIntent { get; init; }

    /// <summary>
    /// True if the question contains a distinctness cue ("how many distinct/unique X"). The LLM
    /// is told to emit COUNT(DISTINCT col), not COUNT(*) / GROUP BY.
    /// </summary>
    public bool IsDistinctCountIntent { get; init; }

    /// <summary>
    /// Verb-implied date column role ("created" / "resolved" / "started" / "issued" / etc.) when
    /// the question carries a lifecycle verb. Empty string = no specific role implied. The LLM
    /// is told which date column to filter / order by.
    /// </summary>
    public string DateRoleHint { get; init; } = "";

    /// <summary>
    /// Derived-metric column hints: maps a metric keyword in the question ("age", "resolution
    /// time", "duration", "MTTR", "revenue") to the canonical T-SQL expression the LLM should
    /// use as the aggregation target. Empty when no metric keyword was found. The LLM is told
    /// "for METRIC X, use expression Y" so it stops guessing wrong columns (the 7B model
    /// frequently picks AffectedUsersCount when asked about "age" or "resolution time").
    /// </summary>
    public IReadOnlyList<DerivedMetricHint> DerivedMetricHints { get; init; } = System.Array.Empty<DerivedMetricHint>();

    /// <summary>
    /// Time-series bucket granularity when the question groups over time ("per month", "monthly",
    /// "per day", "by year") — one of day/week/month/quarter/year, else empty. The LLM is told to
    /// bucket with the matching expression (e.g. month → FORMAT(date,'yyyy-MM')) in SELECT + GROUP BY.
    /// </summary>
    public string TimeBucketHint { get; init; } = "";

    /// <summary>
    /// Empty-grounding-context singleton. Used when grounding fails or is disabled — the
    /// SpecExtractor falls back to the legacy ungrounded prompt.
    /// </summary>
    public static QuestionGroundingContext Empty { get; } = new();
}

/// <summary>
/// One derived-metric hint. Maps a recognised metric keyword in the question to the canonical
/// SQL expression the LLM should aggregate over. Example: question contains "ticket age" →
/// MetricKeyword="age", Expression="DATEDIFF(DAY, Tickets.CreatedAt, GETDATE())",
/// AggregationFunction="AVG" or "MAX" depending on cue ("max age" vs "average age").
/// </summary>
public sealed record DerivedMetricHint(
    string MetricKeyword,
    string Expression,
    string PreferredFunction);

/// <summary>
/// One value-linking binding: a token in the question matched a real value in a DB column.
/// The LLM is told <c>WHERE [Table].[Column] = '[Value]'</c> is required.
/// </summary>
public sealed record ValueLinkBinding(
    string Table,
    string Column,
    string Value,
    /// <summary>Original token in the question that matched. Useful for traceability.</summary>
    string MatchedToken,
    /// <summary>Optional confidence score [0..1]. Exact match = 1.0; fuzzy = lower.</summary>
    float Confidence = 1.0f);

/// <summary>
/// One resolved temporal slot. Maps a phrase in the question ("this week", "Q1", "last 30 days")
/// to a half-open date range using the compiler's <c>@</c>-token vocabulary.
/// </summary>
public sealed record TemporalBinding(
    string Label,
    string StartToken,
    string? EndToken,
    /// <summary>Operator for single-bound filters (gte / lt / etc.). Range filters always use gte/lt.</summary>
    string Op = "gte");

/// <summary>
/// One natural-key resolution: token "TKT-00050" matched <c>Tickets.TicketNumber</c>.
/// </summary>
public sealed record NaturalKeyBinding(
    string Entity,
    string Table,
    string Column,
    string Value);
