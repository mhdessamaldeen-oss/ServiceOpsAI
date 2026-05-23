using ServiceOpsAI.Enums;
using ServiceOpsAI.Constants;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using ServiceOpsAI.Services.AI;
using ServiceOpsAI.Services.AI.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Services.Infrastructure;
using ServiceOpsAI.Services.AI.Investigation;
using ServiceOpsAI.Services.AI.Copilot.Assessment;
using ServiceOpsAI.Services.AI.Copilot.Tools;
using ServiceOpsAI.Services.AI.Copilot.Suggestions;
using SuperAdminCopilot.HostBridge;
using SuperAdminCopilot.Retrieval;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

namespace ServiceOpsAI.Controllers.AI
{
    [Authorize(Roles = RoleNames.Admin)]
    [Route("AiAnalysis/[action]")]
    public class AiAnalysisController : Controller
    {
        private const string MissingAssessmentCacheDetail = "Assessment cache is missing for this trace. Rerun the case to populate cached assessment status.";

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly AiAnalysisQueueService _queueService;
        private readonly EmbeddingQueueService _embeddingService;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly BilingualRetrievalBenchmarkService _benchmarkService;
        private readonly CopilotRecommendationAnalyzer _recommendationAnalyzer;
        private readonly KnowledgeBaseRagService _knowledgeBaseRagService;
        private readonly CopilotToolRegistry _toolRegistry;
        private readonly CopilotAssessmentHandler _assessmentHandler;
        private readonly ILocalizationService _localizer;
        private readonly ISemanticLayer _semanticLayer;
        private readonly IEntityCatalog _entityCatalog;
        private readonly ICopilotSuggestionService _suggestionService;
        private readonly IMapper _mapper;
        private readonly ILogger<AiAnalysisController> _logger;
        // SuperAdminCopilot v2 bridge — used by AskCopilot to route through the new in-host pipeline.
        private readonly ISuperAdminCopilotChatBridge _superAdminCopilotChatBridge;
        // Writer for the "Promote to Trusted" flow — appends a new entry to
        // verified-queries.json and triggers the matcher to reload it.
        private readonly IVerifiedQueryWriter _verifiedQueryWriter;
        private readonly IVerifiedQueryStore _verifiedQueryStore;

        public AiAnalysisController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IServiceScopeFactory scopeFactory,
            AiAnalysisQueueService queueService,
            EmbeddingQueueService embeddingService,
            ISemanticSearchService semanticSearchService,
            BilingualRetrievalBenchmarkService benchmarkService,
            CopilotRecommendationAnalyzer recommendationAnalyzer,
            KnowledgeBaseRagService knowledgeBaseRagService,
            CopilotToolRegistry toolRegistry,
            CopilotAssessmentHandler assessmentHandler,
            ILocalizationService localizer,
            ISemanticLayer semanticLayer,
            IEntityCatalog entityCatalog,
            ICopilotSuggestionService suggestionService,
            IMapper mapper,
            ILogger<AiAnalysisController> logger,
            ISuperAdminCopilotChatBridge superAdminCopilotChatBridge,
            IVerifiedQueryWriter verifiedQueryWriter,
            IVerifiedQueryStore verifiedQueryStore)
        {
            _context = context;
            _userManager = userManager;
            _scopeFactory = scopeFactory;
            _queueService = queueService;
            _embeddingService = embeddingService;
            _semanticSearchService = semanticSearchService;
            _benchmarkService = benchmarkService;
            _recommendationAnalyzer = recommendationAnalyzer;
            _knowledgeBaseRagService = knowledgeBaseRagService;
            _toolRegistry = toolRegistry;
            _assessmentHandler = assessmentHandler;
            _localizer = localizer;
            _semanticLayer = semanticLayer;
            _entityCatalog = entityCatalog;
            _suggestionService = suggestionService;
            _mapper = mapper;
            _logger = logger;
            _superAdminCopilotChatBridge = superAdminCopilotChatBridge;
            _verifiedQueryWriter = verifiedQueryWriter;
            _verifiedQueryStore = verifiedQueryStore;
        }

        /// <summary>
        /// Returns the latest (highest RunNumber) analysis for a ticket.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetStatus(int id)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == id)
                .OrderByDescending(a => a.RunNumber)
                .FirstOrDefaultAsync();

