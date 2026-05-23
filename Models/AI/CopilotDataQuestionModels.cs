namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Explicit operation requested by a data question.
    /// This is separate from the target dataset so the planner does not confuse
    /// "count entities" with "list tickets" or "break down tickets by status".
    /// </summary>
    public enum CopilotDataOperation
    {
        Unknown = 0,
        Count = 1,
        List = 2,
        Breakdown = 3,
        Detail = 4,
        Compare = 5,
        /// <summary>Standalone scalar aggregation (avg/sum/max/min) without a group-by clause.</summary>
        Aggregate = 6
    }

    /// <summary>
    /// Target dataset requested by a data question.
    /// </summary>
    public enum CopilotDataDataset
    {
        Unknown = 0,
        Tickets = 1,
        Entities = 2,
        Users = 3,
        Roles = 4,
        Notifications = 5,
        Settings = 6,
        AiAnalysis = 7
    }

    /// <summary>
    /// Concrete analytics view selected for execution.
    /// Kept explicit so planners and validators do not depend on raw string view names.
    /// </summary>
    public enum CopilotAnalyticsViewKind
    {
        Unknown = 0,
        TicketRecords = 1,
        EntityPerformance = 2,

        Users = 3,
        Roles = 4,
        Notifications = 5,
        Settings = 6,
        AiAnalysis = 7
    }

    /// <summary>
    /// Preferred output shape for the final response.
    /// </summary>
    public enum CopilotDataOutputShape
    {
        Unknown = 0,
        Metric = 1,
        Table = 2,
        Detail = 3
    }

    /// <summary>
    /// Parsed deterministic view of one data question or one clause inside a multipart question.
    /// </summary>
    public class CopilotDataQuestionSpec
    {
        public string QueryText { get; set; } = "";
        public string NormalizedQueryText { get; set; } = "";
        public CopilotDataOperation Operation { get; set; } = CopilotDataOperation.Unknown;
        public CopilotDataDataset Dataset { get; set; } = CopilotDataDataset.Unknown;
        public CopilotDataOutputShape OutputShape { get; set; } = CopilotDataOutputShape.Unknown;
        public string TicketNumber { get; set; } = "";
        public string EntityName { get; set; } = "";
        public string PriorityName { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string AssignedToName { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public List<string> StatusNames { get; set; } = new();
        public TicketDateRange RelativeDateRange { get; set; } = TicketDateRange.Any;
        public bool WantsAllResults { get; set; }
        public int? ExplicitLimit { get; set; }
        public string? GroupByField { get; set; }
        public string? AggregationType { get; set; }
        public string? AggregationColumn { get; set; }
        public bool IsSpecificTicketRequest { get; set; }
        public bool IsConfident { get; set; }
    }
}
