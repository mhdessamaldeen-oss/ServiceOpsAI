namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Defines the primary operational modes for the Admin Copilot.
    /// Addresses USER request to move away from static text/strings.
    /// </summary>
    public enum CopilotChatMode
    {
        /// <summary>
        /// General conversational support without direct data access.
        /// </summary>
        GeneralSupport,

        /// <summary>
        /// RAG-based support using Knowledge Base and similar tickets.
        /// </summary>
        KnowledgeBase,

        /// <summary>
        /// Direct application data retrieval (counts, summaries).
        /// </summary>
        AppDataAnalysis,

        /// <summary>
        /// Advanced dynamic queries (filters, sorting, comparative).
        /// </summary>
        DynamicTicketQuery,

        /// <summary>
        /// External tool execution (e.g., Weather).
        /// </summary>
        ExternalUtility,

        /// <summary>
        /// Perform system actions (Assign, Update Status, Comment).
        /// </summary>
        PlatformAction
    }

    /// <summary>
    /// Confidence levels for LLM-driven routing decisions.
    /// </summary>
    public enum RoutingConfidence
    {
        Low,
        Medium,
        High,
        VeryHigh
    }

    /// <summary>
    /// Reliability levels for generated answers based on source material.
    /// </summary>
    public enum EvidenceStrength
    {
        General,
        Weak,
        Moderate,
        High,
        Definitive
    }

    /// <summary>
    /// Response presentation modes.
    /// </summary>
    public enum ResponseMode
    {
        Conversational,
        KnowledgeMatch,
        StructuredTable,
        MetricSummary,
        DetailedReport,
        VisualChart
    }

    /// <summary>
    /// Contextual roles for conversation messages.
    /// </summary>
    public enum ChatMessageRole
    {
        System,
        User,
        Assistant,
        Tool
    }

    /// <summary>
    /// Categorization labels for ticket linguistic profiles.
    /// </summary>
    public enum TicketLanguageLabel
    {
        English,
        Arabic,
        Mixed,
        Unknown
    }

    /// <summary>
    /// Sorting orientation.
    /// </summary>
    public enum SortDirection
    {
        Asc,
        Desc
    }

    /// <summary>
    /// Intended operation for dynamic queries.
    /// </summary>
    public enum DynamicQueryIntent
    {
        Unspecified,
        List,
        Count,
        Breakdown,
        Summarize,
        Trend,
        GroupBy,
        Detail,
        /// <summary>Standalone scalar aggregation (avg/sum/max/min) with no group-by.</summary>
        Scalar
    }

    /// <summary>
    /// Standardized system messages for the Copilot.
    /// </summary>
    public enum CopilotSystemMessage
    {
        None,
        QuestionRequired,
        TroubleGeneratingResponse,
        ContextAnalyzedButTrouble,
        NoContextDetected,
        ExecutionError
    }

    /// <summary>
    /// Logical layers of the Copilot execution pipeline.
    /// </summary>
    public enum CopilotExecutionLayer
    {
        Context,
        Router,
        DataPlanning,
        DataExecution,
        Executor,
        Complete
    }

    /// <summary>
    /// Status indicators for execution steps.
    /// </summary>
    public enum CopilotStepStatus
    {
        Ok,
        Warn,
        Error,
        Skip
    }

    /// <summary>
    /// Relative date filtering options for ticket queries.
    /// </summary>
    public enum TicketDateRange
    {
        Any,
        Today,
        Last7Days,
        Last30Days,
        ThisMonth
    }

    /// <summary>
    /// Specific system actions the Copilot can plan.
    /// </summary>
    public enum PlatformActionIntent
    {
        None,
        AssignTicket,
        UpdateStatus,
        UpdatePriority,
        AddComment,
        Escalate,
        Resolve
    }

    public enum CopilotPlannerSource
    {
        Unspecified,
        Deterministic,
        Model,
        Fallback
    }

    public enum CopilotExecutionRoute
    {
        General,
        Pipeline,
        CatalogSql,
        Composite
    }

    public enum CopilotPlannerSemanticSignal
    {
        DenormalizedTicketView,
        AttachmentEvidence,
        CommentEvidence,
        CommentCountShape,
        HistoryEvidence,
        AssessmentEvidence,
        TraceEvidence,
        SlaPerformance,
        TicketListIntent,
        ResolutionPerformance,
        TicketCore,
        AverageAggregation,
        SumAggregation,
        CountAggregation,
        RankMax,
        GroupTrigger,
        GroupRanking,
        GroupDimension,
        UserRole,
        CategoryDimension,
        TicketIdentifierDimension,
        EntityDimension,
        StatusDimension,
        PriorityDimension,
        SourceDimension,
        AssigneeDimension,
        ResolverDimension,
        ReporterDimension,
        NewestSort,
        OldestSort,
        SlaProjection,
        ResolutionProjection,
        AssignmentProjection,
        TicketReferenceProjection,
        OpenNegativeStatus,
        OpenStatus,
        ResolvedStatus,
        ClosedStatus,
        SlaBreached,
        Unassigned,
        Overdue,
        ActiveBoolean,
        InactiveBoolean,
        UpdatedAfterResolved,
        NoComments,
        NoAttachments,
        SameReporterResolver,
        RankingDescending,
        RankingAscending,
        RankingSingleResult,
        Today,
        Yesterday,
        CurrentWeek,
        CurrentMonth,
        DeterministicListSignal,
        EntityContext,
        MetricShape,
        GroupingRequired,
        FilterSignal,
        ExplicitAnalyticsRequest
    }

    public enum CopilotCatalogEntityRole
    {
        TicketRecord,
        DenormalizedTicketRecord,
        EntityPerformance,
        TicketAttachmentEvidence,
        TicketCommentEvidence,
        TicketHistoryEvidence,
        CopilotAssessment,
        CopilotTraceHistory,
        UserIdentity
    }

    public enum CopilotKnownLookupValueRole
    {
        Open,
        Closed,
        Resolved
    }
}
