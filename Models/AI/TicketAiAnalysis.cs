using AISupportAnalysisPlatform.Enums;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    /// <summary>
    /// Represents the simplified, lightweight AI analysis result for a ticket.
    /// Optimized for local models and admin-only utility.
    /// </summary>
    public class TicketAiAnalysis
    {
        public int Id { get; set; }

        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }

        /// <summary>
        /// Keeps track of analysis runs for the same ticket.
        /// </summary>
        public int RunNumber { get; set; } = 1;

        [Required]
        public string Summary { get; set; } = string.Empty;

        public string KeyClues { get; set; } = string.Empty;

        [StringLength(100)]
        public string SuggestedClassification { get; set; } = string.Empty;

        [StringLength(50)]
        public string SuggestedPriority { get; set; } = string.Empty;

        public string NextStepSuggestion { get; set; } = string.Empty;

        public AiConfidenceLevel ConfidenceLevel { get; set; } = AiConfidenceLevel.Low;

        [StringLength(100)]
        public string ModelName { get; set; } = string.Empty;

        [StringLength(20)]
        public string PromptVersion { get; set; } = "1.0";

        public AiAnalysisStatus AnalysisStatus { get; set; } = AiAnalysisStatus.NotStarted;

        // --- Metadata ---
        public long ProcessingDurationMs { get; set; }
        public int InputPromptSize { get; set; }
        public string DiagnosticMetadata { get; set; } = string.Empty;

        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public DateTime? LastRefreshedOn { get; set; }

        [StringLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        // --- Navigation ---
        public ICollection<TicketAiAnalysisLog> ExecutionLogs { get; set; } = new List<TicketAiAnalysisLog>();
    }
}
