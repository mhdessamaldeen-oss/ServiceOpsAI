using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Services.Infrastructure;
using AISupportAnalysisPlatform.Constants;

namespace AISupportAnalysisPlatform.Services.AI
{
    public class AiReviewSignalService : IAiReviewSignalService
    {
        private readonly ILocalizationService _localizer;

        public AiReviewSignalService(ILocalizationService localizer)
        {
            _localizer = localizer;
        }

        public bool NeedsReview(TicketAiAnalysis analysis, int totalRunCount, string language = "English", IReadOnlyDictionary<string, int>? priorityLevels = null)
        {
            return CalculateReviewReasons(analysis, totalRunCount, language, priorityLevels).Any();
        }

        public List<string> CalculateReviewReasons(TicketAiAnalysis analysis, int totalRunCount, string language = "English", IReadOnlyDictionary<string, int>? priorityLevels = null)
        {
            var reasons = new List<string>();
            var actualCat = analysis.Ticket?.Category?.Name ?? _localizer.Get(nameof(SystemStrings.None), language);
            var actualLevel = analysis.Ticket?.Priority?.Level ?? 0;

            // AI-suggested priority is just a string (e.g. "High", "Critical"). We need
            // the actual numeric level from the user-managed TicketPriority reference
            // data to compare against the ticket's current level. The caller supplies
            // a name→level map loaded from the DB; if absent, we skip the AI-priority
            // signal rather than fall back to a hardcoded scale that would silently
            // ignore custom priority tiers (e.g. "Critical") an operator may have added.
            var aiLevelStr = analysis.SuggestedPriority ?? TicketPriorityNames.Medium;
            int? aiLevel = priorityLevels != null && priorityLevels.TryGetValue(aiLevelStr, out var lvl)
                ? lvl
                : null;

            if (analysis.ConfidenceLevel == AiConfidenceLevel.Low)
                reasons.Add(_localizer.Get("LowConfidence", language));

            if (analysis.SuggestedClassification == nameof(SystemStrings.Unknown))
                reasons.Add(_localizer.Get("UnknownClassification", language));

            if (analysis.SuggestedClassification != nameof(SystemStrings.Unknown) && analysis.SuggestedClassification != actualCat)
                reasons.Add(_localizer.Get("ClassificationMismatch", language));

            if (aiLevel.HasValue && aiLevel.Value > actualLevel)
                reasons.Add(_localizer.Get("AiPriorityHigher", language));

            // Tier 4.1 — only flag when the run count exceeds the success-run count.
            // A user-initiated refresh (totalRunCount=2 with both successful) is NOT a
            // signal of trouble; we only care about analyses that needed retries to land.
            // analysis.RunNumber is the LATEST run's index; if totalRunCount > RunNumber,
            // there was at least one failed-then-retried run before the success.
            var latestRunIndex = analysis.RunNumber;
            if (latestRunIndex > 0 && totalRunCount > latestRunIndex)
                reasons.Add(_localizer.Get("MultipleRuns", language));

            return reasons;
        }
    }
}
