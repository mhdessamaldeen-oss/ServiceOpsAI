namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotHeuristicOptions
    {
        public const string SectionName = "Ai:CopilotHeuristics";

        public List<string> GreetingPhrases { get; set; } = new();
        public List<string> CapabilityQuestions { get; set; } = new();
        public List<string> AnalyticsPhrases { get; set; } = new();
        public List<string> TicketDomainPhrases { get; set; } = new();
        public List<string> KnowledgePhrases { get; set; } = new();
        public List<string> FollowUpPhrases { get; set; } = new();
        public List<string> MultiPartSeparators { get; set; } = new();
        public List<string> ToolStopWords { get; set; } = new();
        public List<string> ToolLeadingTerms { get; set; } = new();
    }
}
