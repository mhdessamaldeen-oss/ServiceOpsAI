using System.Text.Json.Serialization;

namespace AISupportAnalysisPlatform.Models.AI
{
    public enum CopilotIntentKind
    {
        GeneralChat,
        ExternalToolQuery,
        DataQuery,
        Clarification,
        Unsupported
    }

    public enum CopilotDataSource
    {
        None,
        Database,
        KnowledgeBase,
        ExternalTool,
        Mixed
    }

    public enum CopilotVerificationStatus
    {
        Passed,
        NeedsClarification,
        Failed
    }

    public class CopilotQuestionContext
    {
        public string OriginalQuestion { get; set; } = "";
        public string NormalizedQuestion { get; set; } = "";
        public string Surface { get; set; } = "";
        public string Language { get; set; } = "en";
        public List<string> PreprocessingTrace { get; set; } = new();
        public Dictionary<string, string> SignalsFound { get; set; } = new();
        public string ReferenceToken { get; set; } = "";
        public bool HasEntityReference { get; set; }
        [JsonIgnore]
        public string TicketNumber
        {
            get => ReferenceToken;
            set => ReferenceToken = value ?? "";
        }
        [JsonIgnore]
        public bool HasTicketReference
        {
            get => HasEntityReference;
            set => HasEntityReference = value;
        }
        public bool LooksLikeGreeting { get; set; }
        public bool LooksLikeDataQuestion { get; set; }
        public bool LooksLikeGuidanceQuestion { get; set; }
        public bool LooksLikeExternalToolQuery { get; set; }
        public bool LooksMultiPart { get; set; }
        public bool LooksLikeComplexDataQuestion { get; set; }
        public bool IsFollowUpQuestion { get; set; }
        public bool IsPotentiallyUnsafe { get; set; }
        public string SearchText { get; set; } = "";
        public string ConversationSummary { get; set; } = "";
        public List<string> QueryParts { get; set; } = new();
        public CopilotConversationContext ConversationContext { get; set; } = new();
    }

    public class CopilotIntentDecision
    {
        public CopilotIntentKind Intent { get; set; } = CopilotIntentKind.GeneralChat;
        public CopilotDataSource PrimarySource { get; set; } = CopilotDataSource.None;
        public RoutingConfidence Confidence { get; set; } = RoutingConfidence.Medium;
        public string Reason { get; set; } = "";
        public string ToolName { get; set; } = "none";
        public string ToolQuery { get; set; } = "";
        public Dictionary<string, string> ToolParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public bool IsFallback { get; set; }
    }

    public class CopilotExecutionPlan
    {
        public CopilotIntentDecision Decision { get; set; } = new();
        public string Summary { get; set; } = "";
        public int? TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public string ToolName { get; set; } = "none";
        public string SearchText { get; set; } = "";
        public Dictionary<string, string> ToolParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CopilotDataSource> Sources { get; set; } = new();
        public List<CopilotExecutionStep> PlanSteps { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? TicketQueryPlan { get; set; }
        public CopilotDataIntentPlan? DataIntentPlan { get; set; }
        public List<CopilotStructuredSubPlan> StructuredSubPlans { get; set; } = new();
        public List<CopilotExecutionPlan> SubTasks { get; set; } = new();
        /// <summary>Phase 4: shape contract this plan must satisfy. Null = no per-child shape requirements.</summary>
        public CopilotRequiredAnswerShape? RequiredShape { get; set; }
        /// <summary>Source text of the sub-request that produced this plan (for trace messages).</summary>
        public string SubRequestText { get; set; } = "";
    }

