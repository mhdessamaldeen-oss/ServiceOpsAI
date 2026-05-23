namespace ServiceOpsAI.Models.AI
{
    public class CopilotConversationContext
    {
        public bool HasPriorContext { get; set; }
        public bool IsFollowUpCandidate { get; set; }
        public int? LastTraceId { get; set; }
        public string LastTicketNumber { get; set; } = "";
        public string LastIntent { get; set; } = "";
        public string Summary { get; set; } = "";
        public string LastQuestion { get; set; } = "";
        public string LastAnswerExcerpt { get; set; } = "";
        public AdminCopilotDynamicTicketQueryPlan? PreviousQueryPlan { get; set; }
        public CopilotDataIntentPlan? PreviousDataIntentPlan { get; set; }
        public AdminCopilotActionPlan? PreviousActionPlan { get; set; }
    }
}
