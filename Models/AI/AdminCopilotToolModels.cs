namespace ServiceOpsAI.Models.AI
{
    public class AdminCopilotExecutionDetails
    {
        public string DetectedIntent { get; set; } = "";
        public string RouteReason { get; set; } = "";
        public RoutingConfidence PlannerConfidence { get; set; } = RoutingConfidence.Medium;
        public string SearchQuery { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? LastTechnicalData { get; set; }
        public int? ResultCount { get; set; }
        public string? Brain { get; set; }
        public int? ModelCapacity { get; set; }
        public int? PromptLength { get; set; }
        public bool IsTruncated { get; set; }
        public List<CopilotExecutionStep> Steps { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? QueryPlan { get; set; }
        public List<AdminCopilotDynamicTicketQueryPlan> QueryPlans { get; set; } = new();
        public List<AdminCopilotDynamicTicketQueryExecution> SubExecutions { get; set; } = new();
        public AdminCopilotActionPlan? ActionPlan { get; set; }
        public long TotalElapsedMs { get; set; }

        // Phase 1 Step 7 — answer provenance + confidence. Carries the source-of-truth label
        // the chat UI can render as a trust badge (verified / llm-cold / self-corrected /
        // direct-emit / conversational / knowledge / tool / semantic / refusal). Confidence
        // is in [0..1] (1.0 = verified-match cosine, 0.85 = clean LLM, 0.6 = self-corrected).
        // Null on legacy traces.
        public string? Provenance { get; set; }
        public double? Confidence { get; set; }

        public void AddStep(CopilotExecutionLayer layer, string action, string detail, CopilotStepStatus status = CopilotStepStatus.Ok, long elapsedMs = 0, string? technicalData = null, bool current = false, string? location = null)
        {
            var start = DateTime.UtcNow.AddMilliseconds(-elapsedMs);
            Steps.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = action,
                Detail = detail,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = start,
                CompletedAt = DateTime.UtcNow,
                TechnicalData = technicalData,
                Current = current,
                Location = location ?? "SuperAdminCopilot"
            });
        }
    }

    public class CopilotExecutionStep
    {
        public CopilotExecutionLayer Layer { get; set; } = CopilotExecutionLayer.Context;
        public string Action { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? Location { get; set; }
        public string? TechnicalData { get; set; }
        public CopilotStepStatus Status { get; set; } = CopilotStepStatus.Ok;
        public long ElapsedMs { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public bool Current { get; set; }
        public List<CopilotExecutionStep> SubSteps { get; set; } = new();

        // Per-step LLM call metrics — populated by HostTraceSink.MapStep when this step
        // matches a LlmCallRecord in the per-question LlmCallScope. Persisted as JSON inside
        // CopilotTraceHistory.ExecutionPlan so the investigation UI can render per-step cost
        // without a schema change. Null on non-LLM steps and legacy traces.
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public decimal? EstimatedCostUsd { get; set; }
        public string? LlmModelUsed { get; set; }

        public void AddSubStep(CopilotExecutionLayer layer, string action, string detail, CopilotStepStatus status = CopilotStepStatus.Ok, long elapsedMs = 0, string? technicalData = null, bool current = false, string? location = null)
        {
            var start = DateTime.UtcNow.AddMilliseconds(-elapsedMs);
            SubSteps.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = action,
                Detail = detail,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = start,
                CompletedAt = DateTime.UtcNow,
                TechnicalData = technicalData,
                Current = current,
                Location = location ?? "SuperAdminCopilot"
            });
        }
    }

    public enum CopilotFilterState
    {
        Unspecified = 0,
        Only = 1,
        Clear = 2
    }

    public class AdminCopilotDynamicTicketQueryPlan
    {
        public string? TargetView { get; set; }
        public DynamicQueryIntent Intent { get; set; } = DynamicQueryIntent.Unspecified;
        public string Summary { get; set; } = "";
        public bool RequiresClarification { get; set; }
        public string ClarificationQuestion { get; set; } = "";
        public string TicketNumber { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string PriorityName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string AssignedToName { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public List<string> StatusNames { get; set; } = new();
        public TicketDateRange RelativeDateRange { get; set; } = TicketDateRange.Any;
        public DateTime? AbsoluteStartDateUtc { get; set; }
        public DateTime? AbsoluteEndDateUtc { get; set; }
        public bool RequiresManagerReviewOnly { get; set; }
        public bool OpenOnly { get; set; }
        public bool ResolvedOnly { get; set; }
        public CopilotFilterState ManagerReviewFilterState { get; set; } = CopilotFilterState.Unspecified;
        public CopilotFilterState OpenFilterState { get; set; } = CopilotFilterState.Unspecified;
        public CopilotFilterState ResolvedFilterState { get; set; } = CopilotFilterState.Unspecified;
        public string TextSearch { get; set; } = "";
        public int MaxResults { get; set; } = 10;
        public string SortBy { get; set; } = "CreatedAt";
        public SortDirection SortDirection { get; set; } = SortDirection.Desc;
        public string? OrderByExpression { get; set; }
        public List<string> SelectedColumns { get; set; } = new();
        public string? GroupByField { get; set; }
        public string? AggregationType { get; set; }
        public string? AggregationColumn { get; set; }
        public Dictionary<string, string> GlobalFilters { get; set; } = new();
        public bool HasExplicitTargetView { get; set; }
        public bool HasExplicitLimit { get; set; }
        public bool HasExplicitSort { get; set; }
        public bool HasExplicitColumns { get; set; }
        public bool HasExplicitGrouping { get; set; }
        public bool HasExplicitDateRange { get; set; }
        public bool HasExplicitManagerReviewFilter { get; set; }
        public bool HasExplicitOpenFilter { get; set; }
        public bool HasExplicitResolvedFilter { get; set; }
    }

    public class AdminCopilotDynamicTicketQueryExecution
    {
        public AdminCopilotDynamicTicketQueryPlan Plan { get; set; } = new();
        public int TotalCount { get; set; }
        public int? RequestedLimit { get; set; }
        public List<AdminCopilotStatusCount> StatusBreakdown { get; set; } = new();
        public List<AdminCopilotTicketQueryRow> Rows { get; set; } = new();
        public List<string> StructuredColumns { get; set; } = new();
        public List<AdminCopilotStructuredResultRow> StructuredRows { get; set; } = new();
        public string? GeneratedSql { get; set; }
        public List<CopilotCatalogSqlParameterSnapshot> SqlParameters { get; set; } = new();
        public string Summary { get; set; } = "";
        public string Answer { get; set; } = "";
        public CopilotExecutionRoute ExecutionRoute { get; set; } = CopilotExecutionRoute.Pipeline;
        public EvidenceStrength EvidenceStrength { get; set; } = EvidenceStrength.General;
        public List<CopilotExecutionStep> ExecutionSteps { get; set; } = new();
        public List<AdminCopilotDynamicTicketQueryExecution> SubExecutions { get; set; } = new();
        public CopilotDataIntentPlan? IntentPlan { get; set; }
        public CopilotCatalogQuerySnapshot? QueryModel { get; set; }
        public CopilotPlannerSource PlannerSource { get; set; } = CopilotPlannerSource.Unspecified;
        public string? AnswerShapeGateMessage { get; set; }
        public bool IsLegacyCompatOnly { get; set; }
        public CopilotRequiredAnswerShape? RequiredShape { get; set; }
        public CopilotEntityReferenceResolution? EntityReference { get; set; }
    }

    public class AdminCopilotStructuredResultRow
    {
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LinkUrl { get; set; }
    }

    public class AdminCopilotStatusCount
    {
        public string StatusName { get; set; } = "";
        public int Count { get; set; }
    }

    public class AdminCopilotTicketQueryRow
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string Priority { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public string AssignedToName { get; set; } = "";
        public bool RequiresManagerReview { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
    }

    public class AdminCopilotActionPlan
    {
        public PlatformActionIntent Intent { get; set; } = PlatformActionIntent.None;
        public int? TargetTicketId { get; set; }
        public string? TargetTicketNumber { get; set; }
        public string? TargetValue { get; set; }
        public string? TargetValueDisplay { get; set; }
        public string Summary { get; set; } = "";
        public bool RequiresConfirmation { get; set; } = true;
        public bool IsExecuted { get; set; }
    }
}
