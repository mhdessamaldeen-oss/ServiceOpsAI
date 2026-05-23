using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using System.Reflection;
using ServiceOpsAI.Services.AI.Copilot.Tools;
using Microsoft.AspNetCore.SignalR;
using ServiceOpsAI.Hubs;
using SuperAdminCopilot.HostBridge;


namespace ServiceOpsAI.Services.AI.Copilot.Assessment
{
    /// <summary>
    /// Loads the curated Copilot assessment catalog and runs the active assessment suite.
    /// The same catalog definitions drive the assessment lab and Copilot sample libraries.
    /// </summary>
    public class CopilotAssessmentHandler
    {
        private const string DefaultSurface = CopilotSurfaces.Default;

        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly IHubContext<CopilotAssessmentHub> _hubContext;
        private readonly ILogger<CopilotAssessmentHandler> _logger;
        // SuperAdminCopilot v2 bridge — assessment scenarios now route through the new pipeline.
        private readonly ISuperAdminCopilotChatBridge _superAdminCopilotChatBridge;
        private readonly SuperAdminCopilot.Eval.IExecutionAccuracyChecker _exChecker;
        // C1 — auto-promotion writer. When EnableAssessmentAutoPromotion is on, EX-passing cases
        // that came via the LLM path are appended to verified-queries.json so the catalog grows
        // with every blessed run. Idempotent on canonical question text.
        private readonly SuperAdminCopilot.Retrieval.IVerifiedQueryWriter _verifiedQueryWriter;
        private readonly Microsoft.Extensions.Options.IOptions<SuperAdminCopilot.Configuration.CopilotOptions> _copilotOptions;

        private readonly string _catalogPath;
        private readonly string _suitesFolder;
        private List<string> _activeSuiteFiles = new();
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        public CopilotAssessmentHandler(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            IWebHostEnvironment environment,
            IHubContext<CopilotAssessmentHub> hubContext,
            ILogger<CopilotAssessmentHandler> logger,
            ISuperAdminCopilotChatBridge superAdminCopilotChatBridge,
            SuperAdminCopilot.Eval.IExecutionAccuracyChecker exChecker,
            SuperAdminCopilot.Retrieval.IVerifiedQueryWriter verifiedQueryWriter,
            Microsoft.Extensions.Options.IOptions<SuperAdminCopilot.Configuration.CopilotOptions> copilotOptions)

        {
            _contextFactory = contextFactory;
            _hubContext = hubContext;
            _logger = logger;
            _superAdminCopilotChatBridge = superAdminCopilotChatBridge;
            _exChecker = exChecker;
            _verifiedQueryWriter = verifiedQueryWriter;
            _copilotOptions = copilotOptions;
            _catalogPath = Path.Combine(environment.ContentRootPath, "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json");
            // ── SuperAdminCopilot v2 cutover (2026-05-09) ─────────────────────────────────
            // The assessment page lists JSON files from the in-host SuperAdminCopilot area.
            _suitesFolder = Path.Combine(environment.ContentRootPath, "Areas", "SuperAdminCopilot", "Configuration", "QuestionSuites");
        }

        // Cache for ListSuites — every page load + suite dropdown + run-start used to walk
        // the suites folder and deserialise EVERY JSON file. Cache snapshots the result with a
        // "fingerprint" of the folder's highest LastWriteTimeUtc + the file count. If either
        // changes (suite added / edited / removed) the cache is recomputed; otherwise we
        // return the snapshot in microseconds.
        private (string Fingerprint, IReadOnlyList<AssessmentSuiteOption> Suites)? _listSuitesCache;
        private readonly object _listSuitesCacheGate = new();

