namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotTextSettings
    {
        public GeneralSettings General { get; set; } = new();
        public RoutingSettings Routing { get; set; } = new();
        public PromptSettings Prompts { get; set; } = new();
        public ExternalToolSettings ExternalTools { get; set; } = new();
        public GeneralChatSettings GeneralChat { get; set; } = new();
        public InvestigationSettings Investigation { get; set; } = new();
        public FollowUpSettings FollowUp { get; set; } = new();
        public VerificationSettings Verification { get; set; } = new();
        public PipelineSettings Pipeline { get; set; } = new();
        public List<OperationCatalogEntry> OperationsCatalog { get; set; } = new();

        /// <summary>
        /// Pattern lists for the deterministic classifier and tool resolver. Operators tune these
        /// in <c>copilot-text.json</c> without recompiling. When a list is empty in the JSON, the
        /// service falls back to the built-in defaults so the system stays functional out of the box.
        /// Tier 2 portability fix.
        /// </summary>
        public ClassificationSettings Classification { get; set; } = new();

        /// <summary>Tool-resolution stopwords + leading-term lists used by the external-tool intent
        /// resolver. Override in JSON for non-English deployments. Tier 2 portability fix.</summary>
        public ToolResolutionSettings ToolResolution { get; set; } = new();
    }

    /// <summary>
    /// Regex/word lists driving the deterministic preflight chain + the deterministic planner's
    /// compound-shape detection. Each list is optional in the JSON; empty/absent → built-in defaults.
    /// </summary>
    public class ClassificationSettings
    {
        /// <summary>Regex patterns that mark a question as off-topic chat (jokes, stories,
        /// personal banter). Each entry is a full anchored regex string.</summary>
        public List<string> OffTopicPatterns { get; set; } = new();

        /// <summary>Regex patterns that mark a question as a knowledge / how-to / explain query
        /// (route to GeneralSupport, not DataQuery).</summary>
        public List<string> KnowledgePatterns { get; set; } = new();

        /// <summary>Verbs (regex-or-list) at sentence start that indicate a write/state-changing
        /// command. The Copilot is read-only by contract — these get refused as Unsupported.</summary>
        public List<string> WriteIntentVerbs { get; set; } = new();

        /// <summary>Words/phrases inside a question that suggest multi-clause / compound shape
        /// the deterministic planner shouldn't try to handle on its own (passed to LLM with hint).</summary>
        public List<string> CompoundIndicators { get; set; } = new();
    }

    /// <summary>Per-app overrides for the external-tool intent resolver's vocabulary lists.</summary>
    public class ToolResolutionSettings
    {
        /// <summary>Words to strip from the question text before scoring tool candidates.
        /// Default covers common English + Arabic interrogatives ("what", "show", "في", "من").</summary>
        public List<string> Stopwords { get; set; } = new();

        /// <summary>Prepositions stripped from the leading edge of an extracted parameter value
        /// ("about" / "in" / "from" / "of" / etc).</summary>
        public List<string> LeadingTerms { get; set; } = new();
    }

    public class OperationCatalogEntry
    {
        public string Id { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
    }

    public class GeneralSettings
    {
        public string EmptyQuestionMessage { get; set; } = "";
        public string ClarificationRequiredNotes { get; set; } = "";
        public string UnsupportedAnswer { get; set; } = "";
        public string UnsupportedSummary { get; set; } = "";
        public string SecurityBlockReason { get; set; } = "";
        public string SecurityBlockMessage { get; set; } = "";
        public List<string> DefaultSuggestedPrompts { get; set; } = new();
    }

    public class RoutingSettings
    {
        public string GreetingReason { get; set; } = "";
        public string FollowUpAnalyticsReason { get; set; } = "";
        public string FollowUpInvestigationReason { get; set; } = "";
        public string TicketKnowledgeReason { get; set; } = "";
        public string TicketReferenceReason { get; set; } = "";
        public string StructuredDataReason { get; set; } = "";
        public string KnowledgeBaseQueryReason { get; set; } = "";
        public string ClassificationFallbackReason { get; set; } = "";
        public HeuristicWeights Weights { get; set; } = new();
    }

    public class HeuristicWeights
    {
        public double StructuredDataIndicator { get; set; } = 0.6;
        public double TicketCountSignal { get; set; } = 0.4;
        public double TicketReferenceEnrichment { get; set; } = 0.2;
        public double KnowledgeBaseIndicator { get; set; } = 0.8;
        public double KnowledgePhraseSignal { get; set; } = 0.3;
        public double ExclusiveTicketLookup { get; set; } = 0.9;
        public double GreetingIndicator { get; set; } = 1.0;
        public double HeuristicBypassThreshold { get; set; } = 0.8;
    }

    public class PromptSettings
    {
        public List<string> IntentClassifier { get; set; } = new();
        public List<string> ToolResolver { get; set; } = new();
        public List<string> ExternalToolSummary { get; set; } = new();
        public List<string> KnowledgeMatchIntro { get; set; } = new();
        public List<string> GeneralChat { get; set; } = new();
        public List<string> DataIntentPlanner { get; set; } = new();
        public List<string> PlatformActionPlanner { get; set; } = new();
        public List<string> RecommendationEvidence { get; set; } = new();
        public List<string> Grounding { get; set; } = new();
    }

    public class ExternalToolSettings
    {
        public string MissingAnswer { get; set; } = "";
        public string MissingNotes { get; set; } = "";
        public string LookupFailedSummary { get; set; } = "";
        public string ClarificationSummary { get; set; } = "";
        public string SummaryFallbackTemplate { get; set; } = "";
        public string SuccessSummaryTemplate { get; set; } = "";
        public string FailureAnswerTemplate { get; set; } = "";
        public string FailureSummary { get; set; } = "";
        public string ToolResolverEmptyQuestionReason { get; set; } = "";
        public string ToolResolverNoToolsReason { get; set; } = "";
        public string ToolResolverNoMatchReason { get; set; } = "";
        public string ToolResolverDeterministicReasonTemplate { get; set; } = "";
        public string ToolResolverModelNoResponseReason { get; set; } = "";
        public string ToolResolverAmbiguousReason { get; set; } = "";
    }

    public class GeneralChatSettings
    {
        public string NoToolsText { get; set; } = "";
        public string FallbackAnswer { get; set; } = "";
    }

    public class InvestigationSettings
    {
        public string KnowledgeMatchExecutionSummaryTemplate { get; set; } = "";
        public string TicketContextLabel { get; set; } = "";
        public string TicketLineTemplate { get; set; } = "";
        public string TitleLineTemplate { get; set; } = "";
        public string StatusLineTemplate { get; set; } = "";
        public string PriorityLineTemplate { get; set; } = "";
        public string PendingReasonLineTemplate { get; set; } = "";
        public string TechnicalAssessmentLineTemplate { get; set; } = "";
        public string ResolutionSummaryLineTemplate { get; set; } = "";
        public string KnowledgeLabel { get; set; } = "";
        public string KnowledgeItemTemplate { get; set; } = "";
        public string SimilarLabel { get; set; } = "";
        public string SimilarItemTemplate { get; set; } = "";
        public string UserQuestionTemplate { get; set; } = "";
        public string FallbackTicketTemplate { get; set; } = "";
        public string FallbackKnowledgeTemplate { get; set; } = "";
        public string FallbackSimilarTemplate { get; set; } = "";
        public string FallbackNoEvidence { get; set; } = "";
    }

    public class FollowUpSettings
    {
        public string TicketNextActionTemplate { get; set; } = "";
        public string TicketSimilarTemplate { get; set; } = "";
        public string TicketKnowledgeTemplate { get; set; } = "";
        public string GeneralSimilarTemplate { get; set; } = "";
        public string GeneralNextAction { get; set; } = "";
        public string GeneralKnowledge { get; set; } = "";
    }

    public class VerificationSettings
    {
        public string ClarificationMessage { get; set; } = "";
        public string StructuredPlanMissing { get; set; } = "";
        public string NoData { get; set; } = "";
        public string MissingTicketContext { get; set; } = "";
        public string TicketNotFound { get; set; } = "";
        public string MissingTool { get; set; } = "";
        public string NoAnswer { get; set; } = "";
        public string Passed { get; set; } = "";
    }

    public class PipelineSettings
    {
        /// <summary>
        /// Maximum seconds allowed for the full copilot pipeline before it is cancelled.
        /// A value of 0 or less disables the timeout (no limit).
        /// </summary>
        public int PipelineTimeoutSeconds { get; set; } = 50000;
        public string PreprocessAction { get; set; } = "";
        public string PreprocessDetailTemplate { get; set; } = "";
        public string NormalizeAction { get; set; } = "";
        public string NormalizeDetailTemplate { get; set; } = "";
        public string LoadConversationAction { get; set; } = "";
        public string ClassifyIntentActionTemplate { get; set; } = "";
        public string BuildPlanAction { get; set; } = "";
        public string BuildPlanClarificationTemplate { get; set; } = "";
        public string BuildPlanDetailTemplate { get; set; } = "";
        public string MultiQuerySummaryTemplate { get; set; } = "Coordinated {COUNT} analytics checks from one request.";
        public string ExecuteActionTemplate { get; set; } = "";
        public string VerifyAction { get; set; } = "";
        public string CancelledMessage { get; set; } = "";
        public string TimeoutMessage { get; set; } = "The request took too long to process. Please try a simpler question or try again.";
        public string ErrorMessage { get; set; } = "";
        public string ErrorStepAction { get; set; } = "";
        public string ClassificationFallbackStepAction { get; set; } = "";
        public string ClassificationFallbackStepDetail { get; set; } = "";
        public string EvaluatorUnknownEntityTemplate { get; set; } = "The entity '{0}' is not defined in the approved catalog.";
        public string EvaluatorMissingPrimaryEntityTemplate { get; set; } = "The primary entity '{0}' is missing from the catalog.";
        public string EvaluatorInvalidFieldTemplate { get; set; } = "The requested field '{0}' cannot be resolved or is not approved for display.";
        public string EvaluatorInvalidGroupingTemplate { get; set; } = "The grouping field '{0}' is not approved for grouping operations.";
        public string EvaluatorInvalidAggregationTemplate { get; set; } = "The aggregation field '{0}' on '{1}' is not approved for '{2}' operations.";
        public string SqlValidationFailedTemplate { get; set; } = "SQL Validation Failed: {0}";
    }
}
