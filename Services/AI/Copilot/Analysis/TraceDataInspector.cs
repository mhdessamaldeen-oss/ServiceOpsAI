using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Services.AI.Copilot.Analysis
{
    /// <summary>
    /// Deep inspection of actual Copilot trace data from database
    /// Matches questions with answers to determine correctness
    /// </summary>
    public interface ITraceDataInspector
    {
        Task<List<TraceInspectionResult>> InspectRecentTracesAsync(int count = 50, int? sessionId = null);
        Task<TraceInspectionResult> InspectSingleTraceAsync(int traceId);
        Task<CorrectnessAnalysis> AnalyzeCorrectnessPatternsAsync(int count = 100, int? sessionId = null);
        Task<List<QuestionAnswerPair>> GetQuestionAnswerPairsAsync(int count = 50, int? sessionId = null);
    }

    public class TraceDataInspector : ITraceDataInspector
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<TraceDataInspector> _logger;

        public TraceDataInspector(
            ApplicationDbContext context,
            ILogger<TraceDataInspector> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<TraceInspectionResult>> InspectRecentTracesAsync(int count = 50, int? sessionId = null)
        {
            var query = _context.CopilotTraceHistories.AsQueryable();
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var traces = await query
                .OrderByDescending(t => t.CreatedAt)
                .Take(count)
                .ToListAsync();

            var results = new List<TraceInspectionResult>();
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var trace in traces)
            {
                AdminCopilotExecutionDetails? details = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(trace.ExecutionPlan))
                    {
                        details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(
                            trace.ExecutionPlan, jsonOptions);
                    }
                }
                catch { /* skip parse errors */ }

                var result = new TraceInspectionResult
                {
                    TraceId = trace.Id,
                    Question = trace.Question ?? "",
                    Answer = trace.Answer,
                    Intent = details?.DetectedIntent ?? "Unknown",
                    CreatedAt = trace.CreatedAt,
                    ExecutionTimeMs = trace.TotalElapsedMs,
                    Analysis = new TraceAnalysis()
                };

                if (details != null)
                {
                    result.HasExecutionDetails = true;
                    result.DetectedIntent = details.DetectedIntent;
                    result.Confidence = details.PlannerConfidence.ToString();
                    result.ResultCount = details.ResultCount ?? 0;
                    result.SqlQuery = details.SubExecutions?.FirstOrDefault()?.GeneratedSql;
                    result.IsLegacyCompat = details.SubExecutions?.Any(e => e.IsLegacyCompatOnly) ?? false;
                    result.RequiresClarification = details.QueryPlan?.RequiresClarification == true;

                    AnalyzeCorrectness(result, details, trace);
                }
                else
                {
                    result.HasExecutionDetails = false;
                    result.Analysis.CorrectnessIssues.Add("No execution details recorded");
                    result.Analysis.IsProbablyCorrect = false;
                }

                results.Add(result);
            }

            return results;
        }

        public async Task<TraceInspectionResult> InspectSingleTraceAsync(int traceId)
        {
            var results = await InspectRecentTracesAsync(1);
            return results.FirstOrDefault()!;
        }

        public async Task<CorrectnessAnalysis> AnalyzeCorrectnessPatternsAsync(int count = 100, int? sessionId = null)
        {
            var traces = await InspectRecentTracesAsync(count, sessionId);

            var analysis = new CorrectnessAnalysis
            {
                TotalAnalyzed = traces.Count,
                AnalysisDate = DateTime.UtcNow
            };

            analysis.CorrectAnswers = traces.Where(t => t.Analysis.IsCorrect).ToList();
            analysis.ProbablyCorrect = traces.Where(t => t.Analysis.IsProbablyCorrect && !t.Analysis.IsCorrect).ToList();
            analysis.IncorrectAnswers = traces.Where(t => !t.Analysis.IsCorrect && !t.Analysis.IsProbablyCorrect).ToList();

            analysis.ByIntent = traces
                .GroupBy(t => t.Intent ?? "Unknown")
                .Select(g => new IntentCorrectnessAnalysis
                {
                    Intent = g.Key,
                    TotalCount = g.Count(),
                    CorrectCount = g.Count(t => t.Analysis.IsCorrect),
                    IncorrectCount = g.Count(t => !t.Analysis.IsCorrect && !t.Analysis.IsProbablyCorrect),
                    AverageScore = g.Average(t => t.Analysis.CorrectnessScore)
                })
                .OrderByDescending(x => x.TotalCount)
                .ToList();

            var allIssues = traces.SelectMany(t => t.Analysis.CorrectnessIssues).ToList();
            analysis.TopIssues = allIssues
                .GroupBy(i => i)
                .Select(g => new IssuePattern { Issue = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToList();

            analysis.FailedExamples = analysis.IncorrectAnswers
                .Take(5)
                .Select(t => new FailedExample
                {
                    TraceId = t.TraceId,
                    Question = t.Question,
                    Answer = t.Answer,
                    WhyFailed = string.Join(", ", t.Analysis.CorrectnessIssues),
                    SuggestedFix = SuggestFix(t)
                })
                .ToList();

            return analysis;
        }

        public async Task<List<QuestionAnswerPair>> GetQuestionAnswerPairsAsync(int count = 50, int? sessionId = null)
        {
            var query = _context.CopilotTraceHistories.AsQueryable();
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var tracesData = await query
                .OrderByDescending(t => t.CreatedAt)
                .Take(count)
                .Select(t => new { t.Id, t.Question, t.Answer, t.ExecutionPlan, t.CreatedAt })
                .ToListAsync();

            var results = tracesData.Select(t => {
                AdminCopilotExecutionDetails? details = null;
                try {
                    details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(t.ExecutionPlan, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                } catch { }

                return new QuestionAnswerPair
                {
                    TraceId = t.Id,
                    Question = t.Question ?? "",
                    Answer = t.Answer,
                    Intent = details?.DetectedIntent ?? "Unknown",
                    IsSuccess = !string.IsNullOrWhiteSpace(t.Answer),
                    CreatedAt = t.CreatedAt
                };
            }).ToList();

            return results;
        }

        private void AnalyzeCorrectness(TraceInspectionResult result, AdminCopilotExecutionDetails? details, CopilotTraceHistory trace)
        {
            var analysis = result.Analysis;
            analysis.CorrectnessScore = 0;

            if (details?.Steps?.All(s => s.Status != CopilotStepStatus.Error) == true)
            {
                analysis.CorrectnessScore += 25;
            }
            else
            {
                analysis.CorrectnessIssues.Add("Execution failed or had errors");
            }

            if (details?.QueryPlan != null)
            {
                var intentMatch = DoesAnswerMatchIntent(trace.Question ?? "", trace.Answer, details.QueryPlan, details.ResultCount);
                if (intentMatch)
                {
                    analysis.CorrectnessScore += 25;
                }
                else
                {
                    analysis.CorrectnessIssues.Add("Answer does not match question intent");
                }
            }
            else if (details?.QueryPlan?.RequiresClarification == true)
            {
                analysis.CorrectnessScore += 20;
                analysis.CorrectnessIssues.Add("Required user clarification - ambiguous question");
            }
            else
            {
                analysis.CorrectnessIssues.Add("No query plan generated");
            }

            if (!string.IsNullOrWhiteSpace(trace.Answer))
            {
                if (trace.Answer.Contains("I don't understand") || 
                    trace.Answer.Contains("Unable to") ||
                    trace.Answer.Contains("I cannot"))
                {
                    analysis.CorrectnessIssues.Add("Copilot indicated it couldn't answer");
                }
                else if (trace.Answer.Length > 20)
                {
                    analysis.CorrectnessScore += 25;
                }
                else
                {
                    analysis.CorrectnessScore += 10;
                    analysis.CorrectnessIssues.Add("Answer is very short/possibly incomplete");
                }
            }
            else
            {
                analysis.CorrectnessIssues.Add("No answer provided");
            }

            if (details?.ResultCount.HasValue == true && details.ResultCount.Value > 0)
            {
                analysis.CorrectnessScore += 25;
                analysis.HasDataResults = true;
            }
            else if (details?.QueryPlan?.Intent.ToString().Equals("Count", StringComparison.OrdinalIgnoreCase) == true && details?.ResultCount.HasValue == true && details.ResultCount.Value == 0)
            {
                analysis.CorrectnessScore += 25;
                analysis.HasDataResults = true;
            }
            else if (details?.QueryPlan != null)
            {
                analysis.CorrectnessIssues.Add("Query executed but returned no results");
                analysis.HasDataResults = false;
            }

            analysis.IsCorrect = analysis.CorrectnessScore >= 90;
            analysis.IsProbablyCorrect = analysis.CorrectnessScore >= 70 && analysis.CorrectnessScore < 90;
            
            if (result.IsLegacyCompat)
            {
                analysis.CorrectnessIssues.Add("Used legacy compatibility mode");
            }

            if (details?.PlannerConfidence == RoutingConfidence.Low)
            {
                analysis.CorrectnessIssues.Add("Low confidence intent detection");
            }
        }

        private bool DoesAnswerMatchIntent(string question, string? answer, AdminCopilotDynamicTicketQueryPlan plan, int? resultCount)
        {
            var questionLower = question.ToLower();
            var answerLower = answer?.ToLower() ?? "";
            var planIntentStr = plan.Intent.ToString();

            if (questionLower.Contains("how many") || questionLower.Contains("count") || questionLower.Contains("number of"))
            {
                var hasNumber = answerLower.Any(char.IsDigit);
                if (!hasNumber && !resultCount.HasValue)
                    return false;
            }

            if ((questionLower.Contains("list") || questionLower.Contains("show") || questionLower.Contains("get")) 
                && planIntentStr.Equals("List", StringComparison.OrdinalIgnoreCase))
            {
                if (!resultCount.HasValue && !answerLower.Contains("no ") && !answerLower.Contains("none"))
                    return false;
            }

            return true;
        }

        private string SuggestFix(TraceInspectionResult trace)
        {
            if (trace.Analysis.CorrectnessIssues.Any(i => i.Contains("legacy")))
                return "Review the AnalystAgent semantic layer for better entity grounding";
            
            if (trace.Analysis.CorrectnessIssues.Any(i => i.Contains("clarification")))
                return "Add more training examples for this question pattern";
            
            if (trace.Analysis.CorrectnessIssues.Any(i => i.Contains("no results")))
                return "Check SQL query - may have incorrect filters";

            return "Review execution details for specific error";
        }
    }

    public class TraceInspectionResult
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public string? Answer { get; set; }
        public string? Intent { get; set; }
        public string? DetectedIntent { get; set; }
        public string? Confidence { get; set; }
        public DateTime CreatedAt { get; set; }
        public long ExecutionTimeMs { get; set; }
        public int? ResultCount { get; set; }
        public string? SqlQuery { get; set; }
        public bool HasExecutionDetails { get; set; }
        public bool IsLegacyCompat { get; set; }
        public bool RequiresClarification { get; set; }
        public TraceAnalysis Analysis { get; set; } = new();
    }

    public class TraceAnalysis
    {
        public int CorrectnessScore { get; set; } // 0-100
        public bool IsCorrect { get; set; }
        public bool IsProbablyCorrect { get; set; }
        public bool HasDataResults { get; set; }
        public List<string> CorrectnessIssues { get; set; } = new();
    }

    public class CorrectnessAnalysis
    {
        public int TotalAnalyzed { get; set; }
        public DateTime AnalysisDate { get; set; }
        public List<TraceInspectionResult> CorrectAnswers { get; set; } = new();
        public List<TraceInspectionResult> ProbablyCorrect { get; set; } = new();
        public List<TraceInspectionResult> IncorrectAnswers { get; set; } = new();
        public List<IntentCorrectnessAnalysis> ByIntent { get; set; } = new();
        public List<IssuePattern> TopIssues { get; set; } = new();
        public List<FailedExample> FailedExamples { get; set; } = new();
    }

    public class IntentCorrectnessAnalysis
    {
        public string Intent { get; set; } = "";
        public int TotalCount { get; set; }
        public int CorrectCount { get; set; }
        public int IncorrectCount { get; set; }
        public double AverageScore { get; set; }
    }

    public class IssuePattern
    {
        public string Issue { get; set; } = "";
        public int Count { get; set; }
    }

    public class FailedExample
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public string? Answer { get; set; }
        public string WhyFailed { get; set; } = "";
        public string SuggestedFix { get; set; } = "";
    }

    public class QuestionAnswerPair
    {
        public int TraceId { get; set; }
        public string Question { get; set; } = "";
        public string? Answer { get; set; }
        public string? Intent { get; set; }
        public bool? IsSuccess { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
