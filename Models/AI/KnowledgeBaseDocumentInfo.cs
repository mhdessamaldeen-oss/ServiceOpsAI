namespace AISupportAnalysisPlatform.Models.AI
{
    public class KnowledgeBaseDocumentInfo
    {
        public string Category { get; set; } = "";
        public string FileName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime UpdatedOnUtc { get; set; }
    }
}
