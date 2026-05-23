namespace ServiceOpsAI.Services.AI.Common
{
    /// <summary>
    /// Static lookups + thresholds used by the Investigation layer (SemanticSearchService,
    /// KnowledgeBaseRagService). Extracted from the legacy CopilotHeuristicCatalog so the
    /// rest of that catalog can be deleted with the legacy Copilot tree.
    /// </summary>
    public static class InvestigationHeuristics
    {
        public static readonly HashSet<string> EnglishStopWords = new(StringComparer.Ordinal)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it",
            "of", "on", "or", "that", "the", "this", "to", "was", "were", "with"
        };

        public static readonly HashSet<string> ArabicStopWords = new(StringComparer.Ordinal)
        {
            "في", "من", "على", "الى", "إلى", "عن", "مع", "تم", "هذا", "هذه", "ذلك", "تلك",
            "هناك", "عند", "بعد", "قبل", "كل", "كان", "كانت", "هو", "هي", "ثم", "او", "أو"
        };

        public static readonly string[] KnowledgeBaseIndexedFolders = ["SOPs", "KnownIssues", "Troubleshooting", "ReleaseNotes"];

        public const float KnowledgeBaseScoreThreshold = 0.25f;
        public const float SemanticSearchBaseThreshold = 0.05f;
        public const float SemanticSearchMaxThreshold = 0.40f;
    }
}
