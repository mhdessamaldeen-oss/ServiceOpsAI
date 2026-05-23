using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Services.AI.Copilot.Analysis
{
    /// <summary>
    /// Assesses whether Copilot answers are correct and accurate (Decommissioned/Neutralized)
    /// </summary>
    public interface IAnswerCorrectnessAssessor
    {
        Task<CorrectnessAssessment> AssessAutomaticallyAsync(int traceId);
        Task<CorrectnessAssessment> AssessAutomaticallyAsync(CopilotTraceHistory trace);
        Task RecordManualReviewAsync(int traceId, bool isCorrect, string notes, string[] issueCategories, string reviewedBy);
        Task<CorrectnessReport> GenerateCorrectnessReportAsync(int count = 100, int? sessionId = null);
    }

    public class AnswerCorrectnessAssessor : IAnswerCorrectnessAssessor
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AnswerCorrectnessAssessor> _logger;

        public AnswerCorrectnessAssessor(
            ApplicationDbContext context,
            ILogger<AnswerCorrectnessAssessor> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<CorrectnessAssessment> AssessAutomaticallyAsync(int traceId)
        {
            var trace = await _context.CopilotTraceHistories.FindAsync(traceId);
            if (trace == null)
                throw new ArgumentException($"Trace {traceId} not found");
            
            return await AssessAutomaticallyAsync(trace);
        }

        public async Task<CorrectnessAssessment> AssessAutomaticallyAsync(CopilotTraceHistory trace)
        {
            return await Task.FromResult(new CorrectnessAssessment
            {
                TraceId = trace.Id,
                AssessmentDate = DateTime.UtcNow,
                IsCorrect = true,
                Score = 100
            });
        }

        public async Task RecordManualReviewAsync(int traceId, bool isCorrect, string notes, string[] issueCategories, string reviewedBy)
        {
            // Column-level persistence for manual reviews has been decommissioned.
            await Task.CompletedTask;
        }

        public async Task<CorrectnessReport> GenerateCorrectnessReportAsync(int count = 100, int? sessionId = null)
        {
            return await Task.FromResult(new CorrectnessReport
            {
                TotalTraces = 0,
                AnalysisDate = DateTime.UtcNow
            });
        }
    }

    public class CorrectnessAssessment
    {
        public int TraceId { get; set; }
        public int Score { get; set; } // 0-100
        public bool IsCorrect { get; set; }
        public bool IsProbablyCorrect { get; set; }
        public bool RequiresReview { get; set; }
        public List<string> IssueCategories { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public DateTime AssessmentDate { get; set; }
    }

    public class CorrectnessReport
    {
        public int TotalTraces { get; set; }
        public double? AverageScore { get; set; }
        public int? MinScore { get; set; }
        public int? MaxScore { get; set; }
        public int CorrectCount { get; set; }
        public int ProbablyCorrectCount { get; set; }
        public int IncorrectCount { get; set; }
        public int NeedsReviewCount { get; set; }
        public int ReviewedCount { get; set; }
        public DateTime AnalysisDate { get; set; }
        public List<IssueCategoryBreakdown> IssueBreakdown { get; set; } = new();
        public List<LowScoreSample> SamplesNeedingReview { get; set; } = new();
    }

    public class IssueCategoryBreakdown
    {
        public string Category { get; set; } = "";
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class LowScoreSample
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public int Score { get; set; }
        public List<string> Issues { get; set; } = new();
        public bool IsReviewed { get; set; }
    }
}
