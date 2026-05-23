namespace AISupportAnalysisPlatform.Models.DTOs
{
    public class BenchmarkResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int SummaryId { get; set; }
    }

    public class TraceDetailsDto
    {
        public string TraceId { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public string EvidenceStrength { get; set; } = string.Empty;
        public string ResponseMode { get; set; } = string.Empty;
        public string? UsedTool { get; set; }
        public string? ModelName { get; set; }
        public string? Notes { get; set; }
        public object? ExecutionDetails { get; set; }
        public object? DynamicQueryPlan { get; set; }
        public List<object> DynamicTicketResults { get; set; } = new();
        public List<object> KnowledgeMatches { get; set; } = new();
        public List<object> SimilarTickets { get; set; } = new();
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class ProvisioningStatusDto
    {
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public int Progress { get; set; }
        public bool IsComplete { get; set; }
        public bool IsFailed { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class ProvisioningLogDto
    {
        public string Status { get; set; } = string.Empty;
        public string? StatusLabel { get; set; }
        public List<string> Logs { get; set; } = new();
    }

    public class BenchmarkDto
    {
        public string Version { get; set; } = string.Empty;
        public DateTime CreatedOnUtc { get; set; }
        public int CaseCount { get; set; }
        public List<BenchmarkCaseDto> Cases { get; set; } = new();
    }

    public class BenchmarkCaseDto
    {
        public string Id { get; set; } = string.Empty;
        public string? Bucket { get; set; }
        public string? QueryLanguage { get; set; }
        public string? QueryText { get; set; }
        public int? SourceTicketId { get; set; }
        public int Count { get; set; }
        public bool IncludeAllStatuses { get; set; }
        public List<int>? StatusIds { get; set; }
        public List<int>? ExpectedTicketIds { get; set; }
        public string? Intent { get; set; }
        public string? Notes { get; set; }
    }

    public class RetrievalBenchmarkResultDto
    {
        public string Version { get; set; } = string.Empty;
        public DateTime RunOnUtc { get; set; }
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public double HitRate { get; set; }
        public List<object> Results { get; set; } = new();
    }

    public class BenchmarkHistoryDto
    {
        public int Id { get; set; }
        public DateTime RunOnUtc { get; set; }
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public double HitRate { get; set; }
        public string Version { get; set; } = string.Empty;
        public string? SettingsJson { get; set; }
        public string? ResultsJson { get; set; }
    }

    public class BenchmarkValidationDto
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class BatchProgressDto
    {
        public string BatchId { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int ProcessedCount { get; set; }
        public double ProgressPercent { get; set; }
        public int? CurrentTicketId { get; set; }
        public bool IsRunning { get; set; }
        public int QueueLength { get; set; }
    }

    public class TicketQueueStatusDto
    {
        public int TicketId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? StatusLabel { get; set; }
        public string EnqueuedAt { get; set; } = string.Empty;
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
    }
}
