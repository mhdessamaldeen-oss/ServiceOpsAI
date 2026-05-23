namespace ServiceOpsAI.Models.AI
{
    /// <summary>
    /// Temporal filter for date/time range queries
    /// </summary>
    public class TemporalFilter
    {
        /// <summary>Type of temporal expression</summary>
        public TemporalType Type { get; set; }
        
        /// <summary>Number of days for LastXDays type</summary>
        public int? Days { get; set; }
        
        /// <summary>Absolute start date (if specified)</summary>
        public DateTime? StartDate { get; set; }
        
        /// <summary>Absolute end date (if specified)</summary>
        public DateTime? EndDate { get; set; }
        
        /// <summary>Field to apply filter to (default: CreatedAt)</summary>
        public string FieldName { get; set; } = "CreatedAt";
        
        /// <summary>Original text that was parsed</summary>
        public string? OriginalText { get; set; }
    }
    
    /// <summary>
    /// Types of temporal expressions supported
    /// </summary>
    public enum TemporalType
    {
        /// <summary>Last X days (e.g., "last 7 days", "past week")</summary>
        LastXDays,
        
        /// <summary>This calendar week</summary>
        ThisWeek,
        
        /// <summary>This calendar month</summary>
        ThisMonth,
        
        /// <summary>Yesterday</summary>
        Yesterday,
        
        /// <summary>Today</summary>
        Today,
        
        /// <summary>Any past date (unspecified, default: 7 days)</summary>
        Past,
        
        /// <summary>Recent (configurable, default: 30 days)</summary>
        Recent,
        
        /// <summary>Specific date range provided</summary>
        SpecificRange,
        
        /// <summary>No temporal filter detected</summary>
        None
    }
}
