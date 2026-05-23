using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI;

public interface IAiInsightsService
{
    Task<AiOverviewKpis> GetDashboardOverviewAsync(AiInsightsFilter filters);
    Task<List<ReviewAttentionItem>> GetReviewAttentionItemsAsync(AiInsightsFilter filters);
    Task<ConfidenceInsights> GetConfidenceBreakdownAsync(AiInsightsFilter filters);
    Task<AttachmentEvidenceInsights> GetAttachmentInsightsAsync(AiInsightsFilter filters);
    Task<List<TrendInsight>> GetTrendDataAsync(AiInsightsFilter filters);
    Task<List<PatternInsight>> GetClassificationPatternsAsync(AiInsightsFilter filters);
    Task<List<PatternInsight>> GetPriorityPatternsAsync(AiInsightsFilter filters);
    Task<AiInsightsDashboardViewModel> GetDashboardAsync(AiInsightsFilter filters);
    Task<bool> CompleteReviewAsync(int analysisId, string notes, string reviewer);
}
