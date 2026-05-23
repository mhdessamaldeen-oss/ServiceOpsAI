using System;
using System.Collections.Generic;
using AISupportAnalysisPlatform.Models.Common;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class AiInsightsDashboardViewModel
    {
        public AiInsightsFilter Filter { get; set; } = new();
        public AiOverviewKpis Kpis { get; set; } = new();
        public PagedResult<ReviewAttentionItem> ReviewItems { get; set; } = new();
        public List<PatternInsight> ClassificationPatterns { get; set; } = new();
        public List<PatternInsight> PriorityPatterns { get; set; } = new();
        public ConfidenceInsights ConfidenceData { get; set; } = new();
        public AttachmentEvidenceInsights AttachmentInsights { get; set; } = new();
        public List<TrendInsight> VolumeTrends { get; set; } = new();
        
        // Filter Dropdowns
        public SelectList StatusList { get; set; } = null!;
        public SelectList ClassificationList { get; set; } = null!;
        public SelectList PriorityList { get; set; } = null!;
        public SelectList AiClassificationList { get; set; } = null!;
        public SelectList AiPriorityList { get; set; } = null!;
    }

    public class AiOverviewKpis
    {
        public int TotalAnalyses { get; set; }
        public int NeedsReview { get; set; }
        public int LowConfidence { get; set; }
        public int UnknownClassification { get; set; }
        public int ClassificationMismatch { get; set; }
        public int PriorityMismatch { get; set; }
        public int StrongAttachmentEvidence { get; set; }
        public int MultipleReRuns { get; set; }
    }

    public class ReviewAttentionItem
    {
        public int AnalysisId { get; set; }
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CurrentClassification { get; set; } = string.Empty;
        public string AiClassification { get; set; } = string.Empty;
        public string CurrentPriority { get; set; } = string.Empty;
        public string AiPriority { get; set; } = string.Empty;
        public string Confidence { get; set; } = string.Empty;
        public List<string> ReviewReasons { get; set; } = new();
        public bool HasAttachments { get; set; }
        public DateTime LastAiRun { get; set; }
    }

    public class PatternInsight
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class TrendInsight
    {
        public string DateLabel { get; set; } = string.Empty;
        public int AnalysisVolume { get; set; }
        public int NeedsReviewVolume { get; set; }
    }

    public class ConfidenceInsights
    {
        public int High { get; set; }
        public int Medium { get; set; }
        public int Low { get; set; }
        public double PercentageLow => (High + Medium + Low) == 0 ? 0 : Math.Round((double)Low / (High + Medium + Low) * 100, 1);
    }

    public class AttachmentEvidenceInsights
    {
        public int TicketsWithAiAttachments { get; set; }
        public int TicketsWithHighRelevanceAttachments { get; set; }
        public List<PatternInsight> MostRelevantAttachments { get; set; } = new();
    }
}
