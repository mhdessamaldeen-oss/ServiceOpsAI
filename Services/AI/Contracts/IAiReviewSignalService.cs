using AISupportAnalysisPlatform.Models;

namespace AISupportAnalysisPlatform.Services.AI;

public interface IAiReviewSignalService
{
    bool NeedsReview(TicketAiAnalysis analysis, int totalRunCount, string language = "English", IReadOnlyDictionary<string, int>? priorityLevels = null);
    List<string> CalculateReviewReasons(TicketAiAnalysis analysis, int totalRunCount, string language = "English", IReadOnlyDictionary<string, int>? priorityLevels = null);
}
