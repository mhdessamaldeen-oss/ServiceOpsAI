namespace ServiceOpsAI.Models.AI
{
    public class CopilotChatRequest
    {
        public int? SessionId { get; set; }
        public int? LastTraceId { get; set; }
        public string Surface { get; set; } = "";
        public string ReportStartDate { get; set; } = "";
        public string ReportEndDate { get; set; } = "";
        public string Question { get; set; } = "";
        public int? TicketId { get; set; }
        public List<CopilotChatMessage> History { get; set; } = new();
        
        // Audit & Comparison
        public Guid? AssessmentRunId { get; set; }
        public string? Metadata { get; set; }
        public string? CaseCode { get; set; }
        public string? ConnectionId { get; set; }
        public bool IsAssessment { get; set; }

        /// <summary>The assessment SUITE FILE this question came from. Set by the assessment
        /// handler when running scenarios from a suite file; left null for chat copilot calls so
        /// the trace history can distinguish "from file X" vs "from interactive chat".</summary>
        public string? SourceSuite { get; set; }

        /// <summary>Reference SQL the question was authored against (CopilotAssessmentCase.GoldSql
        /// from the suite file). Set by the assessment handler so the bridge can forward it to the
        /// pipeline, which stores it in CopilotTraceHistory.ExpectedScript alongside the generated
        /// SQL. Null for chat-driven requests.</summary>
        public string? ExpectedSql { get; set; }
    }

    public class CopilotChatMessage
    {
        public ChatMessageRole Role { get; set; } = ChatMessageRole.User;
        public string Content { get; set; } = "";
    }

    public class CopilotChatResponse
    {
        public int? TraceId { get; set; }
        public int? TicketId { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public EvidenceStrength EvidenceStrength { get; set; } = EvidenceStrength.Weak;
        public double GroundingScore { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = "";
        public ResponseMode ResponseMode { get; set; } = ResponseMode.KnowledgeMatch;
        public string UsedTool { get; set; } = "none";
        public string ModelName { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<string> SuggestedPrompts { get; set; } = new();
        public List<CopilotTicketCitation> SimilarTickets { get; set; } = new();
        public List<KnowledgeBaseChunkMatch> KnowledgeMatches { get; set; } = new();
        public AdminCopilotExecutionDetails ExecutionDetails { get; set; } = new();
        public AdminCopilotDynamicTicketQueryPlan? DynamicQueryPlan { get; set; }
        public List<CopilotStructuredSubResult> StructuredQueryResults { get; set; } = new();
        public List<AdminCopilotTicketQueryRow> DynamicTicketResults { get; set; } = new();
        public List<string> StructuredColumns { get; set; } = new();
        public List<AdminCopilotStructuredResultRow> StructuredRows { get; set; } = new();
        public AdminCopilotActionPlan? ActionPlan { get; set; }

        public void ApplyResult(CopilotExecutionResult execution)
        {
            Answer = execution.Answer;
            Notes = string.IsNullOrWhiteSpace(Notes) ? execution.Notes : Notes;
            ResponseMode = execution.ResponseMode;
            EvidenceStrength = execution.EvidenceStrength;
            GroundingScore = execution.GroundingScore;
            GroundingNotes = execution.GroundingNotes;
            UsedTool = execution.UsedTool;
            SuggestedPrompts = execution.SuggestedPrompts;
            SimilarTickets = execution.SimilarTickets;
            KnowledgeMatches = execution.KnowledgeMatches;
            DynamicQueryPlan = execution.DynamicQueryPlan;
            StructuredQueryResults = execution.StructuredQueryResults;
            ActionPlan = execution.ActionPlan;
            DynamicTicketResults = execution.DynamicTicketResults;
            StructuredColumns = execution.StructuredColumns;
            StructuredRows = execution.StructuredRows;

            ExecutionDetails.Summary = execution.Summary;
            ExecutionDetails.LastTechnicalData = execution.TechnicalData;
            ExecutionDetails.QueryPlan = execution.DynamicQueryPlan;
            ExecutionDetails.QueryPlans = execution.StructuredQueryResults.Select(result => result.Execution.Plan).ToList();
            
            if (execution.StructuredResult?.SubExecutions?.Count > 0)
            {
                ExecutionDetails.SubExecutions = execution.StructuredResult.SubExecutions;
            }

            ExecutionDetails.ActionPlan = execution.ActionPlan;
        }
    }

    public class CopilotChatViewModel
    {
        public List<CopilotEvaluationTicketItem> RecentTickets { get; set; } = new();
        public List<CopilotToolDefinition> AvailableTools { get; set; } = new();
        public List<CopilotCapabilityItem> ExternalCapabilities { get; set; } = new();
        public List<CopilotPromptGroup> StandardPromptGroups { get; set; } = new();
        public List<CopilotTraceHistory> RecentTraces { get; set; } = new();
        public List<CopilotChatSession> RecentSessions { get; set; } = new();
        public int KnowledgeDocumentCount { get; set; }
        public string InitialPrompt { get; set; } = string.Empty;
        public string? InitialCaseCode { get; set; }
        public string? InitialSourceSuite { get; set; }
    }

    public class CopilotSamplePrompt
    {
        public string Text { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string? SourceSuite { get; set; }
    }

    public class CopilotCapabilityItem
    {
        public string ToolKey { get; set; } = string.Empty;
        public string ToolTitle { get; set; } = string.Empty;
        public string ToolDescription { get; set; } = string.Empty;
        public List<CopilotSamplePrompt> Prompts { get; set; } = new();
    }

    public class CopilotPromptGroup
    {
        public string Title { get; set; } = string.Empty;
        public List<CopilotSamplePrompt> Prompts { get; set; } = new();
    }

    public class ClearSessionRequest
    {
        public int SessionId { get; set; }
    }
}
