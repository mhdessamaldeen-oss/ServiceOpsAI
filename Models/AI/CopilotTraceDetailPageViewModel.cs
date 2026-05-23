namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotTraceDetailPageViewModel
    {
        public required CopilotTraceHistory Trace { get; set; }

        public required AdminCopilotExecutionDetails ExecutionDetails { get; set; }

        public int ArchiveCount { get; set; }

        public string DefaultTraceView { get; set; } = "story";
    }
}
