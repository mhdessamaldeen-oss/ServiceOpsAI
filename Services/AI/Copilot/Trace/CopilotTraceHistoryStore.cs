using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ServiceOpsAI.Services.AI.Copilot.Trace
{
    public class CopilotTraceHistoryStore
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly CopilotTracePersistenceOptions _options;
        private readonly ILogger<CopilotTraceHistoryStore> _logger;

        // C2 hardening: track consecutive save failures so ops dashboards can alert on
        // persistent DB issues. EventId 9001 is reserved for trace-persistence failures.
        private static readonly EventId TraceSaveFailedEvent = new(9001, "TraceSaveFailed");
        private int _consecutiveFailures;
        // B1 fix: Must match the [StringLength(4000)] on CopilotTraceHistory.GeneratedScript.
        // Previously 50_000 which exceeded the DB column and caused silent SqlException truncation.
        private const int MaxGeneratedScriptLength = 4_000;

        public CopilotTraceHistoryStore(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            IOptions<CopilotTracePersistenceOptions> options,
            ILogger<CopilotTraceHistoryStore> logger)
        {
            _contextFactory = contextFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<int?> SaveAsync(
            CopilotChatResponse response,
            long elapsedMs,
            int? sessionId = null,
            string? caseCode = null,
            string? pipelineTraceId = null,
            string? generatedScript = null,
            string? errorMessage = null,
            string? sourceSuite = null,
            string? expectedScript = null,
            int? llmCallCount = null,
            int? totalPromptTokens = null,
            int? totalCompletionTokens = null,
            decimal? estimatedCostUsd = null,
            string? stepModelsJson = null,
            CancellationToken cancellationToken = default)
        {
            // C2 hardening: single retry for transient DB failures (connection reset,
            // pool exhaustion). Two attempts max — we don't want to delay the user response.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var result = await SaveCoreAsync(response, elapsedMs, sessionId, caseCode,
                        pipelineTraceId, generatedScript, errorMessage, sourceSuite, expectedScript,
                        llmCallCount, totalPromptTokens, totalCompletionTokens, estimatedCostUsd,
                        stepModelsJson, cancellationToken);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return result;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) when (attempt < 2)
                {
                    _logger.LogWarning(ex, "Trace save attempt {Attempt} failed; retrying once.", attempt);
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failures = Interlocked.Increment(ref _consecutiveFailures);
                    // Escalate log severity based on consecutive failure count so ops
                    // dashboards can trigger alerts on persistent DB issues.
                    if (failures >= 5)
                    {
                        _logger.LogCritical(TraceSaveFailedEvent, ex,
                            "Trace persistence CRITICAL: {Failures} consecutive failures. " +
                            "Forensic data is being lost. Investigate DB connectivity immediately.",
                            failures);
                    }
                    else
                    {
                        _logger.LogError(TraceSaveFailedEvent, ex,
                            "Failed to persist copilot trace history (consecutive failures: {Failures}).",
                            failures);
                    }
                    return null;
                }
            }
            return null;
        }

        private async Task<int> SaveCoreAsync(
            CopilotChatResponse response, long elapsedMs, int? sessionId,
            string? caseCode, string? pipelineTraceId, string? generatedScript,
            string? errorMessage, string? sourceSuite, string? expectedScript,
            int? llmCallCount, int? totalPromptTokens, int? totalCompletionTokens, decimal? estimatedCostUsd,
            string? stepModelsJson,
            CancellationToken cancellationToken)
        {
            var executionDetails = CreateAuditSafeExecutionDetails(response.ExecutionDetails, _options);
            var executionJson = JsonSerializer.Serialize(executionDetails, new JsonSerializerOptions
            {
                WriteIndented = false,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            // Forensic Timing Extraction for dedicated ExecutionTimes column
            // Cap at 100 steps to prevent unbounded JSON growth from pathological pipelines.
            var timings = executionDetails.Steps.Take(100).Select(s => new {
                s.Action,
                s.StartedAt,
                s.CompletedAt,
                s.ElapsedMs,
                s.Location
            }).ToList();
            var timingsJson = JsonSerializer.Serialize(timings);

            using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            // Persist the SessionId as-is — do NOT silently drop it on validation failure.
            // The previous behaviour ("session not found → save without reference") was hiding
            // legitimate references because the validation race-loses against the new session
            // being created in a sibling DbContext scope (assessment handler fires Task.Run with
            // its own scope; the trace write may land before that scope's Commit is visible).
            // If the SessionId is truly invalid the FK constraint will catch it on Save —
            // surfaced as an error rather than silently losing observability.
            int? validSessionId = sessionId;

            // Cap GeneratedScript to prevent unbounded nvarchar(MAX) growth from pathological
            // planner retries. 50K chars covers any reasonable SQL; larger payloads are truncated.
            var cappedScript = generatedScript is not null && generatedScript.Length > MaxGeneratedScriptLength
                ? generatedScript[..MaxGeneratedScriptLength] + "\n-- [truncated by trace store]"
                : generatedScript;
            // Apply the same cap to ExpectedScript so a curated GoldSql that happens to exceed
            // the column limit doesn't blow up the save with SqlException.
            var cappedExpected = expectedScript is not null && expectedScript.Length > MaxGeneratedScriptLength
                ? expectedScript[..MaxGeneratedScriptLength] + "\n-- [truncated by trace store]"
                : expectedScript;

            var historyRecord = new CopilotTraceHistory
            {
                Question = response.Question,
                Answer = response.Answer,
                CreatedAt = DateTime.UtcNow,
                ModelName = response.ModelName,
                TotalElapsedMs = elapsedMs,
                SessionId = validSessionId, // Use validated session ID or null
                CaseCode = NormalizeOptional(caseCode, 64),
                ExecutionPlan = executionJson,
                ExecutionTimes = timingsJson,
                PipelineTraceId = pipelineTraceId,
                GeneratedScript = cappedScript,
                ExpectedScript = cappedExpected,
                ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null
                    : (errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage),
                SourceSuite = NormalizeOptional(sourceSuite, 200),
                LlmCallCount = llmCallCount,
                TotalPromptTokens = totalPromptTokens,
                TotalCompletionTokens = totalCompletionTokens,
                EstimatedCostUsd = estimatedCostUsd,
                StepModelsJson = stepModelsJson,
            };

            context.CopilotTraceHistories.Add(historyRecord);
            await context.SaveChangesAsync(cancellationToken);

            return historyRecord.Id;
        }

        private static AdminCopilotExecutionDetails CreateAuditSafeExecutionDetails(
            AdminCopilotExecutionDetails source,
            CopilotTracePersistenceOptions options)
        {
            return new AdminCopilotExecutionDetails
            {
                DetectedIntent = source.DetectedIntent,
                RouteReason = Truncate(source.RouteReason, options),
                PlannerConfidence = source.PlannerConfidence,
                SearchQuery = Truncate(source.SearchQuery, options),
                Summary = Truncate(source.Summary, options),
                LastTechnicalData = Truncate(source.LastTechnicalData, options),
                ResultCount = source.ResultCount,
                Steps = source.Steps.Select(step => CloneStep(step, options)).ToList(),
                QueryPlan = source.QueryPlan,
                QueryPlans = source.QueryPlans,
                SubExecutions = TakeConfigured(source.SubExecutions, options.SubExecutionLimit)
                    .Select(execution => CloneExecution(execution, options))
                    .ToList(),
                ActionPlan = source.ActionPlan,
                TotalElapsedMs = source.TotalElapsedMs
            };
        }

        private static CopilotExecutionStep CloneStep(
            CopilotExecutionStep source,
            CopilotTracePersistenceOptions options)
        {
            return new CopilotExecutionStep
            {
                Layer = source.Layer,
                Action = Truncate(source.Action, options),
                Detail = Truncate(source.Detail, options),
                TechnicalData = Truncate(source.TechnicalData, options),
                Status = source.Status,
                ElapsedMs = source.ElapsedMs,
                StartedAt = source.StartedAt,
                CompletedAt = source.CompletedAt,
                Location = source.Location,
                Current = source.Current,
                SubSteps = source.SubSteps.Select(step => CloneStep(step, options)).ToList(),
                // Carry the per-step LLM telemetry through the audit-safe clone. These were previously
                // DROPPED here, which silently lost every per-call prompt/response (and decisions /
                // phase diagnostics) from the persisted ExecutionPlan — so a historical trace could not
                // show "what did we send / what came back". Restored so the trace covers all LLM calls.
                PromptTokens = source.PromptTokens,
                CompletionTokens = source.CompletionTokens,
                EstimatedCostUsd = source.EstimatedCostUsd,
                LlmModelUsed = source.LlmModelUsed,
                LlmCalls = source.LlmCalls?.Select(c => CloneLlmCall(c, options)).ToList(),
                Decisions = source.Decisions,
                PhaseDiagnostics = source.PhaseDiagnostics,
            };
        }

        /// <summary>Clone a per-call row for persistence. The PREVIEW fields obey the audit text limit;
        /// the FULL prompt/response (eval-run artifact, already bounded at capture by LlmTraceFullMaxChars)
        /// are EXEMPT so the end-to-end inspection text isn't re-clipped.</summary>
        private static TracedLlmCallRow CloneLlmCall(TracedLlmCallRow c, CopilotTracePersistenceOptions options)
        {
            return new TracedLlmCallRow
            {
                Stage = c.Stage,
                Provider = c.Provider,
                Model = c.Model,
                PromptTokens = c.PromptTokens,
                CompletionTokens = c.CompletionTokens,
                ElapsedMs = c.ElapsedMs,
                EstimatedCostUsd = c.EstimatedCostUsd,
                Success = c.Success,
                Error = c.Error,
                PromptPreview = c.PromptPreview is null ? null : Truncate(c.PromptPreview, options),
                ResponsePreview = c.ResponsePreview is null ? null : Truncate(c.ResponsePreview, options),
                PromptFullLength = c.PromptFullLength,
                ResponseFullLength = c.ResponseFullLength,
                RetryAttempt = c.RetryAttempt,
                Kind = c.Kind,
                PromptFull = c.PromptFull,
                ResponseFull = c.ResponseFull,
            };
        }

        private static AdminCopilotDynamicTicketQueryExecution CloneExecution(
            AdminCopilotDynamicTicketQueryExecution source,
            CopilotTracePersistenceOptions options,
            bool allowSyntheticSubExecution = true)
        {
            var subExecutionsSource = source.SubExecutions.Any()
                ? (IEnumerable<AdminCopilotDynamicTicketQueryExecution>)source.SubExecutions
                : allowSyntheticSubExecution && (!string.IsNullOrWhiteSpace(source.GeneratedSql) || source.IntentPlan != null)
                    ? new[] { CloneSelfAsSubExecution(source) }
                    : Array.Empty<AdminCopilotDynamicTicketQueryExecution>();

            return new AdminCopilotDynamicTicketQueryExecution
            {
                Plan = source.Plan,
                TotalCount = source.TotalCount,
                RequestedLimit = source.RequestedLimit,
                StatusBreakdown = source.StatusBreakdown,
                Rows = TakeConfigured(source.Rows, options.StructuredRowLimit).ToList(),
                StructuredColumns = source.StructuredColumns,
                StructuredRows = TakeConfigured(source.StructuredRows, options.StructuredRowLimit)
                    .Select(row => CloneRow(row, options))
                    .ToList(),
                GeneratedSql = Truncate(source.GeneratedSql, options),
                SqlParameters = source.SqlParameters.ToList(),
                Summary = Truncate(source.Summary, options),
                Answer = Truncate(source.Answer, options),
                ExecutionRoute = source.ExecutionRoute,
                EvidenceStrength = source.EvidenceStrength,
                ExecutionSteps = source.ExecutionSteps.Select(step => CloneStep(step, options)).ToList(),
                SubExecutions = TakeConfigured(subExecutionsSource, options.SubExecutionLimit)
                    .Select(execution => CloneExecution(execution, options, allowSyntheticSubExecution: false))
                    .ToList(),
                IntentPlan = source.IntentPlan,
                QueryModel = source.QueryModel,
                PlannerSource = source.PlannerSource,
                AnswerShapeGateMessage = Truncate(source.AnswerShapeGateMessage, options),
                IsLegacyCompatOnly = source.IsLegacyCompatOnly,
                RequiredShape = source.RequiredShape,
                EntityReference = source.EntityReference
            };
        }

        private static AdminCopilotDynamicTicketQueryExecution CloneSelfAsSubExecution(AdminCopilotDynamicTicketQueryExecution source)
        {
            return new AdminCopilotDynamicTicketQueryExecution
            {
                Plan = source.Plan,
                TotalCount = source.TotalCount,
                RequestedLimit = source.RequestedLimit,
                StatusBreakdown = source.StatusBreakdown,
                StructuredColumns = source.StructuredColumns,
                StructuredRows = source.StructuredRows,
                GeneratedSql = source.GeneratedSql,
                SqlParameters = source.SqlParameters,
                Summary = source.Summary,
                Answer = source.Answer,
                ExecutionRoute = source.ExecutionRoute,
                EvidenceStrength = source.EvidenceStrength,
                IntentPlan = source.IntentPlan,
                QueryModel = source.QueryModel,
                PlannerSource = source.PlannerSource,
                AnswerShapeGateMessage = source.AnswerShapeGateMessage,
                IsLegacyCompatOnly = source.IsLegacyCompatOnly,
                RequiredShape = source.RequiredShape,
                EntityReference = source.EntityReference
            };
        }

        private static AdminCopilotStructuredResultRow CloneRow(
            AdminCopilotStructuredResultRow source,
            CopilotTracePersistenceOptions options)
        {
            return new AdminCopilotStructuredResultRow
            {
                LinkUrl = source.LinkUrl,
                Values = source.Values.ToDictionary(
                    pair => pair.Key,
                    pair => Truncate(pair.Value, options),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static IEnumerable<T> TakeConfigured<T>(IEnumerable<T> source, int? limit)
        {
            return limit is > 0 ? source.Take(limit.Value) : source;
        }

        private static string Truncate(string? value, CopilotTracePersistenceOptions options)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? "";
            }

            if (options.TextLengthLimit is not > 0)
            {
                return value;
            }

            return value.Length <= options.TextLengthLimit.Value
                ? value
                : value[..options.TextLengthLimit.Value] + (options.TruncatedTextSuffix ?? string.Empty);
        }

        private static string? NormalizeOptional(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }
    }
}
