using System.Text.Json.Serialization;

namespace AISupportAnalysisPlatform.Models.AI
{
    public enum OutputShape
    {
        Unknown,
        ScalarCount,
        ScalarAverage,
        ScalarSum,
        ScalarMin,
        ScalarMax,
        List,
        GroupedList,
        Detail,
        Comparison,
        Trend
    }

    public enum AgenticIntentKind
    {
        GeneralSupport,
        DataQuery,
        Investigation,
        ExternalTool,
        KnowledgeMatch,
        Clarification,
        Unsupported,
        /// <summary>
        /// Vector-similarity search on tickets via <c>ISemanticSearchService</c>. Two seed paths:
        ///   1. The user names a specific ticket → "tickets like TCK-2026-00500" → call
        ///      <c>GetRelatedTicketsAsync(ticketId)</c>.
        ///   2. The user describes content semantically → "tickets about printer queue
        ///      refresh" → call <c>SearchSimilarTicketsByTextAsync(queryText)</c>.
        /// Bypasses SQL composition entirely. Embeddings are pre-computed by the
        /// <c>EmbeddingQueueService</c>, so this path is fast.
        /// </summary>
        SemanticSearch
    }

    public class AgenticChatRequest
    {
        public string Question { get; set; } = "";
        public List<CopilotChatMessage> History { get; set; } = new();

        /// <summary>Optional record id from the page the user opened the chat from (e.g. a
        /// ticket-detail page → ticket id, an order-detail page → order id). Domain-agnostic;
        /// the consumer app passes whichever id is contextually meaningful. Section 1.8 portability.</summary>
        public int? ContextRecordId { get; set; }

        /// <summary>Legacy alias for <see cref="ContextRecordId"/>. Kept for source-compatibility
        /// with callers that still set <c>ContextTicketId</c>; reads/writes flow through the
        /// generalized field. Marked obsolete to encourage migration.</summary>
        [Obsolete("Use ContextRecordId — domain-agnostic name. Will be removed in a future version.")]
        public int? ContextTicketId
        {
            get => ContextRecordId;
            set => ContextRecordId = value;
        }

        public string? TargetEntity { get; set; }
    }

    public class AgenticIntentPlan
    {
        public string StepDescription { get; set; } = "";
        public AgenticIntentKind Intent { get; set; } = AgenticIntentKind.GeneralSupport;

        /// <summary>The flat list of entities the question touches. There is NO single "primary"
        /// entity — every entry here is a peer. The SQL composer derives the FROM-root from
        /// projection density (which entity contributes the most output columns / aggregations);
        /// it does not privilege <c>Entities[0]</c> over the others.
        ///
        /// <para>Replaces the old <c>PrimaryEntity</c> string field (2026-05-08). The
        /// <see cref="PrimaryEntity"/> property below is now a derived view of
        /// <c>Entities[0]</c> for backward compatibility with the JSON schema, prompt rules,
        /// and existing callsites — but it carries no special weight in the architecture.</para></summary>
        public List<string> Entities { get; set; } = new();

