using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Services.AI.Copilot.Analysis
{
    // ─── DTO models for the trace analyzer ──────────────────────────────────────────────
    // Extracted 2026-05-08 from the bottom of CopilotTraceAnalyzer.cs (was 584-line file).
    // Pure POCOs — no behavior, no DI dependencies. The implementation in CopilotTraceAnalyzer
    // populates these and returns them. Keeping the namespace as the analyzer's so existing
    // callers don't need to change their using statements.

    public class TraceAnalysisReport
    {
        public int TotalTracesAnalyzed { get; set; }
        public DateTime AnalysisDate { get; set; }
        public Dictionary<string, int> IntentBreakdown { get; set; } = new();
        public Dictionary<string, int> ConfidenceDistribution { get; set; } = new();
        public ExecutionTimeStatistics ExecutionTimeStats { get; set; } = new();
        public List<RoutingPattern> RoutingPatternAnalysis { get; set; } = new();
        public List<QueryPlanInsight> QueryPlanAnalysis { get; set; } = new();
        public List<TraceIssue> IssuesFound { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
        public List<TraceSummary> Traces { get; set; } = new();
    }

    public class TraceSummary
    {
        public int Id { get; set; }
        public string Question { get; set; } = "";
        public string? Answer { get; set; }
        public string? Intent { get; set; }
        public long ExecutionTimeMs { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? ModelName { get; set; }
        public int? ContextCapacity { get; set; }
        public int? PromptChars { get; set; }
        public bool IsTruncated { get; set; }
    }

    public class ExecutionTimeStatistics
    {
        public long AverageMs { get; set; }
        public long MinMs { get; set; }
        public long MaxMs { get; set; }
        public long MedianMs { get; set; }
        public long P95Ms { get; set; }
    }

    public class RoutingPattern
    {
        public string Route { get; set; } = "";
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class QueryPlanInsight
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public DynamicQueryIntent Intent { get; set; }
        public bool RequiresClarification { get; set; }
        public bool HasExplicitFilters { get; set; }
        public int FilterCount { get; set; }
        public bool IsCompoundQuery { get; set; }
    }

    public class TraceIssue
    {
        public int TraceId { get; set; }
        public string IssueType { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Description { get; set; } = "";
        public string Question { get; set; } = "";
        public string? DetectedIntent { get; set; }
        public string? RouteReason { get; set; }
        public long? ExecutionTimeMs { get; set; }
        public string? GeneratedSql { get; set; }
    }

    public class TracePatternAnalysis
    {
        public double SuccessRate { get; set; }
        public List<QuestionPattern> CommonPatterns { get; set; } = new();
        public List<FailedQueryInfo> CommonIssues { get; set; } = new();
    }

    public class FailedQueryInfo
    {
        public string? Question { get; set; }
        public string? Intent { get; set; }
        public string? Answer { get; set; }
    }

    public class QuestionPattern
    {
        public string NormalizedForm { get; set; } = "";
        public int VariationCount { get; set; }
        public List<string> Examples { get; set; } = new();
    }

    public class AnomalousTrace
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public string? Answer { get; set; }
        public long ExecutionTimeMs { get; set; }
        public string? Intent { get; set; }
        public List<string> AnomalyReasons { get; set; } = new();
    }

    public class IntentDistributionReport
    {
        public List<IntentStats> Distribution { get; set; } = new();
        public int TotalTraces { get; set; }
    }

    public class IntentStats
    {
        public string Intent { get; set; } = "";
        public int Count { get; set; }
        public long AverageExecutionTimeMs { get; set; }
        public double Percentage { get; set; }
    }
}
