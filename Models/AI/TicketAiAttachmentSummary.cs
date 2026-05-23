using System;
using System.ComponentModel.DataAnnotations;
using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Models
{
    /// <summary>
    /// Normalized per-attachment AI summary. One row per analyzed file per analysis run.
    /// </summary>
    public class TicketAiAttachmentSummary
    {
        public int Id { get; set; }

        public int TicketAiAnalysisId { get; set; }
        public TicketAiAnalysis? TicketAiAnalysis { get; set; }

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        public string Summary { get; set; } = string.Empty;

        public AiRelevanceLevel Relevance { get; set; } = AiRelevanceLevel.Low;

        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    }
}
