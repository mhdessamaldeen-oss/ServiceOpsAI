namespace AISupportAnalysisPlatform.Models.AI
{
    public class CopilotDataQueryOptions
    {
        public const string SectionName = "Copilot:DataQuery";

        public int? SqlCommandTimeoutSeconds { get; set; }
    }
}
