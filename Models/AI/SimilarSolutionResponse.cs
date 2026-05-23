namespace AISupportAnalysisPlatform.Models.AI
{
    public class SimilarSolutionResponse
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? ResolutionSummary { get; set; }
        public string? RootCause { get; set; }
        public float SimilarityScore { get; set; }
        public string StatusName { get; set; } = string.Empty;
        public DateTime ResolvedAt { get; set; }
    }
}