        /// <summary>Backward-compat view of <see cref="Entities"/>[0]. The LLM's JSON output
        /// contract still emits <c>"PrimaryEntity": "..."</c> in many cases; we accept it via
        /// the setter (which appends to <c>Entities</c>) and project it out via the getter
        /// (which reads the first entry). New code should read/write <see cref="Entities"/>
        /// directly. The plan is to remove this property once all callsites have migrated.</summary>
        public string PrimaryEntity
        {
            get => Entities.Count > 0 ? Entities[0] : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (Entities.Count > 0) Entities.RemoveAt(0);
                    return;
                }
                if (Entities.Count == 0) Entities.Add(value);
                else Entities[0] = value;
            }
        }
        public string Action { get; set; } = "";
        public List<string> ExtractedKeywords { get; set; } = new();
        public string ToolName { get; set; } = "";
        public Dictionary<string, string> ToolParameters { get; set; } = new();
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public bool IsInvalid { get; set; }
        public string SqlQuery { get; set; } = "";
        public string Explanation { get; set; } = "";

        /// <summary>
        /// Set when a UPSTREAM system failure (LLM provider 503/429/exception, parse failure,
        /// SQL execution exception) prevented us from producing a real plan. The orchestrator
        /// short-circuits as soon as it sees this — no SQL execution, no formatter, no fake answer.
        /// The value is the verbatim error shown to the user and persisted in CopilotTraceHistories.ErrorMessage.
        /// Distinguishes "system broke" (red) from "user must clarify" (yellow).
        /// </summary>
        public string? SystemError { get; set; }
        
        // Catalog-driven SQL generation properties (Phase 1-3)
        public List<string> Fields { get; set; } = new();  // Projection fields
        public List<AgenticFilter> Filters { get; set; } = new();  // Where conditions
        public List<string> GroupBy { get; set; } = new();  // Group by fields
        public List<AgenticSort> Sorts { get; set; } = new();  // Sort order
        public List<AgenticJoin> Joins { get; set; } = new();  // Join definitions

        /// <summary>
        /// Multi-FK disambiguation hints. When an entity has multiple FKs to the same target
        /// (Tickets has 5 FKs to ApplicationUser: AssignedToUserId, CreatedByUserId,
        /// EscalatedToUserId, etc.) the question's verb ("created", "assigned", "escalated")
        /// signals which FK to use. The classifier emits one entry per disambiguation, and
        /// the JOIN builder prefers the named FK over its default first-FK pick.
        ///
        /// Empty by default. The JOIN builder also has a fuzzy-scoring fallback so this
        /// hint is optional — but explicit hints are unambiguous.
        /// </summary>
        public List<AgenticJoinHint> JoinHints { get; set; } = new();
        public List<AgenticAggregation> Aggregations { get; set; } = new();  // Aggregations
        public List<AgenticHavingFilter> Having { get; set; } = new();  // HAVING clauses for grouped/aggregated queries
        public List<AgenticCalculatedField> CalculatedFields { get; set; } = new();  // Derived columns (DATEDIFF, CASE, ROUND, percentages)
        public int? Limit { get; set; }  // Result limit
        /// <summary>Pagination offset (0-based row index to start from). Set by classifier
        /// when the user asks for "next page" / "rows 11-20" / etc. When non-null AND Limit
        /// is also set, the composer emits ORDER BY ... OFFSET N ROWS FETCH NEXT M ROWS ONLY
        /// and skips TOP. Requires an ORDER BY (composer uses CreatedAt DESC default if none).</summary>
        public int? Offset { get; set; }

        /// <summary>True when the question asks for DISTINCT rows ("show distinct browsers",
        /// "list unique categories"). Renders <c>SELECT DISTINCT</c> instead of plain SELECT.
        /// Mutually exclusive with Aggregations + GroupBy (those imply uniqueness already).</summary>
        public bool IsDistinct { get; set; }
        
        // Semantic resolution properties (Phase 1)
        public float ConfidenceScore { get; set; }  // Entity match confidence (0-1)
        public string OriginalQuestion { get; set; } = "";  // Original user question
        public string NormalizationApplied { get; set; } = "";  // Type of normalization (Pluralized, AliasMapped, etc.)
        
        // Temporal filtering (Phase 2)
        public TemporalFilter? Temporal { get; set; }  // Date/time range filter
        
        // Output shape (Phase 5)
        public OutputShape OutputShape { get; set; } = OutputShape.List;  // Expected result type
        
        // Compound query support (Phase 4)
        public List<AgenticIntentPlan>? SubQueries { get; set; }  // For multi-part queries
        public bool IsCompoundQuery => SubQueries?.Any() == true;
    }
    
    public class AgenticFilter
    {
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public string Operator { get; set; } = "equals";  // equals, contains, gt, lt, between, in, isnull
        public object? Value { get; set; }
        public bool IsNegated { get; set; }

        /// <summary>Column-to-column comparison: when set, the WHERE clause compares
        /// <see cref="Field"/> against ANOTHER column instead of <see cref="Value"/>. Use the
        /// same dotted-notation as projection (e.g. <c>"Tickets.DueDate"</c>). Empty/null →
        /// regular literal comparison via <see cref="Value"/>.
        /// Example: question <em>"tickets resolved AFTER their due date"</em> →
        /// <c>Filter{Field=ResolvedAt, Operator=gt, ValueColumn=DueDate}</c> →
        /// SQL <c>[t].[ResolvedAt] &gt; [t].[DueDate]</c>.</summary>
        public string? ValueColumn { get; set; }
    }
    
    public class AgenticJoin
    {
        public string FromEntity { get; set; } = "";
        public string ToEntity { get; set; } = "";
        public string Relationship { get; set; } = "";
        public List<string> ProjectionFields { get; set; } = new();
    }

    /// <summary>
    /// Names a specific FK column to use when joining to <see cref="ToEntity"/>. Resolves
    /// the multi-FK ambiguity (e.g. Tickets has 5 FKs to ApplicationUser) by saying "use
    /// THIS one, not the alphabetical default". Generic — works for any FK pair, M2M
    /// junctions are addressed via their JunctionTable name in the same field.
    /// </summary>
    public class AgenticJoinHint
    {
        /// <summary>The entity being joined TO (e.g. "ApplicationUser").</summary>
        public string ToEntity { get; set; } = "";

        /// <summary>The exact FK column name to use (e.g. "CreatedByUserId").</summary>
        public string ViaForeignKey { get; set; } = "";

        /// <summary>Override the default <c>LEFT JOIN</c>. Accepted values: <c>"inner"</c> /
        /// <c>"left"</c>. Default LEFT keeps the question's SUBJECT rows even when the JOIN
        /// target has no match. Use INNER when the user's question requires the relationship
        /// to exist (e.g. <em>"tickets that HAVE a parent ticket"</em>).</summary>
        public string JoinType { get; set; } = "left";
    }
    
    public class AgenticAggregation
    {
        public string Function { get; set; } = "";  // count, avg, sum, min, max
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public string Alias { get; set; } = "";

        /// <summary>Set by <c>AggregationTypeGuardStage</c> when AVG/SUM/MIN/MAX targets a
        /// string-typed column that MAY contain numeric values (e.g. EscalationLevel="1","2").
        /// SelectClauseBuilder then wraps the column in <c>TRY_CAST(... AS float)</c>; non-parseable
        /// rows yield NULL which AVG/SUM ignore. Default false (numeric columns don't need it).</summary>
        public bool TryCastNumeric { get; set; }
    }
    
    public class AgenticSort
    {
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public string Direction { get; set; } = "Desc";  // Asc or Desc
    }

    /// <summary>
    /// A derived column the planner wants in the output, expressed as a safe SQL expression
    /// using a whitelisted function from a small set: DATEDIFF, DATEPART, CASE, ROUND, ISNULL, COALESCE,
    /// plus arithmetic on column references. The builder validates the function name against the whitelist
    /// and only allows column-reference arguments (no literals, no nested function calls outside the whitelist).
    /// </summary>
    public class AgenticCalculatedField
    {
        /// <summary>Column alias in the result set (e.g. "ResolutionHours").</summary>
        public string Alias { get; set; } = "";

        /// <summary>Whitelisted function: "datediff", "datepart", "case", "round", "isnull", "coalesce", "percent".</summary>
        public string Function { get; set; } = "";

        /// <summary>Function-specific arguments. Examples below.
        /// - datediff: ["hour", "CreatedAt", "ResolvedAt"] or ["day", "CreatedAt", "ResolvedAt"]
        /// - datepart: ["year", "CreatedAt"]
        /// - round:    ["FieldName", "2"]
        /// - isnull:   ["FieldName", "0"] or ["FieldName", "(no value)"]
        /// - coalesce: ["FieldA", "FieldB", "..."]
        /// - percent:  ["NumeratorField", "DenominatorField"]  (rendered as ROUND(100.0 * a / NULLIF(b,0), 2))
        /// - case:     ["WHEN", "FieldName = 'Open'", "THEN", "1", "WHEN", "FieldName = 'Closed'", "THEN", "0", "ELSE", "0"]
        /// </summary>
        public List<string> Args { get; set; } = new();
    }

    public class AgenticHavingFilter
    {
        public string AggregationAlias { get; set; } = "";  // Alias of the aggregation being filtered (e.g. "TicketCount")
        public string Function { get; set; } = "";          // count, sum, avg, min, max — must match an aggregation
        public string Field { get; set; } = "";             // Field the aggregation is over
        public string Operator { get; set; } = "gt";        // gt, lt, gte, lte, equals, between
        public object? Value { get; set; }
        public bool IsNegated { get; set; }
    }

    public class AgenticTaskDecomposition
    {
        public List<AgenticIntentPlan> Steps { get; set; } = new();
    }

    public class AgenticExecutionResult
    {
        public string FinalAnswer { get; set; } = "";
        public string InternalLogs { get; set; } = "";
        public bool IsSuccess { get; set; }
        public AgenticIntentPlan ExecutedPlan { get; set; } = new();
        public string RawData { get; set; } = "";
    }
}
