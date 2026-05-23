using Microsoft.Extensions.Options;
using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Services.AI.Common
{
    public sealed class CopilotTextCatalog
    {
        private readonly IOptionsMonitor<CopilotTextSettings> _monitor;

        public CopilotTextCatalog(IOptionsMonitor<CopilotTextSettings> monitor)
        {
            _monitor = monitor;
        }

        private CopilotTextSettings _s => _monitor.CurrentValue;

        // General
        public string EmptyQuestionMessage => _s.General.EmptyQuestionMessage;
        public string ClarificationRequiredNotes => _s.General.ClarificationRequiredNotes;
        public string UnsupportedAnswer => _s.General.UnsupportedAnswer;
        public string UnsupportedSummary => _s.General.UnsupportedSummary;
        public IReadOnlyList<string> DefaultSuggestedPrompts => _s.General.DefaultSuggestedPrompts;
        public IReadOnlyList<OperationCatalogEntry> OperationsCatalog => _s.OperationsCatalog;

        // Routing
        public string GreetingReason => _s.Routing.GreetingReason;
        public string FollowUpAnalyticsReason => _s.Routing.FollowUpAnalyticsReason;
        public string FollowUpInvestigationReason => _s.Routing.FollowUpInvestigationReason;
        public string TicketKnowledgeReason => _s.Routing.TicketKnowledgeReason;
        public string TicketReferenceReason => _s.Routing.TicketReferenceReason;
        public string StructuredDataReason => _s.Routing.StructuredDataReason;
        public string RoutingKnowledgeBaseQueryReason => _s.Routing.KnowledgeBaseQueryReason;
        public string ClassificationFallbackReason => _s.Routing.ClassificationFallbackReason;

        // Prompts
        public IReadOnlyList<string> IntentClassifierPromptLines => _s.Prompts.IntentClassifier;
        public IReadOnlyList<string> ToolResolverPromptLines => _s.Prompts.ToolResolver;
        public IReadOnlyList<string> ExternalToolSummaryPromptLines => _s.Prompts.ExternalToolSummary;
        public IReadOnlyList<string> KnowledgeMatchPromptIntroLines => _s.Prompts.KnowledgeMatchIntro;
        public IReadOnlyList<string> GeneralChatPromptLines => _s.Prompts.GeneralChat;
        public IReadOnlyList<string> DataIntentPlannerPromptLines => _s.Prompts.DataIntentPlanner;
        public IReadOnlyList<string> PlatformActionPlannerPromptLines => _s.Prompts.PlatformActionPlanner;
        public IReadOnlyList<string> RecommendationEvidencePromptLines => _s.Prompts.RecommendationEvidence;
        public IReadOnlyList<string> GroundingPromptLines => _s.Prompts.Grounding;

        // Classification & ToolResolution patterns (Tier 2 portability — operator overrides)
        public IReadOnlyList<string> ClassificationOffTopicPatterns => _s.Classification.OffTopicPatterns;
        public IReadOnlyList<string> ClassificationKnowledgePatterns => _s.Classification.KnowledgePatterns;
        public IReadOnlyList<string> ClassificationWriteIntentVerbs => _s.Classification.WriteIntentVerbs;
        public IReadOnlyList<string> ClassificationCompoundIndicators => _s.Classification.CompoundIndicators;
        public IReadOnlyList<string> ToolResolutionStopwords => _s.ToolResolution.Stopwords;
        public IReadOnlyList<string> ToolResolutionLeadingTerms => _s.ToolResolution.LeadingTerms;

        // External Tools
        public string ExternalToolMissingAnswer => _s.ExternalTools.MissingAnswer;
        public string ExternalToolMissingNotes => _s.ExternalTools.MissingNotes;
        public string ExternalToolLookupFailedSummary => _s.ExternalTools.LookupFailedSummary;
        public string ExternalToolClarificationSummary => _s.ExternalTools.ClarificationSummary;
        public string ExternalToolSummaryFallbackTemplate => _s.ExternalTools.SummaryFallbackTemplate;
        public string ExternalToolSuccessSummaryTemplate => _s.ExternalTools.SuccessSummaryTemplate;
        public string ExternalToolFailureAnswerTemplate => _s.ExternalTools.FailureAnswerTemplate;
        public string ExternalToolFailureSummary => _s.ExternalTools.FailureSummary;
        public string ToolResolverEmptyQuestionReason => _s.ExternalTools.ToolResolverEmptyQuestionReason;
        public string ToolResolverNoToolsReason => _s.ExternalTools.ToolResolverNoToolsReason;
        public string ToolResolverNoMatchReason => _s.ExternalTools.ToolResolverNoMatchReason;
        public string ToolResolverDeterministicReasonTemplate => _s.ExternalTools.ToolResolverDeterministicReasonTemplate;
        public string ToolResolverModelNoResponseReason => _s.ExternalTools.ToolResolverModelNoResponseReason;
        public string ToolResolverAmbiguousReason => _s.ExternalTools.ToolResolverAmbiguousReason;

        // General Chat
        public string GeneralChatNoToolsText => _s.GeneralChat.NoToolsText;
        public string GeneralChatFallbackAnswer => _s.GeneralChat.FallbackAnswer;

        // Investigation
        public string KnowledgeMatchExecutionSummaryTemplate => _s.Investigation.KnowledgeMatchExecutionSummaryTemplate;
        public string InvestigationPromptTicketContextLabel => _s.Investigation.TicketContextLabel;
        public string InvestigationPromptTicketLineTemplate => _s.Investigation.TicketLineTemplate;
        public string InvestigationPromptTitleLineTemplate => _s.Investigation.TitleLineTemplate;
        public string InvestigationPromptStatusLineTemplate => _s.Investigation.StatusLineTemplate;
        public string InvestigationPromptPriorityLineTemplate => _s.Investigation.PriorityLineTemplate;
        public string InvestigationPromptPendingReasonLineTemplate => _s.Investigation.PendingReasonLineTemplate;
        public string InvestigationPromptTechnicalAssessmentLineTemplate => _s.Investigation.TechnicalAssessmentLineTemplate;
        public string InvestigationPromptResolutionSummaryLineTemplate => _s.Investigation.ResolutionSummaryLineTemplate;
        public string InvestigationPromptKnowledgeLabel => _s.Investigation.KnowledgeLabel;
        public string InvestigationPromptKnowledgeItemTemplate => _s.Investigation.KnowledgeItemTemplate;
        public string InvestigationPromptSimilarLabel => _s.Investigation.SimilarLabel;
        public string InvestigationPromptSimilarItemTemplate => _s.Investigation.SimilarItemTemplate;
        public string InvestigationPromptUserQuestionTemplate => _s.Investigation.UserQuestionTemplate;
        public string InvestigationFallbackTicketTemplate => _s.Investigation.FallbackTicketTemplate;
        public string InvestigationFallbackKnowledgeTemplate => _s.Investigation.FallbackKnowledgeTemplate;
        public string InvestigationFallbackSimilarTemplate => _s.Investigation.FallbackSimilarTemplate;
        public string InvestigationFallbackNoEvidence => _s.Investigation.FallbackNoEvidence;

        // Follow Up
        public string TicketFollowUpNextActionTemplate => _s.FollowUp.TicketNextActionTemplate;
        public string TicketFollowUpSimilarTemplate => _s.FollowUp.TicketSimilarTemplate;
        public string TicketFollowUpKnowledgeTemplate => _s.FollowUp.TicketKnowledgeTemplate;
        public string GeneralFollowUpSimilarTemplate => _s.FollowUp.GeneralSimilarTemplate;
        public string GeneralFollowUpNextAction => _s.FollowUp.GeneralNextAction;
        public string GeneralFollowUpKnowledge => _s.FollowUp.GeneralKnowledge;

        // Verification
        public string VerificationClarificationMessage => _s.Verification.ClarificationMessage;
        public string VerificationStructuredPlanMissing => _s.Verification.StructuredPlanMissing;
        public string VerificationNoData => _s.Verification.NoData;
        public string VerificationMissingTicketContext => _s.Verification.MissingTicketContext;
        public string VerificationMissingTool => _s.Verification.MissingTool;
        public string VerificationNoAnswer => _s.Verification.NoAnswer;
        public string VerificationPassed => _s.Verification.Passed;

        // Pipeline
        public string StepPreprocessAction => _s.Pipeline.PreprocessAction;
        public string StepPreprocessDetailTemplate => _s.Pipeline.PreprocessDetailTemplate;
        public string StepNormalizeAction => _s.Pipeline.NormalizeAction;
        public string StepNormalizeDetailTemplate => _s.Pipeline.NormalizeDetailTemplate;
        public string StepLoadConversationAction => _s.Pipeline.LoadConversationAction;
        public string StepClassifyIntentActionTemplate => _s.Pipeline.ClassifyIntentActionTemplate;
        public string StepBuildPlanAction => _s.Pipeline.BuildPlanAction;
        public string StepBuildPlanClarificationTemplate => _s.Pipeline.BuildPlanClarificationTemplate;
        public string StepBuildPlanDetailTemplate => _s.Pipeline.BuildPlanDetailTemplate;
        public string StepBuildPlanMultiQueryTemplate => _s.Pipeline.MultiQuerySummaryTemplate;
        public string StepExecuteActionTemplate => _s.Pipeline.ExecuteActionTemplate;
        public string StepVerifyAction => _s.Pipeline.VerifyAction;
        public string PipelineCancelledMessage => _s.Pipeline.CancelledMessage;
        public string PipelineErrorMessage => _s.Pipeline.ErrorMessage;
        public string PipelineErrorStepAction => _s.Pipeline.ErrorStepAction;
        public string ClassificationFallbackStepAction => _s.Pipeline.ClassificationFallbackStepAction;
        public string ClassificationFallbackStepDetail => _s.Pipeline.ClassificationFallbackStepDetail;
        public string PipelineEvaluatorUnknownEntityTemplate => _s.Pipeline.EvaluatorUnknownEntityTemplate;
        public string PipelineEvaluatorMissingPrimaryEntityTemplate => _s.Pipeline.EvaluatorMissingPrimaryEntityTemplate;
        public string PipelineEvaluatorInvalidFieldTemplate => _s.Pipeline.EvaluatorInvalidFieldTemplate;
        public string PipelineEvaluatorInvalidGroupingTemplate => _s.Pipeline.EvaluatorInvalidGroupingTemplate;
        public string PipelineEvaluatorInvalidAggregationTemplate => _s.Pipeline.EvaluatorInvalidAggregationTemplate;
        public string PipelineSqlValidationFailedTemplate => _s.Pipeline.SqlValidationFailedTemplate;
    }
}