    public class CopilotExecutionResult
    {
        public string Answer { get; set; } = "";
        public string Notes { get; set; } = "";
        public ResponseMode ResponseMode { get; set; } = ResponseMode.Conversational;
        public EvidenceStrength EvidenceStrength { get; set; } = EvidenceStrength.Weak;
        public double GroundingScore { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = "";
        public string UsedTool { get; set; } = "none";
        public string Summary { get; set; } = "";
        public int? ResultCount { get; set; }
        public string? TechnicalData { get; set; }
        public List<string> SuggestedPrompts { get; set; } = new();
        public List<CopilotTicketCitation> SimilarTickets { get; set; } = new();
        public List<KnowledgeBaseChunkMatch> KnowledgeMatches { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? DynamicQueryPlan { get; set; }
        public AdminCopilotActionPlan? ActionPlan { get; set; }
        public List<AdminCopilotTicketQueryRow> DynamicTicketResults { get; set; } = new();
        public AdminCopilotDynamicTicketQueryExecution? StructuredResult { get; set; }
        public List<CopilotExecutionStep> ExecutionSteps { get; set; } = new();
        public List<string> StructuredColumns { get; set; } = new();
        public List<AdminCopilotStructuredResultRow> StructuredRows { get; set; } = new();
        public List<CopilotStructuredSubResult> StructuredQueryResults { get; set; } = new();
        public List<CopilotExecutionResult> SubResults { get; set; } = new();
        public CopilotExecutionRoute ExecutionRoute { get; set; } = CopilotExecutionRoute.General;
        public bool IsDeterministicEvidenceAnswer { get; set; }
    }

    public class CopilotVerificationResult
    {
        public CopilotVerificationStatus Status { get; set; } = CopilotVerificationStatus.Passed;
        public string Message { get; set; } = "";
        public EvidenceStrength EvidenceStrength { get; set; } = EvidenceStrength.Weak;
        public double GroundingScore { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = "";
    }

    public class CopilotGroundingResult
    {
        public bool IsGrounded { get; set; } = true;
        public double Confidence { get; set; } = 1.0;
        public string Analysis { get; set; } = "";
        public string EvidenceUsed { get; set; } = "";
        public List<string> HallucinationRisks { get; set; } = new();
    }

    public class CopilotToolResolution
    {
        public bool IsMatch { get; set; }
        public string ToolName { get; set; } = "none";
        public string SearchText { get; set; } = "";
        public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> MissingParameters { get; set; } = new();
        public RoutingConfidence Confidence { get; set; } = RoutingConfidence.Low;
        public string Reason { get; set; } = "";
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
    }

    public class CopilotToolParameterRequirement
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "text";
        public bool IsRequired { get; set; } = true;
        public List<string> Aliases { get; set; } = new();
    }

    public class CopilotToolParameterAnalysis
    {
        public string SearchText { get; set; } = "";
        public List<CopilotToolParameterRequirement> Requirements { get; set; } = new();
        public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> MissingParameters { get; set; } = new();
        public bool RequiresClarification => MissingParameters.Count > 0;
    }

    public class CopilotStructuredSubPlan
    {
        public string QueryText { get; set; } = "";
        public string Label { get; set; } = "";
        public AdminCopilotDynamicTicketQueryPlan QueryPlan { get; set; } = new();
    }

    public class CopilotStructuredSubResult
    {
        public string QueryText { get; set; } = "";
        public string Label { get; set; } = "";
        public AdminCopilotDynamicTicketQueryExecution Execution { get; set; } = new();
    }

    /// <summary>
    /// One independent part of a user request after decomposition.
    /// A single chat message can contain normal chat, data requests, and external tool calls together.
    /// </summary>
    public class CopilotSubRequest
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public CopilotIntentKind Kind { get; set; } = CopilotIntentKind.GeneralChat;
        public CopilotDataSource Source { get; set; } = CopilotDataSource.None;
        public RoutingConfidence Confidence { get; set; } = RoutingConfidence.Medium;
        public string Reason { get; set; } = "";
        public string ToolName { get; set; } = "none";
        public string ToolQuery { get; set; } = "";
        public Dictionary<string, string> ToolParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        /// <summary>
        /// Phase 4: independent answer-shape contract for this child workflow.
        /// Populated by the decomposer from catalog signals; validated by the executor.
        /// </summary>
        public CopilotRequiredAnswerShape? RequiredShape { get; set; }
    }

    /// <summary>
    /// Full decomposition result for a user prompt.
    /// Planning consumes this instead of guessing whether a request is "mixed" from one enum value.
    /// </summary>
    public class CopilotRequestDecomposition
    {
        public List<CopilotSubRequest> SubRequests { get; set; } = new();
    }

    /// <summary>
    /// Catalog-grounded data intent produced before query execution.
    /// It names the requested operation, entities, fields, filters, joins, sorting, and output shape.
    /// </summary>
    public class CopilotDataIntentPlan
    {
        public string Operation { get; set; } = "list";
        public string OutputShape { get; set; } = "table";
        public string PrimaryEntity { get; set; } = "";
        public List<string> Entities { get; set; } = new();
        public List<string> Fields { get; set; } = new();
        public List<CopilotDataFilterPlan> Filters { get; set; } = new();
        public List<CopilotDataFilterPlan> HavingFilters { get; set; } = new();
        public List<CopilotDataSortPlan> Sorts { get; set; } = new();
        public List<string> GroupBy { get; set; } = new();
        public List<CopilotDataAggregationPlan> Aggregations { get; set; } = new();
        public List<CopilotDataJoinPlan> Joins { get; set; } = new();
        public int? Limit { get; set; }
        public bool UseDistinct { get; set; }
        public string Explanation { get; set; } = "";
        public CopilotPlannerSource PlannerSource { get; set; } = CopilotPlannerSource.Unspecified;
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public List<string> ValidationMessages { get; set; } = new();
        public CopilotDataPlanReasoning Reasoning { get; set; } = new();

        // Elite: Decomposition support
        public List<CopilotDataIntentPlan> SubPlans { get; set; } = new();
        public bool IsComposite => SubPlans.Count > 0;

        /// <summary>Phase 6: catalog-driven entity reference resolution outcome (works for any entity, not just tickets).</summary>
        public CopilotEntityReferenceResolution? EntityReference { get; set; }
    }

    public class CopilotDataPlanReasoning
    {
        public string TableExtraction { get; set; } = "";
        public string ProjectionExtraction { get; set; } = "";
        public string GroupingExtraction { get; set; } = "";
        public string AggregationExtraction { get; set; } = "";
        public string HavingExtraction { get; set; } = "";
        public string LimitExtraction { get; set; } = "";
        public string FilterExtraction { get; set; } = "";
        public string OrderingExtraction { get; set; } = "";
        public string FullPrompt { get; set; } = "";
        public string RawModelResponse { get; set; } = "";
    }

    public class CopilotDataFilterPlan
    {
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public string Operator { get; set; } = "equals";
        public object? Value { get; set; }
        public bool IsNegated { get; set; }
        public string ValueType { get; set; } = "Literal"; // Literal, Field
    }

    public class CopilotDataSortPlan
    {
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public SortDirection Direction { get; set; } = SortDirection.Desc;
    }

    public class CopilotDataAggregationPlan
    {
        public string Function { get; set; } = "count";
        public string Entity { get; set; } = "";
        public string Field { get; set; } = "";
        public string Alias { get; set; } = "";
    }

    public class CopilotDataJoinPlan
    {
        public string FromEntity { get; set; } = "";
        public string ToEntity { get; set; } = "";
        public string Relationship { get; set; } = "";
    }
}
