namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotTicketRecommendation
    {
        public int TicketId { get; set; }
        public string Summary { get; set; } = "";
        public string RecommendedAction { get; set; } = "";
        public string EvidenceStrength { get; set; } = "Weak";
        public string ModelName { get; set; } = "";
        public string GenerationNotes { get; set; } = "";
        public int KnowledgeDocumentCount { get; set; }
        public DateTime GeneratedOnUtc { get; set; } = DateTime.UtcNow;
        public List<CopilotTicketCitation> SimilarTickets { get; set; } = new();
        public List<KnowledgeBaseChunkMatch> KnowledgeMatches { get; set; } = new();

        // ── Grounding audit results (Tier 2.4) ──
        // Populated when EnableGroundingAudit is on; surfaced in the Details page so
        // operators can see WHY EvidenceStrength was downgraded.
        public double GroundingConfidence { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = "";
        public List<string> UnsupportedClaims { get; set; } = new();
    }

    public class CopilotTicketCitation
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string ResolutionSummary { get; set; } = "";
        public string RootCause { get; set; } = "";
        public string Status { get; set; } = "";
        public float Score { get; set; }
    }
}
