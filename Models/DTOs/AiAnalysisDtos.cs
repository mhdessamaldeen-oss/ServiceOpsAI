using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Models.DTOs
{
    public class AiAnalysisStatusDto
    {
        public string Status { get; set; } = string.Empty;
        public string? QueueStatus { get; set; }
    }

    public class TicketAiAnalysisDto
    {
        public AiAnalysisStatus Status { get; set; }
        public string? Summary { get; set; }
        public AiConfidenceLevel Confidence { get; set; }
        public string? SuggestedClassification { get; set; }
        public string? SuggestedPriority { get; set; }
        public string? KeyClues { get; set; }
        public string? NextStepSuggestion { get; set; }
        public List<object> Attachments { get; set; } = new();
        public string? Model { get; set; }
        public long Duration { get; set; }
        public int PromptSize { get; set; }
        public string? Metadata { get; set; }
        public int RunNumber { get; set; }
        public string? LatestLog { get; set; }
        public string CreatedOn { get; set; } = string.Empty;
        public string LastRefreshed { get; set; } = string.Empty;
    }

    public class TicketRunHistoryDto
    {
        public int RunNumber { get; set; }
        public string AnalysisStatus { get; set; } = string.Empty;
        public string ConfidenceLevel { get; set; } = string.Empty;
        public string? ModelName { get; set; }
        public long Duration { get; set; }
        public string CreatedOn { get; set; } = string.Empty;
    }

    public class AiLogDto
    {
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
    }

    public class AiSearchMatchDto
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ResolutionSummary { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public string? Status { get; set; }
        public double Score { get; set; }
        public string? ResolvedAt { get; set; }
    }

    public class KnowledgeMatchDto
    {
        public string DocumentName { get; set; } = string.Empty;
        public string SectionTitle { get; set; } = string.Empty;
        public string Excerpt { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public double Score { get; set; }
        public double VectorScore { get; set; }
        public double LexicalScore { get; set; }
    }

    public class AiRecommendationDto
    {
        public int TicketId { get; set; }
        public string? Summary { get; set; }
        public string? RecommendedAction { get; set; }
        public string? EvidenceStrength { get; set; }
        public string? ModelName { get; set; }
        public string? GenerationNotes { get; set; }
        public int KnowledgeDocumentCount { get; set; }
        public string GeneratedOn { get; set; } = string.Empty;
        public List<AiSearchMatchDto> SimilarTickets { get; set; } = new();
        public List<KnowledgeMatchDto> KnowledgeMatches { get; set; } = new();

        // Grounding audit fields (Tier 2.4) — let the UI explain why EvidenceStrength
        // was downgraded, and surface unsupported claims for operator review.
        public double GroundingConfidence { get; set; } = 1.0;
        public string GroundingNotes { get; set; } = string.Empty;
        public List<string> UnsupportedClaims { get; set; } = new();
    }

    public class AiBatchAnalysisDto
    {
        public string Message { get; set; } = string.Empty;
        public string BatchId { get; set; } = string.Empty;
        public int TotalCount { get; set; }
    }

    public class EmbeddingProgressDto
    {
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int? CurrentTicketId { get; set; }
        public bool IsRunning { get; set; }
        public int ProcessedCount { get; set; }
        public double ProgressPercent { get; set; }
        public string? LastErrorMessage { get; set; }
    }
}
