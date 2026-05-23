namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotTracePersistenceOptions
    {
        public const string SectionName = "Copilot:TracePersistence";

        public int? StructuredRowLimit { get; set; }
        public int? SubExecutionLimit { get; set; }
        public int? TextLengthLimit { get; set; }
        public string? TruncatedTextSuffix { get; set; }
    }
}