            if (analysis == null)
            {
                // Check if it's queued but not yet started
                var qStatus = _queueService.GetTicketStatus(id);
                if (qStatus != null && (qStatus.Status == AiAnalysisStatus.Queued || qStatus.Status == AiAnalysisStatus.InProgress))
                {
                    return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto 
                    { 
                        Status = qStatus.Status.ToString(), 
                        QueueStatus = qStatus.Status.ToString() 
                    }));
                }
                return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto 
                { 
                    Status = AiAnalysisStatus.NotStarted.ToString() 
                }));
            }

            var attachments = new List<object>();

            string latestLog = "";
            if (analysis.AnalysisStatus == AiAnalysisStatus.InProgress)
            {
                var log = await _context.TicketAiAnalysisLogs
                    .Where(l => l.TicketAiAnalysisId == analysis.Id)
                    .OrderByDescending(l => l.CreatedOn)
                    .FirstOrDefaultAsync();
                if (log != null) latestLog = log.Message;
            }

            var dto = _mapper.Map<TicketAiAnalysisDto>(analysis);
            dto.LatestLog = latestLog;
            return Json(ApiResponse<TicketAiAnalysisDto>.Ok(dto));
        }

        /// <summary>
        /// Returns a specific run's analysis for a ticket.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRun(int ticketId, int runNumber)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId && a.RunNumber == runNumber)
                .FirstOrDefaultAsync();

            if (analysis == null)
                return Json(ApiResponse<AiAnalysisStatusDto>.Ok(new AiAnalysisStatusDto { Status = "NotStarted" }));

            var attachments = new List<object>();

            var dto = _mapper.Map<TicketAiAnalysisDto>(analysis);
            return Json(ApiResponse<TicketAiAnalysisDto>.Ok(dto));
        }

        /// <summary>
        /// Returns run history (lightweight) for a ticket.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRunHistory(int ticketId)
        {
            var runs = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.RunNumber)
                .ProjectTo<TicketRunHistoryDto>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return Json(ApiResponse<List<TicketRunHistoryDto>>.Ok(runs));
        }

        [HttpGet]
        public async Task<IActionResult> GetLogs(int ticketId)
        {
            var analysis = await _context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.RunNumber)
                .Select(a => new { a.Id })
                .FirstOrDefaultAsync();

            if (analysis == null) return Json(new List<object>());

            var logs = await _context.TicketAiAnalysisLogs
                .Where(l => l.TicketAiAnalysisId == analysis.Id)
                .OrderByDescending(l => l.CreatedOn)
                .Take(200)
                .OrderBy(l => l.CreatedOn)
                .ProjectTo<AiLogDto>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return Json(ApiResponse<List<AiLogDto>>.Ok(logs));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetSimilarSolutions(int id)
        {
            try
            {
                var matches = await _semanticSearchService.GetRelatedTicketsAsync(id, count: 3);
                
                var results = _mapper.Map<List<AiSearchMatchDto>>(matches);

                return Json(ApiResponse<List<AiSearchMatchDto>>.Ok(results));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail("Error retrieving similar solutions: " + ex.Message));
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetCopilotRecommendation(int id)
        {
            try
            {
                var recommendation = await _recommendationAnalyzer.GenerateAsync(id);
                var dto = _mapper.Map<AiRecommendationDto>(recommendation);
                // SimilarTickets and KnowledgeMatches still need manual mapping or nested mapping profile
                dto.SimilarTickets = _mapper.Map<List<AiSearchMatchDto>>(recommendation.SimilarTickets);
                dto.KnowledgeMatches = _mapper.Map<List<KnowledgeMatchDto>>(recommendation.KnowledgeMatches);

                return Json(ApiResponse<AiRecommendationDto>.Ok(dto));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail("Error generating recommendation: " + ex.Message));
            }
        }

        [HttpGet]
        public IActionResult Benchmark()
        {
            return View();
        }

        public async Task<IActionResult> GetAssessmentProgress(int? sessionId, string? caseIds = null, Guid? runId = null)
        {
            if (!sessionId.HasValue) return Json(new { completed = 0 });

            // "Completed" = the case was attempted. A trace row exists ⇒ the orchestrator ran
            // the case to its terminal step (whether it returned an answer, an empty answer, or
            // a refusal). Filtering on Answer-non-empty was wrong: cases that legitimately have
            // no answer (LLM timeout, validator refusal, exception) never counted, so the
            // progress bar stuck below 100% and the assessment session never showed as
            // "finished" even after every case was processed.
            var query = _context.CopilotTraceHistories
                .Where(t => t.SessionId == sessionId.Value && t.CaseCode != null);

            var selectedCodes = await ResolveAssessmentCaseCodesAsync(caseIds);
            if (selectedCodes.Count > 0)
            {
                query = query.Where(t => selectedCodes.Contains(t.CaseCode!));
            }

            var completedCount = runId.HasValue
                ? await query.CountAsync()
                : await query.Select(t => t.CaseCode).Distinct().CountAsync();

            return Json(ApiResponse<object>.Ok(new { completed = completedCount }));
        }

        /// <summary>List every available assessment suite (file in Suites/) so the dropdown on
        /// the assessment page can render them. Returns most-recently-modified first; the entry
        /// flagged <c>isDefault: true</c> is what loads when no explicit selection is made.</summary>
        [HttpGet]
        public IActionResult ListAssessmentSuites()
        {
            var suites = _assessmentHandler.ListSuites();
            return Json(new { success = true, suites });
        }

        /// <summary>Persist the user's suite selection on the handler (in-memory). Subsequent
        /// runs will use that file. Pass an empty/null filename to revert to "newest in folder".</summary>
        [HttpPost]
        public IActionResult SelectAssessmentSuite([FromBody] SelectSuiteRequest request)
        {
            var ok = _assessmentHandler.SetActiveSuite(request?.FileName);
            return Json(new { success = ok, fileName = request?.FileName });
        }

        public class SelectSuiteRequest
        {
            public string? FileName { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> CopilotAssessment([FromQuery] GridRequestModel request, [FromQuery] int? sessionId = null, [FromQuery] string? suite = null)
        {
            request.Normalize();

            // Resolve the suite filename to activate BEFORE the view model loads. The handler
            // is Scoped, so its _activeSuiteFiles is empty at the start of every request — if
            // we let GetAssessmentLabViewModelAsync run without selecting a suite first, it
            // falls back to the legacy master copilot-assessment.json and the grid is populated
            // from that, NOT from the suite the dropdown is about to highlight as default.
            //
            // Precedence: explicit ?suite= query param (URL-shareable) → most-recently-modified
            // suite file (matches the dropdown's IsDefault). If neither resolves the legacy
            // master remains the fallback.
            var availableSuites = _assessmentHandler.ListSuites();
            var defaultSuiteFileName = availableSuites.FirstOrDefault(s => s.IsDefault)?.FileName;
            var activeSuiteFile = !string.IsNullOrWhiteSpace(suite) ? suite : defaultSuiteFileName;
            if (!string.IsNullOrWhiteSpace(activeSuiteFile))
            {
                _assessmentHandler.SetActiveSuite(activeSuiteFile);
            }

            var model = await _assessmentHandler.GetAssessmentLabViewModelAsync();
            model.AvailableSuites = availableSuites.Select(s => new AssessmentSuiteOption
            {
                FileName = s.FileName,
                DisplayName = s.DisplayName,
                ScenarioCount = s.ScenarioCount,
                IsDefault = s.IsDefault
            }).ToList();
            model.ActiveSuiteFileName = activeSuiteFile;
            model.LatestRun = await GetLatestCopilotAssessmentRunAsync(sessionId);
            model.CatalogPage = await GetCopilotAssessmentCatalogPageAsync(request, sessionId);

            // Load available sessions for the dropdown
            model.AvailableSessions = await _context.CopilotChatSessions
                .Where(s => !s.IsDeleted && s.IsAssessment)
                .OrderByDescending(s => s.CreatedAt)
                .Take(50)
                .ToListAsync();
            model.SelectedSessionId = sessionId;

            // Load distinct assessment case codes from CopilotTraceHistories that have an answer.
            var query = _context.CopilotTraceHistories
                .Where(t => t.CaseCode != null && t.Answer != null && t.Answer != "");

            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var traceCaseCodes = await query
                .Select(t => t.CaseCode!)
                .Distinct()
                .ToListAsync();

            model.TraceCaseCodes = new HashSet<string>(traceCaseCodes, StringComparer.OrdinalIgnoreCase);

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_CopilotAssessmentGrid", model.CatalogPage);
            }

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> GetLatestRun(int sessionId, string? caseIds = null)
        {
            var summary = await GetLatestCopilotAssessmentRunAsync(sessionId, ParseCaseIds(caseIds));
            if (summary == null) return NotFound();
            return Json(ApiResponse<CopilotAssessmentRunSummaryDto>.Ok(summary));
        }

        private async Task<CopilotAssessmentReport> RunAssessmentBackgroundAsync(List<CopilotAssessmentCase> suite, string? userId, int sessionId)
        {
            // We use a detached scope or the factory to ensure the background task has its own context
            return await _assessmentHandler.RunAssessmentAsync(suite, userId, sessionId);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunCopilotAssessment([FromBody] RunCopilotAssessmentRequest request)
        {
            var user = await _userManager.GetUserAsync(User);

            // Multi-suite runs: the assessment handler is Scoped, so its in-memory
            // _activeSuiteFiles doesn't survive across requests. Re-apply the selection
            // from the run request body BEFORE loading scenarios. Otherwise the run falls
            // back to "newest file in folder" and only ~10 questions execute (audit fix).
            if (request.SuiteFiles is { Count: > 0 })
            {
                _assessmentHandler.SetActiveSuites(request.SuiteFiles.ToArray());
            }

            var suite = request.CaseIds?.Any() == true
                ? await _assessmentHandler.GetTestSuiteAsync(request.CaseIds)
                : await _assessmentHandler.GetDefaultTestSuiteAsync();

            if (suite == null || !suite.Any())
            {
                return Json(ApiResponse.Fail("No assessment scenarios selected or found."));
            }

            if (!string.IsNullOrWhiteSpace(request.Category))
            {
                suite = suite.Where(c => string.Equals(c.Category, request.Category, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(request.Difficulty))
            {
                suite = suite.Where(c => string.Equals(c.Difficulty, request.Difficulty, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (suite.Count == 0)
            {
                return Json(ApiResponse.Fail("No assessment scenarios matched the supplied Category/Difficulty filters."));
            }

            // Resolve or create session before background work
            int sessionId;
            if (request.SessionId.HasValue)
            {
                var existing = await _context.CopilotChatSessions
                    .FirstOrDefaultAsync(s => s.Id == request.SessionId.Value && !s.IsDeleted);
                if (existing != null)
                {
                    sessionId = existing.Id;
                }
                else
                {
                    var session = CopilotAssessmentHandler.CreateNewAssessmentSession(user?.Id);
                    _context.CopilotChatSessions.Add(session);
                    await _context.SaveChangesAsync();
                    sessionId = session.Id;
                }
            }
            else
            {
                var session = CopilotAssessmentHandler.CreateNewAssessmentSession(user?.Id);
                _context.CopilotChatSessions.Add(session);
                await _context.SaveChangesAsync();
                sessionId = session.Id;
            }

            var totalCases = suite.Count;
            var userId = user?.Id;
            var runId = Guid.NewGuid();

            // Fire-and-forget with a dedicated scope — controller releases all connections immediately
            _ = Task.Run(async () => {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var scopedService = scope.ServiceProvider.GetRequiredService<CopilotAssessmentHandler>();
                    var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<AiAnalysisController>>();
                    try 
                    {
                        await scopedService.RunAssessmentAsync(suite, userId, sessionId, runId);
                    } 
                    catch (Exception ex) 
                    {
                        scopedLogger.LogError(ex, "Background assessment run failed for session {SessionId}", sessionId);
                    }
                }
            });

            return Json(ApiResponse<object>.Ok(new { sessionId, totalCases, runId }, "Assessment started in background."));
        }

        private CopilotAssessmentResult EvaluateTrace(CopilotAssessmentCase testCase, CopilotTraceHistory trace, JsonSerializerOptions jsonOptions)
        {
            try
            {
                var details = string.IsNullOrWhiteSpace(trace.ExecutionPlan)
                    ? new AdminCopilotExecutionDetails()
                    : JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(trace.ExecutionPlan, jsonOptions) ?? new AdminCopilotExecutionDetails();

                var actualResponse = new CopilotChatResponse
                {
                    TraceId = trace.Id,
                    Question = trace.Question,
                    Answer = trace.Answer ?? string.Empty,
                    ExecutionDetails = details,
                    ResponseMode = Enum.TryParse<ResponseMode>(details.RouteReason, out var mode) ? mode : ResponseMode.KnowledgeMatch,
                    UsedTool = details.Steps.FirstOrDefault(s => s.Action.StartsWith("Tool Execution:", StringComparison.OrdinalIgnoreCase))?.Action.Replace("Tool Execution:", "").Trim() ?? "none"
                };

                return new CopilotAssessmentResult
                {
                    Case = testCase,
                    ActualResponse = actualResponse,
                    LatencyMs = trace.TotalElapsedMs,
                    TraceId = trace.Id
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate trace {TraceId} for case {CaseCode}", trace.Id, testCase.Code);
                return new CopilotAssessmentResult
                {
                    Case = testCase,
                    FailureReason = "Trace evaluation failed: " + ex.Message,
                    LatencyMs = trace.TotalElapsedMs,
                    TraceId = trace.Id
                };
            }
        }

        private async Task<CopilotAssessmentRunSummaryDto?> GetLatestCopilotAssessmentRunAsync(int? sessionId, IReadOnlyCollection<string>? caseIds = null)
        {
            var suite = await _assessmentHandler.GetDefaultTestSuiteAsync();
            if (caseIds?.Any() == true)
            {
                var selectedIds = caseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                suite = suite.Where(testCase => selectedIds.Contains(testCase.Id)).ToList();
            }

            var caseCodes = suite
                .Where(testCase => !string.IsNullOrWhiteSpace(testCase.Code))
                .Select(testCase => testCase.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var query = _context.CopilotTraceHistories
                .AsNoTracking()
                .Where(t => t.CaseCode != null && caseCodes.Contains(t.CaseCode!) && !string.IsNullOrWhiteSpace(t.Answer));

            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var latestTraces = await query
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new CopilotAssessmentTraceGridRow
                {
                    Id = t.Id,
                    CaseCode = t.CaseCode!,
                    Question = t.Question,
                    Answer = t.Answer,
                    CreatedAt = t.CreatedAt,
                    TotalElapsedMs = t.TotalElapsedMs
                })
                .ToListAsync();

            if (!latestTraces.Any())
            {
                return null;
            }

            var results = new List<CopilotAssessmentCaseResultDto>();
            long totalLatency = 0;
            int successCount = 0;

            var groupedTraces = latestTraces.GroupBy(t => t.CaseCode).ToList();
            
            foreach (var group in groupedTraces)
            {
                var latestTrace = group.First();
                var testCase = suite.FirstOrDefault(c => string.Equals(c.Code, latestTrace.CaseCode, StringComparison.OrdinalIgnoreCase));
                
                if (testCase == null) continue;

                results.Add(new CopilotAssessmentCaseResultDto
                {
                    Id = testCase.Id,
                    Code = testCase.Code,
                    Category = testCase.Category,
                    Difficulty = testCase.Difficulty,
                    Question = testCase.Question,
                    ExpectedBehavior = testCase.ExpectedBehaviorSummary,
                    ActualMode = string.Empty,
                    ActualIntent = string.Empty,
                    ActualTool = string.Empty,
                    Detail = MissingAssessmentCacheDetail,
                    Answer = latestTrace.Answer ?? string.Empty,
                    AnswerPreview = TrimAnswerPreview(latestTrace.Answer),
                    LatencyMs = latestTrace.TotalElapsedMs,
                    IsSuccess = false, // Forensic assessment status moved to ExecutionPlan
                    TraceId = latestTrace.Id
                });

                totalLatency += latestTrace.TotalElapsedMs;
            }

            return new CopilotAssessmentRunSummaryDto
            {
                SummaryId = 0,
                RunAt = latestTraces.Max(t => t.CreatedAt),
                TotalCases = groupedTraces.Count,
                SuccessCount = successCount,
                SuccessRate = groupedTraces.Count > 0 ? (double)successCount / groupedTraces.Count : 0,
                AverageLatencyMs = groupedTraces.Count > 0 ? totalLatency / groupedTraces.Count : 0,
                Results = results
            };
        }

        private async Task<HashSet<string>> ResolveAssessmentCaseCodesAsync(string? caseIds)
        {
            var selectedIds = ParseCaseIds(caseIds);
            if (!selectedIds.Any())
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var suite = await _assessmentHandler.GetDefaultTestSuiteAsync();
            return suite
                .Where(testCase => selectedIds.Contains(testCase.Id) && !string.IsNullOrWhiteSpace(testCase.Code))
                .Select(testCase => testCase.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ParseCaseIds(string? caseIds)
            => string.IsNullOrWhiteSpace(caseIds)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : caseIds
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string TrimAnswerPreview(string? answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                return string.Empty;
            }

            return answer.Length > 200 ? answer[..200] + "..." : answer;
        }

        private sealed class CopilotAssessmentTraceGridRow
        {
            public int Id { get; set; }
            public int? SessionId { get; set; }
            public string CaseCode { get; set; } = string.Empty;
            public string Question { get; set; } = string.Empty;
            public string? Answer { get; set; }
            public DateTime CreatedAt { get; set; }
            public long TotalElapsedMs { get; set; }
            public string? ModelName { get; set; }
            public string? GeneratedScript { get; set; }
            public int? LlmCallCount { get; set; }
            public int? TotalPromptTokens { get; set; }
            public int? TotalCompletionTokens { get; set; }
            public decimal? EstimatedCostUsd { get; set; }
        }

        private async Task<PagedResult<CopilotAssessmentCaseGridItem>> GetCopilotAssessmentCatalogPageAsync(GridRequestModel request, int? sessionId)
        {
            var suite = await _assessmentHandler.GetDefaultTestSuiteAsync();
            var query = _context.CopilotTraceHistories
                .AsNoTracking()
                .Where(t => !string.IsNullOrWhiteSpace(t.Answer));
                
            if (sessionId.HasValue)
            {
                query = query.Where(t => t.SessionId == sessionId.Value);
            }

            var tracesWithCode = await query
                .Where(t => t.CaseCode != null)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new CopilotAssessmentTraceGridRow
                {
                    Id = t.Id,
                    SessionId = t.SessionId,
                    CaseCode = t.CaseCode!,
                    Question = t.Question,
                    Answer = t.Answer,
                    CreatedAt = t.CreatedAt,
                    TotalElapsedMs = t.TotalElapsedMs,
                    ModelName = t.ModelName,
                    // ExecutionPlan removed from the grid projection — the JSON blob averages
                    // hundreds of KB and the grid only read three trivial fields from it
                    // (ModelCapacity / PromptLength / IsTruncated). Those now read as null on
                    // the grid; the Investigation detail page still deserialises the full plan
                    // when the user clicks through.
                    GeneratedScript = t.GeneratedScript,
                    LlmCallCount = t.LlmCallCount,
                    TotalPromptTokens = t.TotalPromptTokens,
                    TotalCompletionTokens = t.TotalCompletionTokens,
                    EstimatedCostUsd = t.EstimatedCostUsd
                })
                .ToListAsync();

            var tracesByCaseCode = tracesWithCode
                .GroupBy(t => t.CaseCode)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var rows = suite.Select(testCase =>
            {
                tracesByCaseCode.TryGetValue(testCase.Code, out var history);
                history ??= new List<CopilotAssessmentTraceGridRow>();
                
                var latestTrace = history.FirstOrDefault();
                var previousTrace = history.Skip(1).FirstOrDefault();

                // The grid no longer deserialises the full ExecutionPlan (per-row JSON parse
                // dominated this loop on large suites). Details-only fields (ModelCapacity /
                // PromptLength / IsTruncated) are not surfaced in the grid; they remain on the
                // Investigation detail page, which still loads the full plan on demand.
                var totalRuns = history.Count;
                var statusChange = "Archived";

                return new CopilotAssessmentCaseGridItem
                {
                    Id = testCase.Id,
                    Code = testCase.Code,
                    Question = testCase.Question,
                    Category = testCase.Category,
                    CategoryDescription = testCase.CategoryDescription,
                    Difficulty = testCase.Difficulty,
                    SourceSuite = testCase.SourceSuite ?? string.Empty,
                    ExpectedBehavior = testCase.ExpectedBehaviorSummary,
                    SortOrder = testCase.SortOrder,
                    HasAnswer = !string.IsNullOrWhiteSpace(latestTrace?.Answer),
                    HasLatestResult = false,
                    IsSuccess = null,
                    ActualMode = string.Empty,
                    ActualIntent = string.Empty,
                    ActualTool = string.Empty,
                    Detail = latestTrace == null ? string.Empty : MissingAssessmentCacheDetail,
                    AnswerPreview = TrimAnswerPreview(latestTrace?.Answer),
                    LatencyMs = latestTrace?.TotalElapsedMs,
                    TraceId = latestTrace?.Id,
                    TraceSessionId = latestTrace?.SessionId,
                    ModelName = latestTrace?.ModelName,
                    ContextCapacity = null,
                    PromptChars = null,
                    IsTruncated = false,
                    PreviousResult = null,
                    PreviousRunAt = previousTrace?.CreatedAt,
                    StatusChange = statusChange,
                    TotalRuns = totalRuns,
                    HistoricalSuccessRate = 0,
                    LatestRunAt = latestTrace?.CreatedAt,
                    ExpectedSql = testCase.CorrectAnswer?.SQL,
                    GeneratedSql = latestTrace?.GeneratedScript,
                    // Cost / usage roll-up from the latest trace — shown as compact pills in
                    // the grid so a sweep across the suite reveals expensive questions.
                    LlmCallCount = latestTrace?.LlmCallCount,
                    TotalPromptTokens = latestTrace?.TotalPromptTokens,
                    TotalCompletionTokens = latestTrace?.TotalCompletionTokens,
                    EstimatedCostUsd = latestTrace?.EstimatedCostUsd
                };
            }).ToList();

            rows = ApplyCopilotAssessmentFilters(rows, request);
            rows = ApplyCopilotAssessmentSort(rows, request.SortOrder).ToList();

            var totalItems = rows.Count;
            var effectivePageSize = request.GetEffectivePageSize(totalItems);
            var items = rows
                .Skip((request.PageNumber - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToList();

            return new PagedResult<CopilotAssessmentCaseGridItem>
            {
                Items = items,
                TotalCount = totalItems,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };
        }

        private static List<CopilotAssessmentCaseGridItem> ApplyCopilotAssessmentFilters(
            List<CopilotAssessmentCaseGridItem> rows,
            GridRequestModel request)
        {
            if (!string.IsNullOrWhiteSpace(request.SearchString))
            {
                var search = request.SearchString.Trim();
                rows = rows
                    .Where(row =>
                        row.Question.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.Difficulty.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.ExpectedBehavior.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.ActualIntent.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.ActualMode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.ActualTool.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        row.SourceSuite.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            rows = request.Filter switch
            {
                "has-answer" => rows.Where(row => row.HasAnswer).ToList(),
                "needs-answer" => rows.Where(row => !row.HasAnswer).ToList(),
                "pass" => rows.Where(row => row.IsSuccess == true).ToList(),
                "fail" => rows.Where(row => row.IsSuccess == false).ToList(),
                "pending" => rows.Where(row => !row.HasLatestResult).ToList(),
                _ => rows
            };

            return rows;
        }

        private static IOrderedEnumerable<CopilotAssessmentCaseGridItem> ApplyCopilotAssessmentSort(
            List<CopilotAssessmentCaseGridItem> rows,
            string? sortOrder)
        {
            return sortOrder switch
            {
                "scenario" => rows.OrderBy(row => row.Question),
                "scenario_desc" => rows.OrderByDescending(row => row.Question),
                "category" => rows.OrderBy(row => row.Category).ThenBy(row => row.SortOrder),
                "category_desc" => rows.OrderByDescending(row => row.Category).ThenBy(row => row.SortOrder),
                "suite" => rows.OrderBy(row => row.SourceSuite).ThenBy(row => row.SortOrder),
                "suite_desc" => rows.OrderByDescending(row => row.SourceSuite).ThenBy(row => row.SortOrder),
                "code" => rows.OrderBy(row => row.Code).ThenBy(row => row.SortOrder),
                "code_desc" => rows.OrderByDescending(row => row.Code).ThenBy(row => row.SortOrder),
                "difficulty" => rows.OrderBy(row => GetDifficultyRank(row.Difficulty)).ThenBy(row => row.SortOrder),
                "difficulty_desc" => rows.OrderByDescending(row => GetDifficultyRank(row.Difficulty)).ThenBy(row => row.SortOrder),
                "status" => rows.OrderBy(row => GetStatusRank(row.IsSuccess, row.HasLatestResult)).ThenBy(row => row.SortOrder),
                "status_desc" => rows.OrderByDescending(row => GetStatusRank(row.IsSuccess, row.HasLatestResult)).ThenBy(row => row.SortOrder),
                "latency" => rows.OrderBy(row => row.LatencyMs ?? long.MaxValue).ThenBy(row => row.SortOrder),
                "latency_desc" => rows.OrderByDescending(row => row.LatencyMs ?? 0).ThenBy(row => row.SortOrder),
                "runs" => rows.OrderBy(row => row.TotalRuns).ThenBy(row => row.SortOrder),
                "runs_desc" => rows.OrderByDescending(row => row.TotalRuns).ThenBy(row => row.SortOrder),
                "hasAnswer" => rows.OrderBy(row => row.HasAnswer).ThenBy(row => row.SortOrder),
                "hasAnswer_desc" => rows.OrderByDescending(row => row.HasAnswer).ThenBy(row => row.SortOrder),
                "trend" => rows.OrderBy(row => GetTrendRank(row.StatusChange)).ThenBy(row => row.SortOrder),
                "trend_desc" => rows.OrderByDescending(row => GetTrendRank(row.StatusChange)).ThenBy(row => row.SortOrder),
                _ => rows.OrderBy(row => row.SortOrder).ThenBy(row => row.Question)
            };
        }

        private static int GetDifficultyRank(string difficulty)
        {
            return difficulty switch
            {
                "Easy" => 1,
                "Medium" => 2,
                "Hard" => 3,
                "Complicated" => 4,
                _ => 99
            };
        }

        private static int GetStatusRank(bool? isSuccess, bool hasLatestResult)
        {
            if (!hasLatestResult)
            {
                return 3;
            }

            return isSuccess == true ? 1 : 2;
        }

        private static int GetTrendRank(string trend)
        {
            return trend switch
            {
                "Improved" => 1,
                "Same" => 2,
                "Regressed" => 3,
                "New" => 4,
                _ => 5
            };
        }

        [HttpGet]
        public async Task<IActionResult> Copilot(string? prompt = null, string? caseCode = null, string? sourceSuite = null)
        {
            var enabledTools = (await _toolRegistry.GetAllToolsAsync())
                .Where(t => t.IsEnabled)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Title)
                .ToList();

            var recentTickets = await _context.Tickets
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new CopilotEvaluationTicketItem
                {
                    TicketId = t.Id,
                    TicketNumber = t.TicketNumber,
                    Title = t.Title,
                    Status = t.Status != null ? t.Status.Name : "",
                    ProductArea = t.ProductArea ?? "",
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            var recentTraces = await _context.CopilotTraceHistories
                .OrderByDescending(t => t.CreatedAt)
                .Take(10)
                .ToListAsync();

            var recentSessions = await _context.CopilotChatSessions
                .Where(s => !s.IsDeleted)
                .OrderByDescending(s => s.LastInteractionAt)
                .Take(20)
                .ToListAsync();

            var model = new CopilotChatViewModel
            {
                RecentTickets = recentTickets,
                AvailableTools = enabledTools,
                ExternalCapabilities = BuildExternalCapabilities(enabledTools),
                StandardPromptGroups = await _assessmentHandler.GetCopilotPromptGroupsAsync(),
                RecentTraces = recentTraces,
                RecentSessions = recentSessions,
                KnowledgeDocumentCount = _knowledgeBaseRagService.GetDocumentCount(),
                InitialPrompt = prompt ?? string.Empty,
                InitialCaseCode = caseCode,
                InitialSourceSuite = sourceSuite
            };

            return View(model);
        }

        private static List<CopilotCapabilityItem> BuildExternalCapabilities(IEnumerable<CopilotToolDefinition> tools)
        {
            return tools
                .Where(tool => string.Equals(tool.ToolType, "External", StringComparison.OrdinalIgnoreCase))
                .Select(tool => new CopilotCapabilityItem
                {
                    ToolKey = tool.ToolKey,
                    ToolTitle = tool.Title,
                    ToolDescription = tool.Description,
                    Prompts = SplitCapabilityPrompts(tool.TestPrompt, tool.Description, tool.ToolKey)
                })
                .Where(item => item.Prompts.Any())
                .ToList();
        }

        private static List<CopilotSamplePrompt> SplitCapabilityPrompts(string? testPrompt, string fallbackDescription, string? toolKey = null)
        {
            var promptText = string.IsNullOrWhiteSpace(testPrompt) ? fallbackDescription : testPrompt;
            return promptText
                .Split(new[] { "\r\n", "\n", "||" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((text, index) => new CopilotSamplePrompt
                {
                    Text = text,
                    Code = toolKey != null ? $"TOOL-{toolKey}-{index + 1}" : null
                })
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> InvestigationHistory(int? traceId = null, string? view = null)
        {
            var traces = await _context.CopilotTraceHistories
                .OrderByDescending(t => t.CreatedAt)
                .Take(100)
                .ToListAsync();
            ViewBag.InitialTraceId = traceId;
            ViewBag.InitialTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story";
            return View(traces);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteInvestigationHistory(List<int> selectedTraceIds)
        {
            if (selectedTraceIds == null || selectedTraceIds.Count == 0)
            {
                TempData["Error"] = "Select at least one investigation trace to delete.";
                return RedirectToAction(nameof(InvestigationHistory));
            }

            var ids = selectedTraceIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var traces = await _context.CopilotTraceHistories
                .Where(trace => ids.Contains(trace.Id))
                .ToListAsync();

            if (traces.Count == 0)
            {
                TempData["Error"] = "No matching investigation traces were found.";
                return RedirectToAction(nameof(InvestigationHistory));
            }

            await using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                await UnlinkCopilotChatMessagesFromTracesAsync(ids);
                _context.CopilotTraceHistories.RemoveRange(traces);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            TempData["Success"] = $"{traces.Count} investigation trace{(traces.Count == 1 ? "" : "s")} deleted.";
            return RedirectToAction(nameof(InvestigationHistory));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllInvestigationHistory()
        {
            var deletedCount = await DeleteAllCopilotTracesAsync();
            TempData["Success"] = deletedCount == 0
                ? "No investigation traces were found."
                : $"{deletedCount} investigation trace{(deletedCount == 1 ? "" : "s")} deleted.";

            return RedirectToAction(nameof(InvestigationHistory));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllSessions()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Unauthorized(ApiResponse.Fail("User is required."));
            }

            var sessions = await _context.CopilotChatSessions
                .Where(s => s.UserId == user.Id && !s.IsDeleted)
                .ToListAsync();

            foreach (var session in sessions)
            {
                session.IsDeleted = true;
            }

            await _context.SaveChangesAsync();

            return Json(ApiResponse<object>.Ok(
                new { deletedCount = sessions.Count },
                $"{sessions.Count} sessions deleted."));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTrace(int id)
        {
            if (id <= 0)
            {
                return BadRequest(ApiResponse.Fail("Invalid trace id."));
            }

            int deletedCount;
            await using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                await UnlinkCopilotChatMessagesFromTracesAsync([id]);
                deletedCount = await _context.CopilotTraceHistories
                    .Where(trace => trace.Id == id)
                    .ExecuteDeleteAsync();
                await transaction.CommitAsync();
            }

            if (deletedCount == 0)
            {
                return NotFound(ApiResponse.Fail("Trace not found or already deleted."));
            }

            return Json(ApiResponse<object>.Ok(new { deletedCount, deletedId = id }, "Investigation trace deleted."));
        }

        private async Task<int> DeleteAllCopilotTracesAsync()
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            await UnlinkAllCopilotChatMessagesFromTracesAsync();
            var deletedCount = await _context.CopilotTraceHistories.ExecuteDeleteAsync();
            await transaction.CommitAsync();

            return deletedCount;
        }

        private async Task UnlinkCopilotChatMessagesFromTracesAsync(IReadOnlyCollection<int> traceIds)
        {
            if (traceIds.Count == 0)
            {
                return;
            }

            await _context.CopilotChatMessages
                .Where(message => message.TraceId.HasValue && traceIds.Contains(message.TraceId.Value))
                .ExecuteUpdateAsync(updates => updates.SetProperty(message => message.TraceId, (int?)null));
        }

        private async Task UnlinkAllCopilotChatMessagesFromTracesAsync()
        {
            await _context.CopilotChatMessages
                .Where(message => message.TraceId.HasValue)
                .ExecuteUpdateAsync(updates => updates.SetProperty(message => message.TraceId, (int?)null));
        }

        // Trace rows are immutable once written, so the deserialised ExecutionPlan can be
        // cached for the lifetime of the row. Keyed by trace Id; sliding 10-min expiry caps
        // memory while keeping hot rows hot. Sized in MemoryCache via the default cost = 1
        // per entry; the default GC compaction handles overall pressure.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime ExpiresAt, AdminCopilotExecutionDetails Details)> _executionPlanCache = new();
        private static AdminCopilotExecutionDetails DeserialiseExecutionPlan(int traceId, string? executionPlanJson)
        {
            if (string.IsNullOrWhiteSpace(executionPlanJson)) return new AdminCopilotExecutionDetails();

            // Cheap eviction: when the dict grows past ~256 entries, drop the expired ones.
            if (_executionPlanCache.Count > 256)
            {
                foreach (var kv in _executionPlanCache)
                    if (kv.Value.ExpiresAt < DateTime.UtcNow)
                        _executionPlanCache.TryRemove(kv.Key, out _);
            }

            if (_executionPlanCache.TryGetValue(traceId, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
                return entry.Details;

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(executionPlanJson, opts) ?? new AdminCopilotExecutionDetails();
            _executionPlanCache[traceId] = (DateTime.UtcNow.AddMinutes(10), details);
            return details;
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Investigation(int id, string? view = null)
        {
            var trace = await _context.CopilotTraceHistories.FindAsync(id);
            if (trace == null) return NotFound();

            var executionDetails = DeserialiseExecutionPlan(id, trace.ExecutionPlan);

            var model = new CopilotTraceDetailPageViewModel
            {
                Trace = trace,
                ExecutionDetails = executionDetails,
                ArchiveCount = await _context.CopilotTraceHistories.CountAsync(),
                DefaultTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story"
            };

            return View(model);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> TracingDetails(int id, string? view = null)
        {
            var trace = await _context.CopilotTraceHistories.FindAsync(id);
            if (trace == null) return NotFound();

            // Reuses the same cached deserialisation as Investigation — both views read the
            // same immutable ExecutionPlan, so one parse covers both clicks.
            var executionDetails = DeserialiseExecutionPlan(id, trace.ExecutionPlan);

            ViewBag.Trace = trace;
            ViewBag.DefaultTraceView = string.Equals(view, "tree", StringComparison.OrdinalIgnoreCase) ? "tree" : "story";
            return PartialView("_InvestigationTraceDetail", executionDetails);
        }

        [HttpGet]
        public async Task<IActionResult> LoadSession(int id)
        {
            var session = await _context.CopilotChatSessions
                .Include(s => s.Messages)
                .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted);

            if (session == null)
            {
                return NotFound(ApiResponse.Fail("Session not found."));
            }

            var messages = session.Messages.OrderBy(m => m.CreatedAt).Select(m => new {
                msgId = m.Id,
                id = m.TraceId,
                Role = m.Role.ToString().ToLower(),
                Content = m.Content
            });

            return Json(ApiResponse<object>.Ok(new { session.Id, session.Title, messages }));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSession([FromBody] string? title)
        {
            var user = await _userManager.GetUserAsync(User);
            var session = new CopilotChatSession
            {
                UserId = user?.Id,
                Title = string.IsNullOrWhiteSpace(title) ? "New Chat" : title
            };

            _context.CopilotChatSessions.Add(session);
            await _context.SaveChangesAsync();

            return Json(ApiResponse<object>.Ok(new { session.Id, session.Title }));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSession(int id)
        {
            var session = await _context.CopilotChatSessions.FindAsync(id);
            if (session == null) return NotFound(ApiResponse.Fail("Session not found."));

            session.IsDeleted = true;
            await _context.SaveChangesAsync();

            return Json(ApiResponse<object>.Ok(new { deletedId = id }, "Session deleted."));
        }

        public sealed class ExplainResultRequest
        {
            public string Question { get; set; } = string.Empty;
            public List<string> Columns { get; set; } = new();
            public List<List<string>> Rows { get; set; } = new();
        }

        /// <summary>
        /// LLM-backed "Explain this result" — takes a structured table the user already saw
        /// in chat and asks the model for a 2-3 sentence interpretation. Cheap second-pass:
        /// the question + table compresses well and produces a useful narrative ("Most tickets
        /// are Open (535) and In Progress (768). Resolution rate is 38%. …"). No DB hit; no
        /// re-plan; just one LLM call with the rows already on the user's screen.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ExplainResult([FromBody] ExplainResultRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Question))
                return BadRequest(new { message = "Question and result rows are required." });
            if (request.Columns is null || request.Columns.Count == 0 || request.Rows is null || request.Rows.Count == 0)
                return BadRequest(new { message = "No result data to explain." });

            var llm = HttpContext.RequestServices.GetService(typeof(SuperAdminCopilot.Abstractions.ILlmClient))
                      as SuperAdminCopilot.Abstractions.ILlmClient;
            if (llm is null) return StatusCode(503, new { message = "LLM not available." });

            var sb = new System.Text.StringBuilder();
            sb.Append("Question: \"").Append(request.Question.Trim()).AppendLine("\"");
            sb.AppendLine("Result:");
            sb.Append("| ").Append(string.Join(" | ", request.Columns)).AppendLine(" |");
            sb.Append("| ").Append(string.Join(" | ", request.Columns.Select(_ => "---"))).AppendLine(" |");
            // Cap rows the LLM sees so a 1000-row table doesn't blow the prompt. The first
            // and last few rows preserve the shape; sparkline-style middle elision keeps
            // distribution observable without dumping the whole set.
            var rows = request.Rows;
            const int Head = 25, Tail = 5;
            if (rows.Count <= Head + Tail)
            {
                foreach (var r in rows) sb.Append("| ").Append(string.Join(" | ", r)).AppendLine(" |");
            }
            else
            {
                for (int i = 0; i < Head; i++) sb.Append("| ").Append(string.Join(" | ", rows[i])).AppendLine(" |");
                sb.AppendLine($"_(omitting {rows.Count - Head - Tail} middle rows)_");
                for (int i = rows.Count - Tail; i < rows.Count; i++)
                    sb.Append("| ").Append(string.Join(" | ", rows[i])).AppendLine(" |");
            }

            const string system =
                "You explain query results to a business user. " +
                "Write 2-3 short sentences. Lead with the headline number / pattern. " +
                "Call out any anomaly (a row much larger / smaller than the rest, an unexpected zero). " +
                "Do NOT restate the question, do NOT recite every row. No markdown.";

            try
            {
                var text = await llm.GenerateTextAsync(system, sb.ToString(), HttpContext.RequestAborted);
                return Json(new { explanation = (text ?? string.Empty).Trim() });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExplainResult LLM call failed.");
                return StatusCode(500, new { message = ex.GetBaseException().Message });
            }
        }

        /// <summary>
        /// LLM-backed follow-up question suggestions. Given the original question and the
        /// result shape (columns + a few sample rows), asks the model for 3-5 natural next
        /// questions a user might ask. Rendered as chips beneath the table; clicking a chip
        /// submits it as a new question through the normal chat path.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SuggestFollowUps([FromBody] ExplainResultRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Question))
                return BadRequest(new { message = "Question is required." });

            var llm = HttpContext.RequestServices.GetService(typeof(SuperAdminCopilot.Abstractions.ILlmClient))
                      as SuperAdminCopilot.Abstractions.ILlmClient;
            if (llm is null) return StatusCode(503, new { message = "LLM not available." });

            var sb = new System.Text.StringBuilder();
            sb.Append("Original question: \"").Append(request.Question.Trim()).AppendLine("\"");
            if (request.Columns is { Count: > 0 })
            {
                sb.AppendLine("Result columns: " + string.Join(", ", request.Columns));
            }
            if (request.Rows is { Count: > 0 })
            {
                sb.AppendLine("Sample rows (up to 5):");
                foreach (var r in request.Rows.Take(5))
                    sb.Append("  - ").AppendLine(string.Join(" | ", r));
            }
            sb.AppendLine();
            sb.AppendLine("Return ONLY a JSON array of 3 short follow-up questions. No prose, no markdown, no commentary.");

            const string system =
                "You suggest natural follow-up questions a user would ask after seeing a result. " +
                "Each suggestion: a different angle (drill-down, time comparison, alternative dimension, related entity). " +
                "Phrase as a question the chat can answer. Keep each under 12 words. " +
                "Return ONLY a JSON array of strings.";

            try
            {
                var raw = await llm.GenerateJsonAsync(system, sb.ToString(), HttpContext.RequestAborted);
                // Extract the first JSON array; LLM may wrap in fences or chat preamble.
                var start = raw.IndexOf('[');
                var end = raw.LastIndexOf(']');
                if (start < 0 || end <= start) return Json(new { suggestions = Array.Empty<string>() });
                var slice = raw.Substring(start, end - start + 1);
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(slice)
                           ?? new List<string>();
                return Json(new { suggestions = list.Where(s => !string.IsNullOrWhiteSpace(s)).Take(5).ToList() });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SuggestFollowUps LLM call failed.");
                return StatusCode(500, new { message = ex.GetBaseException().Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AskCopilot([FromBody] CopilotChatRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Question))
            {
                return BadRequest(new { message = "A question is required." });
            }

            NormalizeCopilotRequest(request);

            var session = await ResolveCopilotSessionAsync(request, HttpContext.RequestAborted);
            request.SessionId = session.Id;

            await AddCopilotMessageAsync(
                session,
                ChatMessageRole.User,
                request.Question,
                traceId: null,
                HttpContext.RequestAborted);
            
            var result = await _superAdminCopilotChatBridge.AskAsync(request, HttpContext.RequestAborted);

            await AddCopilotMessageAsync(
                session,
                ChatMessageRole.Assistant,
                result.Answer,
                result.TraceId,
                HttpContext.RequestAborted);

            return Json(BuildCopilotAskResponse(session.Id, result));
        }

        private static void NormalizeCopilotRequest(CopilotChatRequest request)
        {
            request.Question = request.Question.Trim();
            request.Surface = string.IsNullOrWhiteSpace(request.Surface)
                ? CopilotSurfaces.Default
                : request.Surface.Trim();
        }

        private async Task<CopilotChatSession> ResolveCopilotSessionAsync(CopilotChatRequest request, CancellationToken cancellationToken)
        {
            if (request.SessionId is > 0)
            {
                var existingSession = await _context.CopilotChatSessions
                    .FirstOrDefaultAsync(session => session.Id == request.SessionId.Value && !session.IsDeleted, cancellationToken);

                if (existingSession != null)
                {
                    return existingSession;
                }
            }

            var user = await _userManager.GetUserAsync(User);
            var session = new CopilotChatSession
            {
                UserId = user?.Id,
                Title = BuildCopilotSessionTitle(request.Question),
                Surface = request.Surface
            };

            _context.CopilotChatSessions.Add(session);
            await _context.SaveChangesAsync(cancellationToken);

            return session;
        }

        private static string BuildCopilotSessionTitle(string question)
        {
            return question.Length > 30
                ? question[..30] + "..."
                : question;
        }

        private async Task AddCopilotMessageAsync(
            CopilotChatSession session,
            ChatMessageRole role,
            string content,
            int? traceId,
            CancellationToken cancellationToken)
        {
            var createdAt = DateTime.UtcNow;

            _context.CopilotChatMessages.Add(new CopilotChatMessageEntity
            {
                SessionId = session.Id,
                Role = role,
                Content = content,
                TraceId = traceId,
                CreatedAt = createdAt
            });

            session.LastInteractionAt = createdAt;
            await _context.SaveChangesAsync(cancellationToken);
        }

        private static object BuildCopilotAskResponse(int sessionId, CopilotChatResponse result)
        {
            return new
            {
                sessionId,
                traceId = result.TraceId,
                question = result.Question,
                answer = result.Answer,
                evidenceStrength = result.EvidenceStrength.ToString(),
                groundingScore = result.GroundingScore,
                modelName = result.ModelName,
                executionDetails = result.ExecutionDetails,
                dynamicTicketResults = result.DynamicTicketResults,
                structuredColumns = result.StructuredColumns,
                structuredRows = result.StructuredRows,
                structuredQueryResults = result.StructuredQueryResults,
                similarTickets = result.SimilarTickets,
                usedTool = result.UsedTool
            };
        }

        /// <summary>
        /// Promote an answer to the Trusted Query store. The analyst clicks "Trust"
        /// on a Live-query response after verifying the SQL is correct; this endpoint
        /// appends a new entry to <c>verified-queries.json</c> and reloads the matcher.
        /// Next time the same (or paraphrased) question is asked, the system routes
        /// via the Trusted lane — sub-second, zero LLM cost, deterministic.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> PromoteToTrusted([FromBody] PromoteToTrustedRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.Sql))
                return BadRequest(new { ok = false, message = "Both question and sql are required." });
            try
            {
                var user = await _userManager.GetUserAsync(User);
                var verifiedBy = string.IsNullOrWhiteSpace(user?.UserName) ? "(promoted via UI)" : user!.UserName!;
                var draft = new VerifiedQueryDraft(
                    Question: request.Question,
                    Sql: request.Sql,
                    Shape: string.IsNullOrWhiteSpace(request.Shape) ? null : request.Shape,
                    VerifiedBy: verifiedBy,
                    Description: string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
                    QuestionVariants: request.QuestionVariants,
                    Tags: request.Tags);
                var entry = await _verifiedQueryWriter.AppendAsync(draft, cancellationToken);
                return Ok(new
                {
                    ok = true,
                    id = entry.Id,
                    storeCount = _verifiedQueryStore.Count,
                    message = $"Saved to the Trusted Query store as '{entry.Id}'. Next time someone asks this, it'll match the fast lane."
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { ok = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PromoteToTrusted] failed for question '{Q}'", request.Question);
                return StatusCode(500, new { ok = false, message = ex.Message });
            }
        }

        public sealed class PromoteToTrustedRequest
        {
            public string Question { get; set; } = "";
            public string Sql { get; set; } = "";
            public string? Shape { get; set; }
            public string? Description { get; set; }
            public List<string>? QuestionVariants { get; set; }
            public List<string>? Tags { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> Evaluation()
        {
            var evaluations = await _assessmentHandler.GetEvaluationsAsync();
            var ticketIds = evaluations.Select(e => e.TicketId).ToHashSet();

            var recentTickets = await _context.Tickets
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new CopilotEvaluationTicketItem
                {
                    TicketId = t.Id,
                    TicketNumber = t.TicketNumber,
                    Title = t.Title,
                    Status = t.Status != null ? t.Status.Name : "",
                    ProductArea = t.ProductArea ?? "",
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            var model = new CopilotEvaluationViewModel
            {
                Tickets = recentTickets,
                ExistingEvaluations = evaluations.OrderByDescending(e => e.EvaluatedOnUtc).ToList(),
                TotalEvaluations = evaluations.Count,
                PassedEvaluations = evaluations.Count(e => string.Equals(e.OverallOutcome, "Pass", StringComparison.OrdinalIgnoreCase)),
                FailedEvaluations = evaluations.Count(e => string.Equals(e.OverallOutcome, "Fail", StringComparison.OrdinalIgnoreCase))
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEvaluation([FromForm] CopilotEvaluationEntry entry)
        {
            if (entry.TicketId <= 0)
            {
                TempData["Error"] = "Ticket is required for Copilot evaluation.";
                return RedirectToAction(nameof(Evaluation));
            }

            var user = await _userManager.GetUserAsync(User);
            entry.EvaluatedBy = user?.FullName ?? user?.UserName ?? "Admin";
            entry.EvaluatedOnUtc = DateTime.UtcNow;

            await _assessmentHandler.SaveEvaluationAsync(entry);
            TempData["Success"] = $"Evaluation saved for {entry.TicketNumber}.";
            return RedirectToAction(nameof(Evaluation));
        }

        [HttpGet]
        public IActionResult KnowledgeBase()
        {
            var model = new KnowledgeBaseAdminViewModel
            {
                RootPath = _knowledgeBaseRagService.KnowledgeBaseRootPath,
                Categories = _knowledgeBaseRagService.GetManagedFolders().ToList(),
                Documents = _knowledgeBaseRagService.GetManagedDocuments().ToList()
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadKnowledgeDocument(IFormFile? file, string category)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "No file was selected.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            if (!_knowledgeBaseRagService.IsManagedCategory(category))
            {
                TempData["Error"] = "Invalid knowledge-base category.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var extension = Path.GetExtension(file.FileName);
            if (!string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .md and .txt files are supported.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var safeFileName = Path.GetFileName(file.FileName);
            var targetDirectory = _knowledgeBaseRagService.GetCategoryDirectory(category);
            Directory.CreateDirectory(targetDirectory);

            var targetPath = Path.Combine(targetDirectory, safeFileName);
            await using (var stream = System.IO.File.Create(targetPath))
            {
                await file.CopyToAsync(stream);
            }

            _knowledgeBaseRagService.InvalidateDocument(targetPath);
            TempData["Success"] = $"{safeFileName} uploaded to {category}.";
            return RedirectToAction(nameof(KnowledgeBase));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteKnowledgeDocument(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                TempData["Error"] = "Knowledge document path is missing.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            var fullPath = Path.GetFullPath(Path.Combine(_knowledgeBaseRagService.KnowledgeBaseRootPath, relativePath));
            var rootPath = Path.GetFullPath(_knowledgeBaseRagService.KnowledgeBaseRootPath);
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Invalid knowledge document path.";
                return RedirectToAction(nameof(KnowledgeBase));
            }

            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
                _knowledgeBaseRagService.InvalidateDocument(fullPath);
                TempData["Success"] = $"{Path.GetFileName(fullPath)} deleted.";
            }
            else
            {
                TempData["Error"] = "Knowledge document was not found.";
            }

            return RedirectToAction(nameof(KnowledgeBase));
        }

        [HttpGet]
        public async Task<IActionResult> GetTuningSettings()
        {
            var settings = await _semanticSearchService.GetTuningSettingsAsync();
            return Ok(settings);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateTuningSettings([FromBody] ServiceOpsAI.Models.AI.RetrievalTuningSettings settings)
        {
            await _semanticSearchService.UpdateTuningSettingsAsync(settings);
            return Ok(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetRetrievalBenchmark()
        {
            try
            {
                var benchmark = await _benchmarkService.LoadAsync();
                return Json(ApiResponse<BenchmarkDto>.Ok(new BenchmarkDto
                {
                    Version = benchmark.Version,
                    CreatedOnUtc = benchmark.CreatedOnUtc,
                    CaseCount = benchmark.Cases.Count,
                    Cases = benchmark.Cases.Select(c => new BenchmarkCaseDto
                    {
                        Id = c.Id,
                        Bucket = c.Bucket,
                        QueryLanguage = c.QueryLanguage,
                        QueryText = c.QueryText,
                        SourceTicketId = c.SourceTicketId,
                        Count = c.Count,
                        IncludeAllStatuses = c.IncludeAllStatuses,
                        StatusIds = c.StatusIds,
                        ExpectedTicketIds = c.ExpectedTicketIds,
                        Intent = c.Intent,
                        Notes = c.Notes
                    }).ToList()
                }));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse.Fail("Backend error loading benchmark file: " + ex.Message));
            }
        }

        [HttpGet]
        public async Task<IActionResult> RunRetrievalBenchmark(string? bucket = null, string? caseId = null)
        {
            var result = await _benchmarkService.RunAsync(bucket: bucket, caseId: caseId);
            return Json(ApiResponse<RetrievalBenchmarkResultDto>.Ok(_mapper.Map<RetrievalBenchmarkResultDto>(result)));
        }

        [HttpGet]
        public async Task<IActionResult> GetBenchmarkHistory()
        {
            var history = await _context.RetrievalBenchmarkRuns
                .OrderByDescending(r => r.RunOnUtc)
                .Take(10)
                .Select(r => new BenchmarkHistoryDto
                {
                    Id = r.Id,
                    RunOnUtc = r.RunOnUtc,
                    TotalCases = r.TotalCases,
                    EvaluatedCases = r.EvaluatedCases,
                    HitCases = r.HitCases,
                    HitRate = r.HitRate,
                    Version = r.Version,
                    SettingsJson = r.SettingsJson,
                    ResultsJson = r.ResultsJson
                })
                .ToListAsync();
            return Json(ApiResponse<List<BenchmarkHistoryDto>>.Ok(history));
        }

        [HttpGet]
        public async Task<IActionResult> ValidateRetrievalBenchmark(string? bucket = null)
        {
            var result = await _benchmarkService.ValidateAsync(bucket: bucket);
            return Json(ApiResponse<BenchmarkValidationDto>.Ok(_mapper.Map<BenchmarkValidationDto>(result)));
        }

        [HttpPost("{id}")]
        public async Task<IActionResult> RunAnalysis(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            // Use the queue for sequential processing
            _queueService.Enqueue(id, userId, isRefresh: false);

            return Json(ApiResponse.Ok(_localizer.Get("AnalysisQueued")));
        }

        [HttpPost("{id}")]
        public async Task<IActionResult> RefreshAnalysis(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            // Use the queue for sequential processing
            _queueService.Enqueue(id, userId, isRefresh: true);

            return Json(ApiResponse.Ok(_localizer.Get("ReAnalysisQueued")));
        }

        /// <summary>
        /// Batch analysis — enqueues all tickets for SEQUENTIAL processing through the queue.
        /// No more parallel fire-and-forget.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> RunBulkAnalysis([FromBody] List<int>? ticketIds)
        {
            var user = await _userManager.GetUserAsync(User);
            var userId = user!.Id;

            var idsToAnalyze = ticketIds != null && ticketIds.Any()
                ? ticketIds
                : await _context.Tickets
                    .Where(t => !t.IsDeleted)
                    .Select(t => t.Id)
                    .ToListAsync();

            if (!idsToAnalyze.Any())
                return BadRequest(_localizer.Get("NoTicketsSelected"));

            var batchId = _queueService.EnqueueBatch(idsToAnalyze, userId);

            return Json(ApiResponse<AiBatchAnalysisDto>.Ok(new AiBatchAnalysisDto
            {
                Message = string.Format(_localizer.Get("TicketsQueuedForAnalysis"), idsToAnalyze.Count),
                BatchId = batchId,
                TotalCount = idsToAnalyze.Count
            }));
        }

        [HttpPost]
        public IActionResult StopBatchAnalysis()
        {
            _queueService.StopBatchProcess();
            return Json(ApiResponse.Ok(_localizer.Get("BatchProcessingStopped")));
        }

        [HttpPost]
        public async Task<IActionResult> RunBulkEmbedding([FromBody] List<int>? ticketIds)
        {
            var idsToEmbed = ticketIds != null && ticketIds.Any()
                ? ticketIds
                : await _context.Tickets.Where(t => !t.IsDeleted).Select(t => t.Id).ToListAsync();

            var started = _embeddingService.StartBatch(idsToEmbed);
            if (!started)
                return Json(ApiResponse.Fail(_localizer.Get("EmbeddingBatchRunning")));

            return Json(ApiResponse.Ok(string.Format(_localizer.Get("EmbeddingProcessStarted"), idsToEmbed.Count)));
        }

        [HttpGet]
        public IActionResult GetEmbeddingProgress()
        {
            var p = _embeddingService.GetProgress();
            return Json(ApiResponse<EmbeddingProgressDto>.Ok(new EmbeddingProgressDto
            {
                TotalCount = p.TotalCount,
                CompletedCount = p.CompletedCount,
                FailedCount = p.FailedCount,
                CurrentTicketId = p.CurrentTicketId,
                IsRunning = p.IsRunning,
                ProcessedCount = p.ProcessedCount,
                ProgressPercent = p.ProgressPercent,
                LastErrorMessage = p.LastErrorMessage
            }));
        }

        [HttpPost]
        public IActionResult StopEmbedding()
        {
            _embeddingService.Stop();
            return Json(ApiResponse.Ok(_localizer.Get("EmbeddingProcessStopped")));
        }

        /// <summary>
        /// Real-time batch progress endpoint for the UI to poll.
        /// </summary>
        [HttpGet]
        public IActionResult GetBatchProgress()
        {
            var progress = _queueService.GetBatchProgress();
            var dto = _mapper.Map<BatchProgressDto>(progress);
            dto.QueueLength = _queueService.QueueLength;
            return Json(ApiResponse<BatchProgressDto>.Ok(dto));
        }

        /// <summary>
        /// Get the queue status for a specific ticket.
        /// </summary>
        [HttpGet("{id}")]
        public IActionResult GetQueueStatus(int id)
        {
            var status = _queueService.GetTicketStatus(id);
            if (status == null)
            {
                return Json(ApiResponse<ProvisioningLogDto>.Ok(new ProvisioningLogDto 
                { 
                    Status = "NotStarted", 
                    StatusLabel = _localizer.Get("NotStarted") 
                }));
            }

            return Json(ApiResponse<TicketQueueStatusDto>.Ok(new TicketQueueStatusDto
            {
                TicketId = status.TicketId,
                Status = status.Status.ToString(),
                StatusLabel = _localizer.Get(status.Status.ToString()),
                EnqueuedAt = status.EnqueuedAt.ToString("HH:mm:ss"),
                StartedAt = status.StartedAt?.ToString("HH:mm:ss"),
                CompletedAt = status.CompletedAt?.ToString("HH:mm:ss")
            }));
        }

        [HttpGet]
        public async Task<IActionResult> Hub([FromQuery] GridRequestModel request)
        {
            request.Normalize();

            var query = _context.Tickets
                .AsNoTracking()
                .Include(t => t.Status)
                .Include(t => t.Department)
                .Where(t => !t.IsDeleted);

            if (!string.IsNullOrEmpty(request.SearchString))
            {
                query = query.Where(t => t.TicketNumber.Contains(request.SearchString) || t.Title.Contains(request.SearchString));
            }

            if (request.StatusId.HasValue)
                query = query.Where(t => t.StatusId == request.StatusId.Value);
            
            if (request.PriorityId.HasValue)
                query = query.Where(t => t.PriorityId == request.PriorityId.Value);
            
            if (request.DepartmentId.HasValue)
                query = query.Where(t => t.DepartmentId == request.DepartmentId.Value);
            
            if (request.CategoryId.HasValue)
                query = query.Where(t => t.CategoryId == request.CategoryId.Value);

            // Populate Dropdowns
            ViewBag.StatusId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketStatuses.OrderBy(s => s.Name).ToListAsync(), "Id", "Name", request.StatusId);
            ViewBag.PriorityId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketPriorities.OrderBy(p => p.Name).ToListAsync(), "Id", "Name", request.PriorityId);
            ViewBag.CategoryId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.TicketCategories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", request.CategoryId);
            ViewBag.EntityFilterId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(await _context.Departments.OrderBy(e => e.Name).ToListAsync(), "Id", "Name", request.DepartmentId);

            switch (request.SortOrder)
            {
                case "id_desc": query = query.OrderByDescending(t => t.TicketNumber); break;
                case "Id": query = query.OrderBy(t => t.TicketNumber); break;
                case "date_desc": query = query.OrderByDescending(t => t.CreatedAt); break;
                case "Title": query = query.OrderBy(t => t.Title); break;
                case "title_desc": query = query.OrderByDescending(t => t.Title); break;
                case "Department": query = query.OrderBy(t => t.Department!.Name); break;
                case "entity_desc": query = query.OrderByDescending(t => t.Department!.Name); break;
                case "Status": query = query.OrderBy(t => t.Status!.Name); break;
                case "status_desc": query = query.OrderByDescending(t => t.Status!.Name); break;
                default: query = query.OrderByDescending(t => t.CreatedAt); break;
            }

            // Read this screen under ReadUncommitted to reduce contention with bulk AI jobs,
            // but execute the whole transaction through EF's retry strategy.
            _context.Database.SetCommandTimeout(120);
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            var totalItems = 0;
            var effectivePageSize = request.PageSize;
            var tickets = new List<Ticket>();
            var latestAnalyses = new Dictionary<int, TicketAiAnalysis>();

            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted);

                totalItems = await query.CountAsync();
                effectivePageSize = request.GetEffectivePageSize(totalItems);

                tickets = await query
                    .Skip((request.PageNumber - 1) * effectivePageSize)
                    .Take(effectivePageSize)
                    .ToListAsync();

                var visibleTicketIds = tickets.Select(t => t.Id).ToList();
                latestAnalyses = await _context.TicketAiAnalyses
                    .AsNoTracking()
                    .Where(a => visibleTicketIds.Contains(a.TicketId))
                    .GroupBy(a => a.TicketId)
                    .Select(g => g
                        .OrderByDescending(a => a.RunNumber)
                        .Select(a => new TicketAiAnalysis
                        {
                            TicketId = a.TicketId,
                            Summary = a.Summary,
                            AnalysisStatus = a.AnalysisStatus,
                            RunNumber = a.RunNumber
                        })
                        .First())
                    .ToDictionaryAsync(a => a.TicketId, a => a);











                await transaction.CommitAsync();
            });

            ViewBag.AnalysesByTicketId = latestAnalyses;

            var pagedResult = new PagedResult<Ticket>
            {
                Items = tickets,
                TotalCount = totalItems,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_HubGrid", pagedResult);
            }

            return View(pagedResult);
        }

        // Helper: deserialize legacy JSON blob for backward compat
        private static IEnumerable<object> DeserializeAttachmentJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return Enumerable.Empty<object>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray().Select(e => new
                {
                    FileName = e.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "",
                    Summary = e.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "",
                    Relevance = e.TryGetProperty("relevance", out var rl) ? rl.GetString() ?? "Low" : "Low"
                }).ToList();
            }
            catch { return Enumerable.Empty<object>(); }
        }
        [HttpGet]
        public async Task<IActionResult> GetMetadataHints()
        {
            // Entities come from the active SuperAdminCopilot semantic layer; fields come from
            // the active schema catalog.
            var semanticEntities = _semanticLayer.Config.Entities;
            var entityNamesByTable = semanticEntities
                .Where(e => !string.IsNullOrWhiteSpace(e.Table))
                .GroupBy(e => e.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            var entities = semanticEntities
                .Select(e => new { Name = e.Name, Type = "Department", Aliases = (List<string>)e.Synonyms })
                .ToList();

            var fields = _entityCatalog.Snapshot.Columns
                .Where(c => entityNamesByTable.ContainsKey(c.TableName))
                .Select(c => new
                {
                    Name = c.ColumnName,
                    Department = entityNamesByTable[c.TableName],
                    Type = "Field",
                    Aliases = new List<string>()
                })
                .ToList();

            var temporal = new[] { "today", "yesterday", "this week", "last week", "this month", "last month", "this year", "last year" }
                .Select(p => new { Name = p, Type = "Temporal" }).ToList();

            var operations = new[] { "count", "list", "show", "find", "summarize", "group by", "compare", "average", "sum", "max", "min", "top", "latest" }
                .OrderBy(o => o).ToList();

            return Json(ApiResponse<object>.Ok(new {
                Entities = entities,
                Fields = fields,
                Temporal = temporal,
                Operations = operations
            }));
        }

        /// <summary>
        /// Live type-ahead for the chat input. Returns up to 6 catalog questions whose tokens
        /// overlap with what the user has typed so far (stem-aware), so the dropdown surfaces
        /// real, working questions — "tickets" → "Show tickets created today", "users" → "Users
        /// with more than 5 tickets", etc. Empty <paramref name="q"/> returns the standard
        /// stratified set so the dropdown isn't empty when first focused.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CopilotSuggest(string q)
        {
            try
            {
                var picks = await _suggestionService.GetTypeAheadAsync(q ?? string.Empty);
                return Json(ApiResponse<object>.Ok(new
                {
                    Suggestions = picks.Select(p => new
                    {
                        Question = p.Question,
                        Category = p.Category,
                        Difficulty = p.Difficulty
                    }).ToList()
                }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CopilotSuggest failed for q={Query}", q);
                return Json(ApiResponse<object>.Ok(new { Suggestions = Array.Empty<object>() }));
            }
        }
    }
}

