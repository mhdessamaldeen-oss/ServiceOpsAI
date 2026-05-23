namespace ServiceOpsAI.Models.AI
{
    /// <summary>
    /// The shape contract a child workflow must satisfy after planning.
    /// Populated by the request decomposer from catalog signals; validated by the executor before SQL runs.
    /// </summary>
    public class CopilotRequiredAnswerShape
    {
        /// <summary>Catalog entity role that should produce the rows (e.g. TicketRecord, TicketCommentEvidence).</summary>
        public string EntityRole { get; set; } = string.Empty;
        /// <summary>Required filter roles by name (e.g. "status=Open", "ticket-reference"). Free-form tokens.</summary>
        public List<string> RequiredFilterRoles { get; set; } = new();
        /// <summary>Required group-by field roles. Empty = no grouping required.</summary>
        public List<string> RequiredGroupingRoles { get; set; } = new();
        /// <summary>Required projection field roles. Empty = no projection requirement.</summary>
        public List<string> RequiredProjectionRoles { get; set; } = new();
        /// <summary>Required limit (top N). Null = no limit requirement.</summary>
        public int? RequiredLimit { get; set; }
        /// <summary>Required sort field role and direction (asc/desc). Null = no sort requirement.</summary>
        public string? RequiredSortField { get; set; }
        public string? RequiredSortDirection { get; set; }
        /// <summary>Required catalog operation (count/list/aggregate/breakdown/detail). Null = any.</summary>
        public string? RequiredOperation { get; set; }
        /// <summary>True if the requested wording is yes/no shaped (used by the grounding gate).</summary>
        public bool IsYesNoShape { get; set; }
        /// <summary>Free-form description used in trace messages.</summary>
        public string Description { get; set; } = string.Empty;
    }
}
