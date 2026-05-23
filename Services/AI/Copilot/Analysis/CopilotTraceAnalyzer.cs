using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Services.AI.Copilot.Analysis
{
    /// <summary>
    /// Deep dive analyzer for Copilot trace history
    /// Investigates why answers were given in certain ways
    /// </summary>
    public interface ICopilotTraceAnalyzer
    {
        Task<TraceAnalysisReport> AnalyzeRecentTracesAsync(int count = 100, int? sessionId = null);
        Task<TracePatternAnalysis> IdentifyPatternsAsync(int? sessionId = null);
        Task<List<AnomalousTrace>> FindAnomaliesAsync(int? sessionId = null);
        Task<IntentDistributionReport> GetIntentDistributionAsync(int? sessionId = null);
    }

    public class CopilotTraceAnalyzer : ICopilotTraceAnalyzer
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<CopilotTraceAnalyzer> _logger;

        public CopilotTraceAnalyzer(
            ApplicationDbContext context,
            ILogger<CopilotTraceAnalyzer> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TraceAnalysisReport> AnalyzeRecentTracesAsync(int count = 100, int? sessionId = null)
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

            var report = new TraceAnalysisReport
            {
                TotalTracesAnalyzed = traces.Count,
                AnalysisDate = DateTime.UtcNow,
                IntentBreakdown = new Dictionary<string, int>(),
                ConfidenceDistribution = new Dictionary<string, int>(),
                ExecutionTimeStats = new ExecutionTimeStatistics(),
                RoutingPatternAnalysis = new List<RoutingPattern>(),
                QueryPlanAnalysis = new List<QueryPlanInsight>(),
                IssuesFound = new List<TraceIssue>(),
                Recommendations = new List<string>()
            };

            var jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            var executionTimes = new List<long>();
            var planTypes = new Dictionary<string, int>();
            var routingReasons = new Dictionary<string, int>();

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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse trace {TraceId}", trace.Id);
                    continue;
                }

                var intent = details?.DetectedIntent ?? "Unknown";
                report.IntentBreakdown[intent] = report.IntentBreakdown.GetValueOrDefault(intent) + 1;

                var confidence = details?.PlannerConfidence.ToString() ?? "Unknown";
                report.ConfidenceDistribution[confidence] = report.ConfidenceDistribution.GetValueOrDefault(confidence) + 1;

                executionTimes.Add(trace.TotalElapsedMs);

                if (details != null)
                {
                    var routingKey = $"{details.DetectedIntent} -> {details.RouteReason}";
                    routingReasons[routingKey] = routingReasons.GetValueOrDefault(routingKey) + 1;

                    AnalyzeForIssues(trace, details, report.IssuesFound);

                    if (details.QueryPlan != null)
                    {
                        var planType = details.QueryPlan.Intent.ToString();
                        planTypes[planType] = planTypes.GetValueOrDefault(planType) + 1;

                        report.QueryPlanAnalysis.Add(new QueryPlanInsight
                        {
                            TraceId = trace.Id,
                            Question = trace.Question,
                            Intent = details.QueryPlan.Intent,
                            RequiresClarification = details.QueryPlan.RequiresClarification,
                            HasExplicitFilters = details.QueryPlan.HasExplicitTargetView ||
                                                details.QueryPlan.HasExplicitLimit ||
                                                details.QueryPlan.HasExplicitSort,
                            FilterCount = details.QueryPlan.GlobalFilters?.Count ?? 0,
                            IsCompoundQuery = details.QueryPlans?.Count > 1
                        });
                    }

                    if (details.SubExecutions?.Any(e => e.IsLegacyCompatOnly) == true)
                    {
                        report.IssuesFound.Add(new TraceIssue
                        {
                            TraceId = trace.Id,
                            IssueType = "LegacyCompatOnly",
                            Severity = "Warning",
                            Description = "Query fell back to legacy compatibility layer instead of semantic intent plan",
                            Question = trace.Question
                        });
                    }
                }

                report.Traces.Add(new TraceSummary
                {
                    Id = trace.Id,
                    Question = trace.Question,
                    Answer = trace.Answer,
                    Intent = details?.DetectedIntent ?? "Unknown",
                    ExecutionTimeMs = trace.TotalElapsedMs,
                    CreatedAt = trace.CreatedAt,
                    ModelName = trace.ModelName ?? details?.DetectedIntent,
                    ContextCapacity = details?.ModelCapacity,
                    PromptChars = details?.PromptLength,
                    IsTruncated = details?.IsTruncated ?? false
                });
            }

            if (executionTimes.Any())
            {
                report.ExecutionTimeStats = new ExecutionTimeStatistics
                {
                    AverageMs = (long)executionTimes.Average(),
                    MinMs = executionTimes.Min(),
                    MaxMs = executionTimes.Max(),
                    MedianMs = executionTimes.OrderBy(t => t).ElementAt(executionTimes.Count / 2),
                    P95Ms = executionTimes.OrderBy(t => t).ElementAt((int)(executionTimes.Count * 0.95))
                };
            }

            report.RoutingPatternAnalysis = routingReasons
                .Select(r => new RoutingPattern
                {
                    Route = r.Key,
                    Count = r.Value,
                    Percentage = (double)r.Value / traces.Count * 100
                })
                .OrderByDescending(r => r.Count)
                .ToList();

            GenerateRecommendations(report);

            return report;
        }

        public async Task<TracePatternAnalysis> IdentifyPatternsAsync(int? sessionId = null)
        {
            var query = _context.CopilotTraceHistories.AsQueryable();
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var traces = await query
                .OrderByDescending(t => t.CreatedAt)
                .Take(200)
                .ToListAsync();

            var analysis = new TracePatternAnalysis();
            var jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            var questionPatterns = new Dictionary<string, List<string>>();
            var successfulQueries = new List<CopilotTraceHistory>();
            var failedQueries = new List<Tuple<CopilotTraceHistory, AdminCopilotExecutionDetails?>>();

            foreach (var trace in traces)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(trace.ExecutionPlan))
                    {
                        var details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(
                            trace.ExecutionPlan, jsonOptions);

                        if (details?.ResultCount > 0 || 
                            (details?.Steps?.LastOrDefault()?.Status == CopilotStepStatus.Ok))
                        {
                            successfulQueries.Add(trace);
                        }
                        else
                        {
                            failedQueries.Add(Tuple.Create(trace, details));
                        }

                        var normalizedQuestion = NormalizeQuestion(trace.Question);
                        if (!questionPatterns.ContainsKey(normalizedQuestion))
                        {
                            questionPatterns[normalizedQuestion] = new List<string>();
                        }
                        questionPatterns[normalizedQuestion].Add(trace.Question);
                    }
                }
                catch { /* skip malformed */ }
            }

            analysis.SuccessRate = traces.Count == 0 ? 0 : (double)successfulQueries.Count / traces.Count * 100;
            analysis.CommonPatterns = questionPatterns
                .Where(p => p.Value.Count > 1)
                .Select(p => new QuestionPattern
                {
                    NormalizedForm = p.Key,
                    VariationCount = p.Value.Count,
                    Examples = p.Value.Take(3).ToList()
                })
                .OrderByDescending(p => p.VariationCount)
                .ToList();

            analysis.CommonIssues = failedQueries
                .Select(t => new FailedQueryInfo { Question = t.Item1.Question, Intent = t.Item2?.DetectedIntent ?? "Unknown", Answer = t.Item1.Answer })
                .Take(10)
                .ToList();

            return analysis;
        }

        public async Task<List<AnomalousTrace>> FindAnomaliesAsync(int? sessionId = null)
        {
            var query = _context.CopilotTraceHistories.AsQueryable();
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var traces = await query
                .OrderByDescending(t => t.CreatedAt)
                .Take(200)
                .ToListAsync();

            var anomalies = new List<AnomalousTrace>();
            var jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            if (!traces.Any())
            {
                return new List<AnomalousTrace>();
            }

            var avgTime = traces.Average(t => t.TotalElapsedMs);
            var timeThreshold = avgTime * 3;

            foreach (var trace in traces)
            {
                var reasons = new List<string>();

                if (trace.TotalElapsedMs > timeThreshold)
                {
                    reasons.Add($"Very slow execution ({trace.TotalElapsedMs}ms vs avg {avgTime:F0}ms)");
                }

                if (string.IsNullOrWhiteSpace(trace.Answer) || trace.Answer?.Contains("I don't understand") == true)
                {
                    reasons.Add("Empty or unclear answer");
                }

                AdminCopilotExecutionDetails? details = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(trace.ExecutionPlan))
                    {
                        details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(
                            trace.ExecutionPlan, jsonOptions);

                        if (details?.PlannerConfidence == RoutingConfidence.Low)
                        {
                            reasons.Add("Low confidence routing");
                        }

                        if (details?.QueryPlan?.RequiresClarification == true)
                        {
                            reasons.Add("Required user clarification");
                        }

                        if (details?.Steps?.Any(s => s.Status == CopilotStepStatus.Error) == true)
                        {
                            reasons.Add("Contains error steps");
                        }
                    }
                }
                catch { }

                if (reasons.Any())
                {
                    anomalies.Add(new AnomalousTrace
                    {
                        TraceId = trace.Id,
                        Question = trace.Question,
                        Answer = trace.Answer,
                        ExecutionTimeMs = trace.TotalElapsedMs,
                        Intent = details?.DetectedIntent ?? "Unknown",
                        AnomalyReasons = reasons
                    });
                }
            }

            return anomalies.OrderByDescending(a => a.AnomalyReasons.Count).ToList();
        }

        public async Task<IntentDistributionReport> GetIntentDistributionAsync(int? sessionId = null)
        {
            var query = _context.CopilotTraceHistories.AsQueryable();
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var tracesData = await query
                .Select(t => new { t.ExecutionPlan, t.TotalElapsedMs })
                .ToListAsync();

            var distribution = tracesData
                .Select(t => {
                    try {
                        var details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(t.ExecutionPlan, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return new { Intent = details?.DetectedIntent ?? "Unknown", t.TotalElapsedMs };
                    } catch {
                        return new { Intent = "Unknown", t.TotalElapsedMs };
                    }
                })
                .GroupBy(x => x.Intent)
                .Select(g => new IntentStats
                {
                    Intent = g.Key,
                    Count = g.Count(),
                    AverageExecutionTimeMs = (long)g.Average(x => x.TotalElapsedMs),
                    Percentage = tracesData.Count == 0 ? 0 : (double)g.Count() / tracesData.Count * 100
                })
                .OrderByDescending(x => x.Count)
                .ToList();

            return new IntentDistributionReport
            {
                Distribution = distribution,
                TotalTraces = tracesData.Count
            };
        }

        private void AnalyzeForIssues(CopilotTraceHistory trace, AdminCopilotExecutionDetails details, List<TraceIssue> issues)
        {
            if (details.PlannerConfidence == RoutingConfidence.High && 
                details.Steps?.Any(s => s.Status == CopilotStepStatus.Error) == true)
            {
                issues.Add(new TraceIssue
                {
                    TraceId = trace.Id,
                    IssueType = "HighConfidenceFailure",
                    Severity = "High",
                    Description = "High confidence routing but execution failed",
                    Question = trace.Question,
                    DetectedIntent = details.DetectedIntent,
                    RouteReason = details.RouteReason
                });
            }

            if (details.QueryPlan == null && details.DetectedIntent == "DataQuery")
            {
                issues.Add(new TraceIssue
                {
                    TraceId = trace.Id,
                    IssueType = "MissingQueryPlan",
                    Severity = "Medium",
                    Description = "Data query intent detected but no query plan was generated",
                    Question = trace.Question
                });
            }

            if (details.QueryPlan != null && details.ResultCount == 0 && 
                !details.QueryPlan.RequiresClarification)
            {
                issues.Add(new TraceIssue
                {
                    TraceId = trace.Id,
                    IssueType = "ZeroResults",
                    Severity = "Low",
                    Description = "Valid query plan but returned zero results",
                    Question = trace.Question,
                    GeneratedSql = details.SubExecutions?.FirstOrDefault()?.GeneratedSql
                });
            }

            if (trace.TotalElapsedMs > 30000)
            {
                issues.Add(new TraceIssue
                {
                    TraceId = trace.Id,
                    IssueType = "SlowExecution",
                    Severity = "Medium",
                    Description = $"Execution took {trace.TotalElapsedMs}ms (>30s threshold)",
                    Question = trace.Question,
                    ExecutionTimeMs = trace.TotalElapsedMs
                });
            }
        }

        private void GenerateRecommendations(TraceAnalysisReport report)
        {
            var lowConfidenceCount = report.ConfidenceDistribution.GetValueOrDefault("Low", 0);
            if (report.TotalTracesAnalyzed > 0 && lowConfidenceCount > report.TotalTracesAnalyzed * 0.1)
            {
                report.Recommendations.Add(
                    $"{lowConfidenceCount} queries ({lowConfidenceCount * 100 / report.TotalTracesAnalyzed}%) had low confidence routing. " +
                    "Consider adding more training examples for common question patterns.");
            }

            var legacyCount = report.IssuesFound.Count(i => i.IssueType == "LegacyCompatOnly");
            if (legacyCount > 0)
            {
                report.Recommendations.Add(
                    $"{legacyCount} queries fell back to legacy compatibility mode. " +
                    "These queries may not benefit from semantic intent improvements.");
            }

            var slowCount = report.IssuesFound.Count(i => i.IssueType == "SlowExecution");
            if (slowCount > 0)
            {
                report.Recommendations.Add(
                    $"{slowCount} queries exceeded 30s execution time. " +
                    "Consider query optimization or adding caching for common patterns.");
            }

            var mostCommonIntent = report.IntentBreakdown.OrderByDescending(kvp => kvp.Value).FirstOrDefault();
            if (report.TotalTracesAnalyzed > 0 && mostCommonIntent.Value > report.TotalTracesAnalyzed * 0.3)
            {
                report.Recommendations.Add(
                    $"'{mostCommonIntent.Key}' represents {mostCommonIntent.Value * 100 / report.TotalTracesAnalyzed}% of queries. " +
                    "Optimize this intent path for better performance.");
            }
        }

        private string NormalizeQuestion(string? question)
        {
            if (string.IsNullOrWhiteSpace(question)) return "empty";
            return question.ToLowerInvariant().Trim();
        }
    }

    // ─── DTO models extracted to Models/Trace/CopilotTraceAnalyzerModels.cs (2026-05-08) ───
    // The 11 POCO classes that used to live here (TraceAnalysisReport, TraceSummary,
    // ExecutionTimeStatistics, RoutingPattern, QueryPlanInsight, TraceIssue,
    // TracePatternAnalysis, FailedQueryInfo, QuestionPattern, AnomalousTrace,
    // IntentDistributionReport, IntentStats) are now in that sibling file. Same namespace,
    // so callers don't need any using-statement updates. This file dropped from 584 → 470 lines.
}
