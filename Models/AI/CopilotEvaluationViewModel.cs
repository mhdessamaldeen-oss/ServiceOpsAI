using AISupportAnalysisPlatform.Models;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotEvaluationViewModel
    {
        public List<CopilotEvaluationTicketItem> Tickets { get; set; } = new();
        public List<CopilotEvaluationEntry> ExistingEvaluations { get; set; } = new();
        public int TotalEvaluations { get; set; }
        public int PassedEvaluations { get; set; }
        public int FailedEvaluations { get; set; }
    }

    public class CopilotEvaluationTicketItem
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
