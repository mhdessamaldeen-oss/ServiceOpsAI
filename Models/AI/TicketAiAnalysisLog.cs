using System;
using System.ComponentModel.DataAnnotations;
using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Models
{
    public class TicketAiAnalysisLog
    {
        public int Id { get; set; }
        public int TicketAiAnalysisId { get; set; }
        public TicketAiAnalysis? TicketAiAnalysis { get; set; }
        public string Message { get; set; } = string.Empty;
        public AiLogLevel LogLevel { get; set; } = AiLogLevel.Info;
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    }
}