        /// <summary>
        /// Enumerate suite files in the Suites/ folder. Most-recently-modified first; the first
        /// entry is flagged <c>IsDefault=true</c> so the UI dropdown can preselect it. Returns an
        /// empty list when the folder is missing.
        /// </summary>
        public IReadOnlyList<AssessmentSuiteOption> ListSuites()
        {
            if (!Directory.Exists(_suitesFolder)) return Array.Empty<AssessmentSuiteOption>();

            var files = Directory.GetFiles(_suitesFolder, "*.json");
            if (files.Length == 0) return Array.Empty<AssessmentSuiteOption>();

            // Fingerprint = count + max mtime. Cheap to compute (one stat call per file) and
            // invalidates on any edit/add/remove.
            var maxMtime = files.Select(File.GetLastWriteTimeUtc).Max();
            var fingerprint = $"{files.Length}::{maxMtime.Ticks}";
            var snapshot = _listSuitesCache;
            if (snapshot is { } valid && valid.Fingerprint == fingerprint) return valid.Suites;

            lock (_listSuitesCacheGate)
            {
                snapshot = _listSuitesCache;
                if (snapshot is { } stillValid && stillValid.Fingerprint == fingerprint) return stillValid.Suites;

                var entries = files
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(path =>
                    {
                        int count = 0;
                        try
                        {
                            using var stream = File.OpenRead(path);
                            var data = JsonSerializer.Deserialize<AssessmentData>(stream, _jsonOptions);
                            count = data?.Scenarios?.Count ?? 0;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to read suite file {Path}; counting 0 scenarios.", path);
                        }
                        return new AssessmentSuiteOption
                        {
                            FileName = Path.GetFileName(path),
                            DisplayName = Path.GetFileNameWithoutExtension(path),
                            ScenarioCount = count,
                            IsDefault = false
                        };
                    })
                    .ToList();

                if (entries.Count > 0) entries[0].IsDefault = true;
                _listSuitesCache = (fingerprint, entries);
                return entries;
            }
        }

        /// <summary>
        /// Select a single suite file as the active one. Pass null/empty to revert to the
        /// "newest in folder" default. Returns true on success, false when the named file is
        /// missing.
        /// </summary>
        public bool SetActiveSuite(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _activeSuiteFiles = new List<string>();
                return true;
            }

            // The suite-picker UI joins checkbox values with commas before sending.
            // Without this branch the path lookup treats "a.json,b.json" as one filename
            // and silently fails — the run then falls back to the legacy master catalog
            // and the Forensic Coverage total + Suite column both show stale data.
            if (fileName.Contains(','))
            {
                var files = fileName
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
                SetActiveSuites(files);
                return _activeSuiteFiles.Count > 0;
            }

            var path = Path.Combine(_suitesFolder, fileName);
            if (!File.Exists(path)) return false;
            _activeSuiteFiles = new List<string> { fileName };
            return true;
        }

