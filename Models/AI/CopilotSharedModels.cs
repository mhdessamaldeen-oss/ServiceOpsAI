namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Shared metrics and summary models for the Copilot AI service.
    /// Moved to public visibility to support layered handler architecture.
    /// </summary>
    public class TicketMetrics
    {
        public int TotalActiveTickets { get; set; }
        public int OpenTickets { get; set; }
        public int ResolvedTickets { get; set; }
        public int ClosedTickets { get; set; }
        public int ManagerReviewTickets { get; set; }
        public int CreatedToday { get; set; }
    }

    public class RepeatedTicketSummary
    {
        public string Title { get; set; } = "";
        public int Count { get; set; }
        public string SampleTicketNumber { get; set; } = "";
    }

    public class EntityTicketCountSummary
    {
        public string EntityName { get; set; } = "";
        public int TotalTickets { get; set; }
        public int OpenTickets { get; set; }
        public int ResolvedTickets { get; set; }
        public int ClosedTickets { get; set; }
    }
}
