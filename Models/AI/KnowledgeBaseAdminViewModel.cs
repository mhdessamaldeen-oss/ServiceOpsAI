namespace AISupportAnalysisPlatform.Models.AI
{
    public class KnowledgeBaseAdminViewModel
    {
        public string RootPath { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public List<KnowledgeBaseDocumentInfo> Documents { get; set; } = new();
    }
}
