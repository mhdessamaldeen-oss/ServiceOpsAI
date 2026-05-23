namespace AISupportAnalysisPlatform.Models.AI
{
    public class KnowledgeBaseChunkMatch
    {
        public string DocumentName { get; set; } = "";
        public string SectionTitle { get; set; } = "";
        public string Excerpt { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public float Score { get; set; }
        public float VectorScore { get; set; }
        public float LexicalScore { get; set; }
    }
}
