namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotEvaluationEntry
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string TicketTitle { get; set; } = "";
        public string SimilarTicketsRating { get; set; } = "Unknown";
        public string KnowledgeBaseRating { get; set; } = "Unknown";
        public string RecommendedActionRating { get; set; } = "Unknown";
        public string EvidenceStrengthRating { get; set; } = "Unknown";
        public string OverallOutcome { get; set; } = "Unknown";
        public string Notes { get; set; } = "";
        public string EvaluatedBy { get; set; } = "";
        public DateTime EvaluatedOnUtc { get; set; } = DateTime.UtcNow;
    }
}
