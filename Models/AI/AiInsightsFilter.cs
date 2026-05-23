using System;
using AISupportAnalysisPlatform.Models.Common;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class AiInsightsFilter
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? TicketStatus { get; set; }
        public string? CurrentClassification { get; set; }
        public string? AiSuggestedClassification { get; set; }
        public string? CurrentPriority { get; set; }
        public string? AiSuggestedPriority { get; set; }
        public string? ConfidenceLevel { get; set; }
        public bool NeedsReviewOnly { get; set; }
        public bool HasAttachmentsOnly { get; set; }
        public bool MismatchOnly { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = GridRequestModel.DefaultPageSize;

        public void Normalize()
        {
            PageNumber = Math.Max(1, PageNumber);

            if (PageSize == GridRequestModel.FullDataPageSize)
            {
                return;
            }

            if (PageSize <= 0 || PageSize > GridRequestModel.MaxPageSize)
            {
                PageSize = GridRequestModel.DefaultPageSize;
            }
        }

        public int GetEffectivePageSize(int totalCount)
        {
            Normalize();

            if (PageSize == GridRequestModel.FullDataPageSize)
            {
                return totalCount > 0 ? totalCount : GridRequestModel.DefaultPageSize;
            }

            return PageSize;
        }
    }
}
