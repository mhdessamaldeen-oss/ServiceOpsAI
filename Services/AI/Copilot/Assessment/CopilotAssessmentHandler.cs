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

            // Materialise + order once so we can group by SourceSuite below.
            var orderedCases = suite
                .OrderBy(item => item.LoadOrder)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.Question)
                .ToList();

            // Per user direction 2026-05-25: ONE CopilotChatSession PER SUITE FILE. A
            // multi-suite run that picks suites A + B + C creates three sessions titled
            // "A", "B", "C" — the session selector dropdown lists each separately, and
            // every trace / chat message FKs back to its owning suite-session. Cases
            // missing SourceSuite (legacy master catalog fallback) bucket under a single
            // "super-admin-copilot" session so they still get a non-null SessionId.
            var suiteGroups = orderedCases
                .GroupBy(c => string.IsNullOrWhiteSpace(c.SourceSuite) ? "super-admin-copilot" : c.SourceSuite)
                .OrderBy(g => g.Min(c => c.LoadOrder))
                .ToList();

            // PRIMARY session = the one the client already joined for SignalR progress
            // updates (the controller created and returned it before this method ran).
            // We reuse it as the FIRST suite's session so the client doesn't need to
            // re-join SignalR groups mid-run; sessions 2..N are freshly created.
            int primarySessionId;
            if (existingSessionId.HasValue)
            {
                var existingSession = await context.CopilotChatSessions.FirstOrDefaultAsync(s => s.Id == existingSessionId.Value && !s.IsDeleted, ct);
                if (existingSession != null)
                {
                    existingSession.LastInteractionAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    primarySessionId = existingSession.Id;
                }
                else
                {
                    var session = CreateNewAssessmentSession(userId);
                    context.CopilotChatSessions.Add(session);
                    await context.SaveChangesAsync(ct);
                    primarySessionId = session.Id;
                }
            }
            else
            {
                var session = CreateNewAssessmentSession(userId);
                context.CopilotChatSessions.Add(session);
                await context.SaveChangesAsync(ct);
                primarySessionId = session.Id;
            }

            report.SessionId = primarySessionId;

            // Apply per-deployment default latency budget. Cases that set MaxLatencyMs explicitly
            // still win; this only changes the fallback applied to cases that leave it null. Setting
            // is read once per run-start so an in-flight run uses a consistent value.
            var defaultLatency = await GetDefaultAssessmentLatencyMsAsync(context, ct);
            CopilotAssessmentResult.DefaultMaxLatencyMs = defaultLatency;

            var totalInSuite = orderedCases.Count;
            var processedInSuite = 0;

            // Initial progress notification — always targets the primary session group so the
            // client's single SignalR subscription receives the entire multi-suite run's events.
            await _hubContext.Clients.Group(primarySessionId.ToString()).SendAsync("ProgressUpdate", processedInSuite, totalInSuite, runId);

            // Outer loop = one iteration per distinct suite file. Inner loop = the cases of
            // that suite. Each suite opens its own session (the first reuses primarySessionId)
            // and every chat message / trace inside that suite is FK'd to that suite's session.
            // suiteSessionIds collects every session opened by this run so the empty-cleanup
            // pass at the end can soft-delete suites whose cases all failed before persisting
            // any user message — otherwise the Copilot session list ends up cluttered with
            // empty "suite-baseline-…" rows that show no questions when clicked.
            var suiteSessionIds = new List<int>();
            var suiteIndex = 0;
            foreach (var suiteGroup in suiteGroups)
            {
                var suiteLabel = suiteGroup.Key;
                int suiteSessionId;
                if (suiteIndex == 0)
                {
                    suiteSessionId = primarySessionId;
                    await context.CopilotChatSessions
                        .Where(s => s.Id == primarySessionId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, suiteLabel), ct);
                }
                else
                {
                    var nextSession = CreateNewAssessmentSession(userId);
                    nextSession.Title = suiteLabel;
                    context.CopilotChatSessions.Add(nextSession);
                    await context.SaveChangesAsync(ct);
                    suiteSessionId = nextSession.Id;
                }
                suiteSessionIds.Add(suiteSessionId);
                suiteIndex++;

            foreach (var testCase in suiteGroup)
            {
                ct.ThrowIfCancellationRequested();

                // Every case in this suite-group FKs to suiteSessionId (one session per
                // suite file). Multi-turn refinement cases still work because each case's
                // SeedHistory is replayed against the same suiteSessionId before the test
                // question fires — the _specMemory / _refinementDetector both key off
                // ConversationId (= sessionId.ToString()) and see the seeded prior spec.
                var caseSessionId = suiteSessionId;

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

                    // Save the user message EAGERLY (one round-trip) before the bridge call.
                    // Pre-2026-05-25 this was batched with the assistant message into one save
                    // after AskAsync returned — but if AskAsync threw (Ollama timeout, bridge
                    // exception, etc.) the userMsg was discarded with the change tracker, and
                    // the suite-session ended up with zero messages even though the assessment
                    // "ran" the case. With per-suite sessions a suite whose cases all fail
                    // looked completely empty in the Copilot session list. Trade-off: 2× the
                    // round-trips per case (200 instead of 100 for a 100-case run); acceptable
                    // given how rare 100+ case suites are and how confusing empty sessions are.
                    var userMsg = new CopilotChatMessageEntity
                    {
                        SessionId = caseSessionId,
                        Role = ChatMessageRole.User,
                        Content = testCase.Question,
                        CreatedAt = DateTime.UtcNow
                    };
                    context.CopilotChatMessages.Add(userMsg);
                    await context.SaveChangesAsync(ct);

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
                    await _hubContext.Clients.Group(primarySessionId.ToString()).SendAsync("ProgressUpdate", processedInSuite, totalInSuite, runId);
                    await _hubContext.Clients.Group(primarySessionId.ToString()).SendAsync("CaseCompleted", new {
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller explicitly canceled — honor it, stop the loop.
                    throw;
                }
                catch (OperationCanceledException ocEx)
                {
                    // Inner cancellation (HttpClient timeout, internal token) bubbled up but
                    // OUR caller's token is still active. Treat as per-case failure and continue.
                    // Without this catch, a single Ollama timeout kills the whole assessment loop.
                    _logger.LogWarning(ocEx, "Inner cancellation in case {Code} — recording failure and continuing", testCase.Code);
                    report.Results.Add(new CopilotAssessmentResult
                    {
                        Case = testCase,
                        FailureReason = "inner cancellation (likely LLM call timeout): " + ocEx.GetBaseException().Message
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
                // Task.Delay uses ct, but we catch any cancellation so the loop continues — only
                // an EXPLICIT caller-token cancellation aborts the suite.
                if (processedInSuite < totalInSuite)
                {
                    try
                    {
                        var delayMs = await GetAssessmentDelayMsAsync(context, ct);
                        if (delayMs > 0) await Task.Delay(delayMs, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { /* inner CT canceled — continue loop */ }
                }
            } // end: foreach testCase in suiteGroup
            } // end: foreach suiteGroup in suiteGroups

            // Soft-delete any suite-session that finished with zero chat messages — a suite
            // whose every case failed before its userMsg saved would otherwise leave a stale
            // empty row in the Copilot session list. The primary session is excluded so the
            // controller / SignalR group reference stays valid even when nothing landed in it.
            try
            {
                var cleanupCandidates = suiteSessionIds.Where(id => id != primarySessionId).ToList();
                if (cleanupCandidates.Count > 0)
                {
                    var emptyIds = await context.CopilotChatSessions
                        .Where(s => cleanupCandidates.Contains(s.Id) && !s.IsDeleted && !s.Messages.Any())
                        .Select(s => s.Id)
                        .ToListAsync(ct);
                    if (emptyIds.Count > 0)
                    {
                        await context.CopilotChatSessions
                            .Where(s => emptyIds.Contains(s.Id))
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true), ct);
                        _logger.LogInformation("[Assessment] soft-deleted {Count} empty suite session(s): {Ids}", emptyIds.Count, string.Join(",", emptyIds));
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "[Assessment] empty-session cleanup failed — non-fatal");
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
