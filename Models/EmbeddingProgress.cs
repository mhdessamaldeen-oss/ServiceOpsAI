namespace AISupportAnalysisPlatform.Models
{
    /// <summary>
    /// Tracks the real-time progress of a bulk embedding operation.
    /// </summary>
    public class EmbeddingProgress
    {
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int? CurrentTicketId { get; set; }
        public bool IsRunning { get; set; }
        public string? LastErrorMessage { get; set; }
        public int ProcessedCount => CompletedCount + FailedCount;
        public double ProgressPercent => TotalCount == 0 ? 0 : Math.Round((double)ProcessedCount / TotalCount * 100, 1);
    }
}
