namespace AISupportAnalysisPlatform.Models
{
    public class SemanticSearchMatch
    {
        public required Ticket Ticket { get; set; }
        public float Score { get; set; }
        public float VectorScore { get; set; }
        public float LexicalScore { get; set; }
        public float LanguageBoost { get; set; }
    }
}