        /// <summary>
        /// Select multiple suite files for a multi-suite run. Each file must exist under
        /// <c>Suites/</c>; missing entries are silently dropped.
        /// </summary>
        public void SetActiveSuites(string[] fileNames)
        {
            if (fileNames == null || fileNames.Length == 0)
            {
                _activeSuiteFiles = new List<string>();
                return;
            }
            // Sort selected suites alphabetically so a run always proceeds in name order
            // (01 → 02 → … → 10), regardless of the order the user ticked checkboxes. With
            // the "NN - Realistic …" naming convention this gives oldest-to-newest execution.
            _activeSuiteFiles = fileNames
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Where(f => File.Exists(Path.Combine(_suitesFolder, f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }


        public class AssessmentData
        {
            public List<CopilotAssessmentCase> Scenarios { get; set; } = new();
            public List<CopilotEvaluationEntry> Evaluations { get; set; } = new();
        }

        public async Task<CopilotAssessmentLabViewModel> GetAssessmentLabViewModelAsync()
        {
            var data = await LoadDataAsync();
            var assessmentCases = await GetDefaultTestSuiteAsync();

            return new CopilotAssessmentLabViewModel
            {
                CaseGroups = assessmentCases
                    .GroupBy(testCase => new { testCase.Category, testCase.CategoryDescription })
                    .Select(group => new CopilotAssessmentCaseGroup
                    {
                        Category = group.Key.Category,
                        Description = group.Key.CategoryDescription,
                        Cases = group.ToList() // suite is already ordered
                    })
                    .ToList(),
                CopilotSampleGroups = BuildCopilotSampleGroups(data.Scenarios, DefaultSurface)
            };
        }

        public async Task<List<CopilotPromptGroup>> GetCopilotPromptGroupsAsync(string surface = DefaultSurface)
        {
            var data = await LoadDataAsync();
            return BuildCopilotSampleGroups(data.Scenarios, surface);
        }

        public async Task<CopilotAssessmentReport> RunAssessmentAsync(IEnumerable<CopilotAssessmentCase> suite, string? userId = null, int? existingSessionId = null, Guid? specificRunId = null, CancellationToken ct = default)
        {
            var report = new CopilotAssessmentReport();
            var runId = specificRunId ?? Guid.NewGuid();
            var metadata = JsonSerializer.Serialize(new { Source = "AssessmentSuite", RunId = runId });

            using var context = await _contextFactory.CreateDbContextAsync(ct);

            // The controller already creates and persists the session before this runs.
            // We just need to resolve the session ID for message tracking.
            int sessionId;
            if (existingSessionId.HasValue)
            {
                var existingSession = await context.CopilotChatSessions.FirstOrDefaultAsync(s => s.Id == existingSessionId.Value && !s.IsDeleted, ct);
                if (existingSession != null)
                {
                    existingSession.LastInteractionAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    sessionId = existingSession.Id;
                }
                else
                {
                    // Fallback: create a new session if the passed one was somehow deleted
                    var session = CreateNewAssessmentSession(userId);
                    context.CopilotChatSessions.Add(session);
                    await context.SaveChangesAsync(ct);
                    sessionId = session.Id;
                }
            }
            else
            {
                var session = CreateNewAssessmentSession(userId);
                context.CopilotChatSessions.Add(session);
                await context.SaveChangesAsync(ct);
                sessionId = session.Id;
            }

            report.SessionId = sessionId;

            // Title = suite filename (without .json) so the Copilot UI dropdown shows exactly
            // the suite the user picked. Per user direction 2026-05-19: just the suite name,
            // no "Assessment Run" prefix, no timestamp — LastInteractionAt distinguishes reruns.
            var primarySuiteFile = _activeSuiteFiles.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(primarySuiteFile))
            {
                var suiteLabel = Path.GetFileNameWithoutExtension(primarySuiteFile);
                await context.CopilotChatSessions
                    .Where(s => s.Id == sessionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, suiteLabel), ct);
            }

            // Apply per-deployment default latency budget. Cases that set MaxLatencyMs explicitly
            // still win; this only changes the fallback applied to cases that leave it null. Setting
            // is read once per run-start so an in-flight run uses a consistent value.
            var defaultLatency = await GetDefaultAssessmentLatencyMsAsync(context, ct);
            CopilotAssessmentResult.DefaultMaxLatencyMs = defaultLatency;

            var totalInSuite = suite.Count();
            var processedInSuite = 0;

            // Initial progress notification
            await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("ProgressUpdate", processedInSuite, totalInSuite, runId);


            // Multi-suite contract: process every scenario from the first picked suite to
            // completion before touching the next suite — LoadOrder is monotonic across suites
            // in the order the user selected them. Single-suite runs leave LoadOrder = 0, so
            // the existing SortOrder / Question tiebreakers still control order.
            foreach (var testCase in suite
                .OrderBy(item => item.LoadOrder)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.Question))
            {
                ct.ThrowIfCancellationRequested();

                // ── One session per suite-run (UI grouping) ───────────────────────
                // Per user instruction 2026-05-19: all cases of a single suite-run share ONE
                // CopilotChatSessions row so the Copilot UI's "open this session" view shows the
                // whole assessment as a single conversation. Trade-off: the orchestrator's
                // _specMemory + _refinementDetector (keyed by ConversationId, which the bridge
                // derives from SessionId) will see case N's spec when case N+1 starts — which
                // could cause case N+1 to be detected as a refinement. The user explicitly
                // accepts that trade-off: UI consistency over per-case isolation.
                //
                // Multi-turn cases (those with SeedHistory) still work correctly because the
                // refinement detector compares the question text against the seed-turn, not
                // against arbitrary prior cases.
                //
                // (Previous design — per-case session for SpecMemory isolation — was reverted
                // here; older runs' data is consolidated via Tests/consolidate-sessions.sql.)
                var caseSessionId = sessionId;

                try
                {
                    // Multi-turn refinement: if the case carries SeedHistory[], replay each
                    // User-role turn through AskAsync FIRST (same SessionId) so the orchestrator's
                    // _specMemory + _refinementDetector see the prior spec when the actual test
                    // question arrives. Without this pre-pass, every refinement case ("only the
                    // critical ones" after "show me open tickets") starts with empty spec memory
                    // and falls through to a fresh SpecExtractor pass that has no context.
                    //
                    // Skips on cases without SeedHistory (single-turn). Failures during a seed
                    // turn are logged but don't abort the case — the actual test question still
                    // gets its chance, just without the seeded context.
                    if (testCase.SeedHistory is { Count: > 0 })
                    {
                        foreach (var seedTurn in testCase.SeedHistory)
                        {
                            if (seedTurn?.Role != ChatMessageRole.User) continue;
                            if (string.IsNullOrWhiteSpace(seedTurn.Content)) continue;
                            try
                            {
                                var seedReq = new CopilotChatRequest
                                {
                                    Question = seedTurn.Content,
                                    SessionId = caseSessionId,
                                    Metadata = metadata,
                                    IsAssessment = true
                                };
                                await _superAdminCopilotChatBridge.AskAsync(seedReq, ct);
                            }
                            catch (Exception seedEx)
                            {
                                _logger.LogWarning(seedEx, "Seed turn failed for case {Code}: '{Q}' — continuing", testCase.Code, seedTurn.Content);
                            }
                        }
                    }

                    // Queue both the user message and (later) the assistant message in the same
                    // change-tracker batch — they get flushed together after the pipeline runs.
                    // Previously two separate SaveChangesAsync per case meant 2 × N round-trips
                    // for an N-case suite (200 RTs for a 100-case run); now it's N.
                    var userMsg = new CopilotChatMessageEntity
                    {
                        SessionId = caseSessionId,
                        Role = ChatMessageRole.User,
                        Content = testCase.Question,
                        CreatedAt = DateTime.UtcNow
                    };
                    context.CopilotChatMessages.Add(userMsg);

                    var request = new CopilotChatRequest
                    {
                        Question = testCase.Question,
                        History = CloneHistory(testCase.SeedHistory),
                        AssessmentRunId = runId,
                        Metadata = metadata,
                        SessionId = caseSessionId,
                        CaseCode = testCase.Code,
                        // The suite filename ("section ID") — the assessment grid uses this to
                        // group cases by category. Without it the trace row gets the generic
                        // SourceSuite="super-admin-copilot" tag and the grid can't tell which
                        // file produced each answer.
                        SourceSuite = testCase.SourceSuite,
                        // Forward the curated reference SQL so the bridge can persist it to
                        // CopilotTraceHistory.ExpectedScript next to the GeneratedScript for
                        // side-by-side comparison from the DB or admin grid.
                        ExpectedSql = testCase.ExpectedSql,
                        IsAssessment = true
                    };

                    var startTime = DateTime.UtcNow;
                    // ── SuperAdminCopilot v2 cutover (2026-05-09) ─────────────────────────
                    // Route assessment scenarios through the active in-host SuperAdminCopilot pipeline.
                    // var response = await _copilotService.AskAsync(request);                  // pre-2026 dead path
                    var response = await _superAdminCopilotChatBridge.AskAsync(request, ct);
                    var endTime = DateTime.UtcNow;

                    // Save both messages in one SaveChanges — user message was queued before the
                    // bridge call (tracker only, no DB write), assistant message added here, then
                    // a single round-trip persists the pair. Halves the per-case write count.
                    var assistantMsg = new CopilotChatMessageEntity
                    {
                        SessionId = caseSessionId,
                        Role = ChatMessageRole.Assistant,
                        Content = response.Answer,
                        TraceId = response.TraceId,
                        CreatedAt = DateTime.UtcNow
                    };
                    context.CopilotChatMessages.Add(assistantMsg);
                    await context.SaveChangesAsync(ct);

                    var result = new CopilotAssessmentResult
                    {
                        Case = testCase,
                        ActualResponse = response,
                        LatencyMs = (long)(endTime - startTime).TotalMilliseconds,
                        TraceId = response.TraceId
                    };

                    // ── Execution Accuracy check ───────────────────────────────────────
                    // When the case carries a curated ExpectedSql and the copilot produced rows,
                    // run the rigorous multiset comparison: execute both queries, compare result
                    // sets order-independently. This is the strongest correctness signal — beats
                    // row-count / column-shape alone. Result is stamped on the assessment row so
                    // the grid can show it.
                    if (!string.IsNullOrWhiteSpace(testCase.ExpectedSql))
                    {
                        try
                        {
                            // Convert the bridge's flattened string-keyed structured rows back
                            // to the IReadOnlyList<IReadOnlyDictionary<string, object?>> shape
                            // the checker expects. Same data, just unboxed.
                            var copilotRows = response.StructuredRows?
                                .Select(r => (IReadOnlyDictionary<string, object?>)r.Values
                                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase))
                                .ToList();
                            var exResult = await _exChecker.CheckAsync(testCase.ExpectedSql!, copilotRows, ct);
                            result.ExAccuracy = exResult.Match;
                            result.ExExpectedRowCount = exResult.ExpectedRowCount;
                            result.ExError = exResult.Error;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[AssessmentEX] Execution-accuracy check threw for case {Code}", testCase.Code);
                            result.ExAccuracy = null;
                            result.ExError = ex.Message;
                        }
                    }

                    // ── C1 auto-promotion ─────────────────────────────────────────
                    // When enabled, append EX-passing LLM-emitted SQL to verified-queries.json so
                    // the catalog grows organically. Skip if the answer already came from the
                    // catalog (provenance "verified") — that path doesn't teach us anything new.
                    // Idempotent on canonical question, so re-runs add nothing.
                    if (_copilotOptions.Value.EnableAssessmentAutoPromotion
                        && result.ExAccuracy == true
                        && !string.IsNullOrWhiteSpace(response.Notes)
                        && !string.Equals(response.ExecutionDetails?.Provenance, "verified", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(response.ExecutionDetails?.Provenance, "refusal", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var draft = new SuperAdminCopilot.Retrieval.VerifiedQueryDraft(
                                Question: testCase.Question,
                                Sql: response.Notes,
                                Shape: testCase.Category,
                                VerifiedBy: $"auto-promoted via assessment run {runId:N}",
                                Description: $"Promoted from EX-passing case {testCase.Code} on {DateTime.UtcNow:yyyy-MM-dd}.");
                            await _verifiedQueryWriter.AppendAsync(draft, ct);
                        }
                        catch (Exception promoteEx)
                        {
                            // Non-fatal — promotion failure shouldn't fail the assessment.
                            _logger.LogWarning(promoteEx, "[AutoPromote] failed for case {Code}", testCase.Code);
                        }
                    }

                    await CacheTraceAssessmentAsync(context, result, ct);
                    report.Results.Add(result);
                    
                    processedInSuite++;
                    await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("ProgressUpdate", processedInSuite, totalInSuite, runId);
                    await _hubContext.Clients.Group(sessionId.ToString()).SendAsync("CaseCompleted", new {
                        Id = result.Case.Id,
                        IsSuccess = result.IsSuccess,
                        LatencyMs = result.LatencyMs,
                        ActualMode = result.ActualMode,
                        ActualIntent = result.ActualIntent,
                        ActualTool = result.ActualTool,
                        AnswerPreview = result.ActualResponse?.Answer ?? "",
                        Detail = result.ActualResponse?.ExecutionDetails?.Summary ?? "",
                        TraceId = result.TraceId,
                        HasAnswer = result.HasAnswer
                    });



                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to run assessment case: {Question}", testCase.Question);
                    report.Results.Add(new CopilotAssessmentResult
                    {
                        Case = testCase,
                        FailureReason = ex.GetBaseException().Message
                    });
                }

                // Throttle between cases to stay under provider rate limits (Gemini free tier = 10 RPM).
                // Reads from the GeminiAssessmentDelayMs system setting; default 7000ms (≈8.5 RPM with margin).
                // Skips the delay after the last case so we don't pad the total run time.
                if (processedInSuite < totalInSuite)
                {
                    var delayMs = await GetAssessmentDelayMsAsync(context, ct);
                    if (delayMs > 0) await Task.Delay(delayMs, ct);
                }
            }

            report.TotalCases = report.Results.Count;
            report.SuccessCount = report.Results.Count(r => r.IsSuccess);
            report.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

            // Persist run summary so users can compare runs over time.
            try
            {
                var latencies = report.Results.Select(r => r.LatencyMs).Where(ms => ms > 0).ToList();
                var failedCodes = report.Results
                    .Where(r => !r.IsSuccess)
                    .Select(r => r.Case?.Code ?? string.Empty)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                var summary = new CopilotAssessmentRunSummary
                {
                    RunId = runId,
                    RunAt = DateTime.UtcNow,
                    ModelName = report.Results.FirstOrDefault()?.ActualResponse?.ModelName,
                    TotalCases = report.TotalCases,
                    PassCount = report.SuccessCount,
                    FailCount = report.TotalCases - report.SuccessCount,
                    AvgLatencyMs = latencies.Count > 0 ? (long)latencies.Average() : 0,
                    MaxLatencyMs = latencies.Count > 0 ? latencies.Max() : 0,
                    FailedCaseCodes = JsonSerializer.Serialize(failedCodes)
                };
                context.CopilotAssessmentRunSummaries.Add(summary);
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist CopilotAssessmentRunSummary for run {RunId}", runId);
            }

            return report;
        }

