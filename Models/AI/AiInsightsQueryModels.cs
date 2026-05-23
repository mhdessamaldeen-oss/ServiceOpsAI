using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Models.AI;

internal sealed class FilteredAnalysisData
{
    public required List<AnalysisSnapshot> LatestAnalyses { get; init; }
    public required Dictionary<int, int> RunCounts { get; init; }
    // Loaded once from the user-managed TicketPriority reference data. Lets the
    // review-signal service compare AI-suggested priority names to numeric levels
    // without hardcoding a High=3/Medium=2/Low=1 scale that would silently ignore
    // any "Critical" tier an operator added via Reference Data UI.
    public required IReadOnlyDictionary<string, int> PriorityLevels { get; init; }
}

internal sealed class AnalysisSnapshot
{
    public required int AnalysisId { get; init; }
    public required int TicketId { get; init; }
    public required int RunNumber { get; init; }
    public required DateTime CreatedOn { get; init; }
    public required AiConfidenceLevel ConfidenceLevel { get; init; }
    public required string SuggestedClassification { get; init; }
    public required string SuggestedPriority { get; init; }
    public required string TicketNumber { get; init; }
    public required string Title { get; init; }
    public required string CurrentClassification { get; init; }
    public required string CurrentPriority { get; init; }
    public required int CurrentPriorityLevel { get; init; }
    public required int DescriptionLength { get; init; }
    public required int TicketAttachmentCount { get; init; }
    public int RunCount { get; set; }
    public bool IsReviewCompleted { get; set; }
    public List<AttachmentSummarySnapshot> AttachmentSummaries { get; set; } = new();
}

internal sealed class AttachmentSummarySnapshot
{
    public required int AnalysisId { get; init; }
    public required string FileName { get; init; }
    public required AiRelevanceLevel Relevance { get; init; }
}