        private static async Task CacheTraceAssessmentAsync(
            ApplicationDbContext context,
            CopilotAssessmentResult result,
            CancellationToken ct)
        {
            // The assessment results are now transient or managed via ExecutionPlan JSON.
            // Direct column-level persistence for assessment metadata has been decommissioned.
            await Task.CompletedTask;
        }

        /// <summary>
        /// How long to wait between assessment cases. Reads from SystemSettings so users can adjust
        /// the throttle from the UI based on their provider's rate limit (Gemini free = 10 RPM → ~7s).
        /// Returns 0 if no delay is configured (zero throttling). Honors the assessment-loop
        /// cancellation token so a cancel during the DB read propagates instead of waiting for the
        /// EF default timeout.
        /// </summary>
        private static async Task<int> GetAssessmentDelayMsAsync(ApplicationDbContext context, CancellationToken ct = default)
        {
            var setting = await context.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiAssessmentDelayMs, ct);
            return int.TryParse(setting?.Value, out var v) && v >= 0 ? v : 0;
        }

        /// <summary>
        /// Default per-case latency budget (ms) applied when a case does not set
        /// <c>MaxLatencyMs</c> explicitly. Sourced from <c>CopilotAssessmentDefaultLatencyMs</c>
        /// SystemSettings row; falls back to 30000 (30s) for backward compatibility. Operators
        /// running against slow local models (e.g. 7B Ollama on modest hardware) should set this
        /// to ~120000 so VQ-Explainer cases don't false-fail on latency.
        /// </summary>
        private static async Task<long> GetDefaultAssessmentLatencyMsAsync(ApplicationDbContext context, CancellationToken ct = default)
        {
            var setting = await context.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == SettingKeys.CopilotAssessmentDefaultLatencyMs, ct);
            return long.TryParse(setting?.Value, out var v) && v > 0 ? v : 30000;
        }

        public static CopilotChatSession CreateNewAssessmentSession(string? userId)
        {
            return new CopilotChatSession
            {
                UserId = userId,
                Title = $"Automated Assessment: {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                IsAssessment = true,
                CreatedAt = DateTime.UtcNow,
                LastInteractionAt = DateTime.UtcNow,
                Surface = CopilotSurfaces.Default
            };
        }

        public CopilotAssessmentRunSummaryDto BuildRunSummary(CopilotAssessmentReport report, int summaryId)
        {
            return new CopilotAssessmentRunSummaryDto
            {
                SummaryId = summaryId,
                SessionId = report.SessionId,
                RunAt = report.RunAt,
                TotalCases = report.TotalCases,
                SuccessCount = report.SuccessCount,
                SuccessRate = report.SuccessRate,
                AverageLatencyMs = report.AverageLatencyMs,
                Results = report.Results
                    .OrderBy(result => result.Case.SortOrder)
                    .ThenBy(result => result.Case.Question)
                    .Select(result => new CopilotAssessmentCaseResultDto
                    {
                        Id = result.Case.Id,
                        Code = result.Case.Code,
                        Category = result.Case.Category,
                        Difficulty = result.Case.Difficulty,
                        Question = result.Case.Question,
                        ExpectedBehavior = result.Case.ExpectedBehaviorSummary,
                        ActualMode = result.ActualMode,
                        ActualIntent = result.ActualIntent,
                        ActualTool = result.ActualTool,
                        Detail = result.Detail,
                        Answer = result.ActualResponse?.Answer ?? string.Empty,
                        AnswerPreview = result.AnswerPreview,
                        LatencyMs = result.LatencyMs,
                        IsSuccess = result.IsSuccess,
                        TraceId = result.ActualResponse?.TraceId,
                        ExpectedSql = result.Case.CorrectAnswer?.SQL,
                        GeneratedSql = result.ActualResponse?.StructuredQueryResults?
                            .Select(r => r.Execution?.GeneratedSql)
                            .FirstOrDefault(sql => !string.IsNullOrWhiteSpace(sql))
                            ?? result.ActualResponse?.ExecutionDetails?.LastTechnicalData
                    })
                    .ToList()
            };
        }

        public async Task<List<CopilotAssessmentCase>> GetTestSuiteAsync(List<string> caseIds)
        {
            var suite = await GetDefaultTestSuiteAsync();
            var idSet = caseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return suite
                .Where(testCase => idSet.Contains(testCase.Id))
                .ToList();
        }

        public async Task<List<CopilotAssessmentCase>> GetDefaultTestSuiteAsync()
        {
            // When the user selected one or more suite files, load and union them.
            // Otherwise fall back to the master copilot-assessment.json.
            //
            // Per-scenario isolation: we parse the file as a raw JsonDocument first, then
            // deserialize each scenario individually inside its own try/catch. A single typo
            // ("ExpectedIntent":"KnowledgeMatch" — not a valid CopilotIntentKind) used to
            // throw on the whole-document DeserializeAsync call, silently dropping every
            // scenario in the suite and falling back to the legacy master file. Now bad
            // scenarios are logged-and-skipped while their siblings still load.
            var scenarios = new List<CopilotAssessmentCase>();
            var multiSuite = _activeSuiteFiles.Count > 1;
            if (_activeSuiteFiles.Count > 0)
            {
                // In multi-suite mode we use LoadOrder as the primary sort key so all scenarios
                // from one suite run to completion before the next suite begins. The order of
                // _activeSuiteFiles (preserved from the UI checkbox order) determines suite order;
                // within a suite, the index in the file determines question order. A monotonic
                // counter across the whole load is the simplest way to encode both.
                var loadCounter = 0;
                foreach (var file in _activeSuiteFiles)
                {
                    var path = Path.Combine(_suitesFolder, file);
                    if (!File.Exists(path)) continue;
                    var suiteLabel = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        await using var stream = File.OpenRead(path);
                        using var doc = await JsonDocument.ParseAsync(stream);
                        if (!doc.RootElement.TryGetProperty("Scenarios", out var scenariosEl)
                            && !doc.RootElement.TryGetProperty("scenarios", out scenariosEl))
                        {
                            _logger.LogWarning("Suite file {Path} has no 'Scenarios' array; skipping.", path);
                            continue;
                        }
                        if (scenariosEl.ValueKind != JsonValueKind.Array)
                        {
                            _logger.LogWarning("Suite file {Path} 'Scenarios' is not an array; skipping.", path);
                            continue;
                        }
                        int skippedInFile = 0;
                        foreach (var scenarioEl in scenariosEl.EnumerateArray())
                        {
                            try
                            {
                                var raw = scenarioEl.GetRawText();
                                var scenario = JsonSerializer.Deserialize<CopilotAssessmentCase>(raw, _jsonOptions);
                                if (scenario is null) { skippedInFile++; continue; }
                                scenario.SourceSuite = suiteLabel;
                                scenario.LoadOrder = loadCounter++;
                                scenarios.Add(scenario);
                            }
                            catch (JsonException jex)
                            {
                                skippedInFile++;
                                var codeHint = scenarioEl.TryGetProperty("Code", out var c) ? c.GetString() : null;
                                _logger.LogWarning(jex,
                                    "Suite {File} scenario {Code} failed to deserialize; skipping. Raw: {Raw}",
                                    file, codeHint ?? "(unknown)", Truncate(scenarioEl.GetRawText(), 200));
                            }
                        }
                        if (skippedInFile > 0)
                            _logger.LogInformation("Suite {File}: {Skipped} scenario(s) skipped due to deserialization errors.", file, skippedInFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read suite file {Path}; skipping.", path);
                    }
                }
            }
            else
            {
                var data = await LoadDataAsync();
                scenarios = data.Scenarios.ToList();
            }

            // Multi-suite: keep one suite's questions contiguous (LoadOrder is monotonic across
            // suites in the picked order). Single-suite / legacy: fall back to SortOrder + Question.
            return multiSuite
                ? scenarios
                    .OrderBy(testCase => testCase.LoadOrder)
                    .ThenBy(testCase => testCase.SortOrder)
                    .ThenBy(testCase => testCase.Question)
                    .ToList()
                : scenarios
                    .OrderBy(testCase => testCase.SortOrder)
                    .ThenBy(testCase => testCase.Question)
                    .ToList();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        // ── Consolidated Evaluation Methods ──

        public async Task<List<CopilotEvaluationEntry>> GetEvaluationsAsync()
        {
            var data = await LoadDataAsync();
            return data.Evaluations;
        }

        public async Task SaveEvaluationAsync(CopilotEvaluationEntry entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                var data = await LoadDataAsync();
                var existing = data.Evaluations.FirstOrDefault(e => e.TicketId == entry.TicketId);
                if (existing != null)
                {
                    data.Evaluations.Remove(existing);
                }

                data.Evaluations.Add(entry);
                data.Evaluations = data.Evaluations.OrderByDescending(e => e.EvaluatedOnUtc).ToList();

                await using var stream = File.Create(_catalogPath);
                await JsonSerializer.SerializeAsync(stream, data, _jsonOptions);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<AssessmentData> LoadDataAsync()
        {
            var pathsToTry = new[]
            {
                _catalogPath,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "copilot-assessment.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json")
            };

            string? finalPath = null;
            foreach (var path in pathsToTry.Distinct())
            {
                if (File.Exists(path))
                {
                    finalPath = path;
                    break;
                }
            }

            if (finalPath == null)
            {
                _logger.LogWarning("Copilot assessment catalog not found in any searched locations.");
                return new AssessmentData 
                { 
                    Scenarios = new List<CopilotAssessmentCase> 
                    { 
                        new CopilotAssessmentCase { Code = "ERR-404", Question = "Catalog file not found. Checked: " + string.Join(", ", pathsToTry), Category = "System Error" } 
                    } 
                };
            }

            try
            {
                var json = await File.ReadAllTextAsync(finalPath);
                var data = JsonSerializer.Deserialize<AssessmentData>(json, _jsonOptions);

                if (data?.Scenarios != null)
                {
                    foreach (var testCase in data.Scenarios)
                    {
                        NormalizeCase(testCase);
                    }
                }

                return data ?? new AssessmentData();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize copilot-assessment data from {Path}", finalPath);
                return new AssessmentData
                {
                    Scenarios = new List<CopilotAssessmentCase>
                    {
                        new CopilotAssessmentCase { Code = "ERR-JSON", Question = "Failed to load JSON: " + ex.Message, Category = "System Error" }
                    }
                };
            }
        }

        private static void NormalizeCase(CopilotAssessmentCase testCase)
        {
            testCase.Category = string.IsNullOrWhiteSpace(testCase.Category) ? "General" : testCase.Category.Trim();
            testCase.CategoryDescription = string.IsNullOrWhiteSpace(testCase.CategoryDescription) ? string.Empty : testCase.CategoryDescription.Trim();
            testCase.LibraryGroup = string.IsNullOrWhiteSpace(testCase.LibraryGroup) ? testCase.Category : testCase.LibraryGroup.Trim();

            testCase.LibrarySurfaces = testCase.LibrarySurfaces?.Select(s => s.Trim()).ToList() ?? new List<string> { CopilotSurfaces.Default };
            if (testCase.LibrarySurfaces.Count == 0)
            {
                testCase.LibrarySurfaces.Add(CopilotSurfaces.Default);
            }

            // Sync from CorrectAnswer if root properties are empty
            if (testCase.CorrectAnswer != null)
            {
                if (testCase.ExpectedFilters == null || !testCase.ExpectedFilters.Any())
                {
                    testCase.ExpectedFilters = testCase.CorrectAnswer.ExpectedFilters?.Cast<dynamic>().ToList();
                }

                if (testCase.ExpectedAnswerKeywords == null || !testCase.ExpectedAnswerKeywords.Any())
                {
                    testCase.ExpectedAnswerKeywords = testCase.CorrectAnswer.ExpectedAnswerKeywords;
                }

                if (testCase.ExpectedClarificationKeywords == null || !testCase.ExpectedClarificationKeywords.Any())
                {
                    testCase.ExpectedClarificationKeywords = testCase.CorrectAnswer.ExpectedClarificationKeywords;
                }

                if (testCase.ExpectedClarification.HasValue && testCase.ExpectedClarification.Value && !testCase.ExpectedIntent.HasValue)
                {
                    testCase.ExpectedIntent = CopilotIntentKind.Clarification;
                }
            }

            testCase.GenerateStableId();
        }

        private static List<CopilotPromptGroup> BuildCopilotSampleGroups(IEnumerable<CopilotAssessmentCase> cases, string surface)
        {
            return cases
                .Where(testCase => testCase.IncludeInCopilotLibrary && testCase.SupportsSurface(surface))
                .GroupBy(testCase => testCase.LibraryGroup)
                .OrderBy(group => group.Min(testCase => testCase.SortOrder))
                .Select(group => new CopilotPromptGroup
                {
                    Title = group.Key,
                    Prompts = group
                        .OrderBy(testCase => testCase.SortOrder)
                        .Select(testCase => new CopilotSamplePrompt
                        {
                            Text = testCase.Question,
                            Code = testCase.Code,
                            SourceSuite = testCase.SourceSuite
                        })
                        .DistinctBy(p => p.Text, StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToList()
                })
                .Where(group => group.Prompts.Count > 0)
                .ToList();
        }

        private static List<CopilotSamplePrompt> SplitToolPrompts(string testPrompt, string? codePrefix = null)
        {
            return testPrompt
                .Split(new[] { "\r\n", "\n", "||" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select((text, index) => new CopilotSamplePrompt
                {
                    Text = text,
                    Code = codePrefix != null ? $"{codePrefix}-{index + 1}" : null
                })
                .ToList();
        }
        private static List<CopilotChatMessage> CloneHistory(IEnumerable<CopilotChatMessage> history)
        {
            return history
                .Select(message => new CopilotChatMessage
                {
                    Role = message.Role,
                    Content = message.Content
                })
                .ToList();
        }
    }
}
