namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Data;
using ServiceOpsAI.Enums;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Copilot.Trace;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;
using AnalystAgent.Pipeline;

/// <summary>
/// Bridges the new copilot's <see cref="ITraceSink"/> to the host's
/// <see cref="CopilotTraceHistoryStore"/>. Every question + SQL + result + timing lands in
/// the existing CopilotTraceHistories table so the existing investigation-tree UI picks them up
/// alongside the legacy copilot's traces, complete with the per-stage step panel populated.
///
/// <para><b>What goes where</b>:
///   • <c>Answer</c> = the full LLM-generated reply (with citations + data table) shown in chat,
///     not the synthesized "Returned N row(s)" placeholder. Falls back to the placeholder only
///     when the orchestrator didn't pass a reply (defensive — should always be present).
///   • <c>ModelName</c> = the actual planner LLM model from the Copilot workload provider
///     (e.g. "qwen2.5:3b"), NOT the pipeline name "analyst-agent". Read fresh on each
///     write so settings changes take effect without a restart.
///   • <c>SourceSuite</c> = the question-suite filename when the request came from the eval
///     runner; the generic "analyst-agent" tag otherwise.
///   • <c>CaseCode</c> = the suite scenario code (e.g. SAC-L01-03) when from the runner; null
///     for chat-driven traces.</para>
/// THIS IS THE ONLY FILE that depends on the host's trace store and chat-response types.
/// </summary>
/// <summary>Per-step LLM metric accumulator used by <see cref="HostTraceSink.MapStep"/> to
/// stamp tokens / cost / resolved-model on each <see cref="CopilotExecutionStep"/>.</summary>
internal sealed record StepLlmMetric(int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd, string? ModelUsed);

internal sealed class HostTraceSink : ITraceSink
{
    private const string SourcePrefix = "analyst-agent";

    private readonly CopilotTraceHistoryStore _store;
    private readonly IAiProviderFactory _providerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceOpsAI.Services.AI.Cost.ICostCalculator _costCalculator;
    private readonly ITraceWriteQueue _writeQueue;
    private readonly ILogger<HostTraceSink> _logger;

    public HostTraceSink(
        CopilotTraceHistoryStore store,
        IAiProviderFactory providerFactory,
        IServiceScopeFactory scopeFactory,
        ServiceOpsAI.Services.AI.Cost.ICostCalculator costCalculator,
        ITraceWriteQueue writeQueue,
        ILogger<HostTraceSink> logger)
    {
        _store = store;
        _providerFactory = providerFactory;
        _scopeFactory = scopeFactory;
        _costCalculator = costCalculator;
        _writeQueue = writeQueue;
        _logger = logger;
    }

    public async Task<int?> RecordAsync(
        string question,
        string? sql,
        int? rowCount,
        long elapsedMs,
        string? error,
        string? reply = null,
        string? caseCode = null,
        string? sourceSuite = null,
        int? sessionId = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        IReadOnlyList<PipelineStep>? steps = null,
        string? expectedSql = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Prefer the full LLM-generated reply (with the markdown table + citations) — that's
            // what the user sees in chat and what they want when they View an assessment row.
            // Fall back to the synthesized placeholder only when the orchestrator didn't supply
            // one (e.g. early refusal paths that don't run the explainer).
            var answer = !string.IsNullOrWhiteSpace(reply)
                ? reply
                : !string.IsNullOrEmpty(error)
                    ? $"Failed: {error}"
                    : (rowCount.HasValue ? $"Returned {rowCount} row(s)." : "OK.");

            // Resolve the actual planner LLM model name fresh per call — admins can swap models
            // in settings without restarting; the ProviderFactory reflects the current selection.
            // Defensive try/catch: if the factory throws, fall back to the pipeline tag rather
            // than fail the trace write.
            string modelName;
            try
            {
                modelName = _providerFactory.GetProviderForWorkload(AiWorkloadType.Copilot).ModelName ?? SourcePrefix;
            }
            catch
            {
                modelName = SourcePrefix;
            }

            var executionDetails = new AdminCopilotExecutionDetails
            {
                Summary = $"analyst-agent pipeline ({steps?.Count ?? 0} stages, {elapsedMs}ms)",
                TotalElapsedMs = elapsedMs,
                DetectedIntent = ResolveDetectedIntent(sql, error),
                RouteReason = string.IsNullOrWhiteSpace(error) ? "ok" : ResolveFailedStage(steps) ?? "error",
                ResultCount = rowCount,
                LastTechnicalData = sql,
            };

            // Per-step LLM metrics — match the scope's LlmCallRecord entries back to the
            // pipeline-step list by stage name, compute cost via ICostCalculator, and pass the
            // resulting map into MapStep so each step row gets stamped with its own tokens /
            // cost / model. Stage names are normalised (strip retry suffix) for the lookup.
            // ALSO build a per-stage list of TracedLlmCallRow so each step carries one row per
            // call — prompt/response previews live here so the investigation page renders
            // "what we sent / what came back" without log lookups.
            var stepMetrics = new Dictionary<string, StepLlmMetric>(StringComparer.OrdinalIgnoreCase);
            var stepCalls = new Dictionary<string, List<TracedLlmCallRow>>(StringComparer.OrdinalIgnoreCase);
            var scopeForSteps = AnalystAgent.Abstractions.LlmCallScope.Current;
            if (scopeForSteps is not null && scopeForSteps.CallCount > 0)
            {
                foreach (var r in scopeForSteps.Records)
                {
                    var promptT = r.Usage?.Prompt ?? 0;
                    var completionT = r.Usage?.Completion ?? 0;
                    var est = r.Usage is not null
                        ? await _costCalculator.EstimateAsync(r.Provider, r.Model, promptT, completionT, cancellationToken)
                        : new ServiceOpsAI.Services.AI.Cost.CostEstimate(0m, null);
                    if (r.Usage is not null)
                    {
                        // Some stages fire multiple LLM calls (Decomposer always; Planner under retry).
                        // Accumulate across calls so the rendered row shows the stage's TOTAL cost.
                        if (stepMetrics.TryGetValue(r.Stage, out var existingMetric))
                        {
                            stepMetrics[r.Stage] = existingMetric with
                            {
                                PromptTokens = existingMetric.PromptTokens + promptT,
                                CompletionTokens = existingMetric.CompletionTokens + completionT,
                                EstimatedCostUsd = existingMetric.EstimatedCostUsd + est.CostUsd,
                            };
                        }
                        else
                        {
                            stepMetrics[r.Stage] = new StepLlmMetric(
                                PromptTokens: promptT,
                                CompletionTokens: completionT,
                                EstimatedCostUsd: est.CostUsd,
                                ModelUsed: r.Model);
                        }
                    }
                    // Always record the row regardless of usage availability — even a failed
                    // call (no Usage) should appear in the investigation list so operators see
                    // "this call failed with error X".
                    if (!stepCalls.TryGetValue(r.Stage, out var rowList))
                    {
                        rowList = new List<TracedLlmCallRow>();
                        stepCalls[r.Stage] = rowList;
                    }
                    rowList.Add(new TracedLlmCallRow
                    {
                        Stage = r.Stage,
                        Provider = r.Provider,
                        Model = r.Model,
                        PromptTokens = r.Usage is null ? null : promptT,
                        CompletionTokens = r.Usage is null ? null : completionT,
                        ElapsedMs = r.ElapsedMs,
                        EstimatedCostUsd = r.Usage is null ? null : est.CostUsd,
                        Success = r.Success,
                        Error = r.Error,
                        PromptPreview = r.PromptPreview,
                        ResponsePreview = r.ResponsePreview,
                        PromptFullLength = r.PromptFullLength,
                        ResponseFullLength = r.ResponseFullLength,
                        RetryAttempt = r.RetryAttempt,
                        Kind = r.Kind,
                        PromptFull = r.PromptFull,
                        ResponseFull = r.ResponseFull,
                    });
                }
            }

            if (steps is { Count: > 0 })
            {
                foreach (var s in steps)
                    executionDetails.Steps.Add(MapStep(s, stepMetrics, stepCalls));
            }

            // COVERAGE GUARANTEE: any recorded call whose stage didn't match a named pipeline step
            // (e.g. IntentClassifier when there is no IntentClassifier step, or bge-m3 embedding calls
            // that fire inside SchemaLink/ScopeGate) would otherwise be ORPHANED by TryMatchCalls and
            // dropped from the trace. Collect every call-group not attached above into a synthetic step
            // so the persisted trace covers ALL model calls — the whole point of this capture.
            if (stepCalls.Count > 0)
            {
                var attached = new HashSet<List<TracedLlmCallRow>>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
                void CollectAttached(CopilotExecutionStep st)
                {
                    if (st.LlmCalls is { } lc) attached.Add(lc);
                    foreach (var ss in st.SubSteps) CollectAttached(ss);
                }
                foreach (var st in executionDetails.Steps) CollectAttached(st);

                var orphans = stepCalls
                    .Where(kv => kv.Value.Count > 0 && !attached.Contains(kv.Value))
                    .SelectMany(kv => kv.Value)
                    .ToList();
                if (orphans.Count > 0)
                {
                    executionDetails.Steps.Add(new CopilotExecutionStep
                    {
                        Layer = CopilotExecutionLayer.Context,
                        Action = "Model calls (unattached)",
                        Detail = $"{orphans.Count} model call(s) not tied to a named step (classifier / embeddings).",
                        Status = CopilotStepStatus.Ok,
                        StartedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow,
                        Location = "AnalystAgent",
                        LlmCalls = orphans,
                    });
                }
            }

            var response = new CopilotChatResponse
            {
                Question = question,
                Answer = answer,
                ModelName = modelName,
                ExecutionDetails = executionDetails,
                Notes = sql ?? string.Empty,
            };

            if (rows is { Count: > 0 })
            {
                response.StructuredColumns = rows[0].Keys.ToList();
                response.StructuredRows = rows.Select(row =>
                {
                    var rr = new AdminCopilotStructuredResultRow();
                    foreach (var (k, v) in row)
                        rr.Values[k] = v?.ToString() ?? string.Empty;
                    return rr;
                }).ToList();

                executionDetails.SubExecutions.Add(new AdminCopilotDynamicTicketQueryExecution
                {
                    TotalCount = rowCount ?? rows.Count,
                    StructuredColumns = response.StructuredColumns,
                    StructuredRows = response.StructuredRows,
                    GeneratedSql = sql,
                    Summary = rowCount.HasValue ? $"{rowCount} row(s)" : $"{rows.Count} row(s)",
                    Answer = answer,
                });
            }

            // SourceSuite carries the suite filename verbatim when running from the eval lab so
            // the assessment grid's Suite column has something specific to show. For chat-driven
            // traces the generic tag is fine.
            var suiteTag = string.IsNullOrEmpty(sourceSuite) ? SourcePrefix : sourceSuite;

            // Per-question LLM cost roll-up. The orchestrator opened a LlmCallScope at the top
            // of AskAsync; every ILlmClient call inside appended a record. Read totals here,
            // resolve per-record costs from ModelPricing, and pass aggregates to the trace store.
            var scope = AnalystAgent.Abstractions.LlmCallScope.Current;
            int? llmCallCount = null;
            int? totalPromptTokens = null;
            int? totalCompletionTokens = null;
            decimal? estimatedCostUsd = null;
            if (scope is not null && scope.CallCount > 0)
            {
                llmCallCount = scope.CallCount;
                totalPromptTokens = scope.TotalPromptTokens;
                totalCompletionTokens = scope.TotalCompletionTokens;
                // Dedupe by (Provider, Model) before calling ICostCalculator — a question
                // that fires 5 calls all on the same model used to do 5 cost lookups; now it
                // does 1 (the calculator's in-memory cache hides repeats anyway, but the dedup
                // also removes per-call Task overhead and keeps the loop straight). Sum the
                // tokens per pair first, then a single Estimate call per pair.
                var byModel = new Dictionary<(string Provider, string Model), (int Prompt, int Completion)>();
                foreach (var r in scope.Records)
                {
                    if (r.Usage is null) continue;
                    var key = (r.Provider ?? "unknown", r.Model ?? string.Empty);
                    byModel.TryGetValue(key, out var acc);
                    byModel[key] = (acc.Prompt + r.Usage.Prompt, acc.Completion + r.Usage.Completion);
                }
                decimal total = 0m;
                foreach (var ((prov, mdl), (pTok, cTok)) in byModel)
                {
                    var est = await _costCalculator.EstimateAsync(prov, mdl, pTok, cTok, cancellationToken);
                    total += est.CostUsd;
                }
                estimatedCostUsd = total;
                // Stamp the aggregates on the trace's ExecutionDetails Summary too so the
                // existing trace-grid table (which reads ExecutionPlan JSON) can show cost
                // without an EF migration. Cheap belt-and-braces.
                executionDetails.Summary =
                    $"{executionDetails.Summary} | {llmCallCount} LLM calls, " +
                    $"{totalPromptTokens}+{totalCompletionTokens} tokens, ~${estimatedCostUsd:F4}";

                // Post-question cost-gate notice. The pre-call gate enforces the TOKEN cap on
                // the hot path; the COST cap is checked here because per-call USD requires a
                // DB pricing lookup we don't want on the call path. When the question lands
                // over budget we don't unwind (the user already has the answer), but we tag
                // the row so admins can audit + tune the cap.
                try
                {
                    using var optScope = _scopeFactory.CreateScope();
                    var opts = optScope.ServiceProvider
                        .GetService<Microsoft.Extensions.Options.IOptionsMonitor<AnalystAgent.Configuration.AnalystOptions>>()
                        ?.CurrentValue;
                    if (opts is not null && opts.MaxCostPerQuestionUsd > 0m && estimatedCostUsd > opts.MaxCostPerQuestionUsd)
                    {
                        _logger.LogWarning(
                            "[HostTraceSink] Question exceeded cost budget: ${ActualCost:F4} > ${Cap:F4}. Trace #{TraceCase} '{Q}'.",
                            estimatedCostUsd, opts.MaxCostPerQuestionUsd, caseCode ?? "—",
                            question.Length > 80 ? question[..80] + "…" : question);
                        executionDetails.Summary += $" | OVER BUDGET (cap: ${opts.MaxCostPerQuestionUsd:F4})";
                    }
                }
                catch { /* config read shouldn't break trace save */ }
            }

            // Per-step model JSON — one entry per pipeline stage that actually fired an LLM
            // call. The pipeline has ~6 stages (Decomposer, SpecExtractor, Compiler, Executor,
            // Validator, Explainer); each may use a DIFFERENT model (cloud planner +
            // local-LLM compiler, etc.). Stored as a separate JSON column so the trace grid
            // can display "which model did what" without parsing the full ExecutionPlan blob.
            string? stepModelsJson = null;
            if (stepMetrics.Count > 0)
            {
                var stepModels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (stage, metric) in stepMetrics)
                    stepModels[stage] = metric.ModelUsed;
                stepModelsJson = System.Text.Json.JsonSerializer.Serialize(stepModels);
            }

            // Persist the trace row SYNCHRONOUSLY so the caller gets the auto-assigned Id back —
            // the chat UI uses this id to wire the "Open Investigation" button on the result
            // bubble (Copilot.cshtml line 1244 reads m.id from the response; null id ⇒ button
            // suppressed, the user-visible regression we fixed here). DB write is ~10-50ms,
            // negligible vs the 1-5s LLM call that produced the answer.
            //
            // The EMBEDDING work (question vector + model name) stays async — that's the part
            // worth the 50-200ms ollama round-trip the original "fire and forget" was avoiding.
            int? traceId = null;
            try
            {
                traceId = await _store.SaveAsync(
                    response, elapsedMs,
                    sessionId: sessionId,
                    caseCode: caseCode,
                    pipelineTraceId: null,
                    generatedScript: sql,
                    errorMessage: error,
                    sourceSuite: suiteTag,
                    expectedScript: expectedSql,
                    llmCallCount: llmCallCount,
                    totalPromptTokens: totalPromptTokens,
                    totalCompletionTokens: totalCompletionTokens,
                    estimatedCostUsd: estimatedCostUsd,
                    stepModelsJson: stepModelsJson,
                    cancellationToken: cancellationToken);
            }
            catch (Exception saveEx)
            {
                _logger.LogWarning(saveEx, "[AnalystAgent] Trace row save failed; question still answered.");
            }

            // Fire-and-forget the embedding write so the user-visible response isn't blocked
            // on ollama. The trace row already exists; the embedding column gets backfilled.
            if (traceId is int id && string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(sql))
            {
                _ = Task.Run(() => TryEmbedAndPersistAsync(id, question, CancellationToken.None));
            }
            return traceId;
        }
        catch (Exception ex)
        {
            // Trace persistence is best-effort — never let it break the user's question.
            _logger.LogWarning(ex, "[AnalystAgent] Trace persistence failed; question still answered.");
            return null;
        }
    }

    private static string ResolveDetectedIntent(string? sql, string? error)
    {
        if (!string.IsNullOrWhiteSpace(sql) && sql.TrimStart().StartsWith("-- Tool", StringComparison.OrdinalIgnoreCase))
            return CopilotIntentKind.ExternalToolQuery.ToString();
        if (!string.IsNullOrWhiteSpace(sql))
            return CopilotIntentKind.DataQuery.ToString();
        if (!string.IsNullOrWhiteSpace(error))
            return CopilotIntentKind.Unsupported.ToString();
        return CopilotIntentKind.GeneralChat.ToString();
    }

    private static string? ResolveFailedStage(IReadOnlyList<PipelineStep>? steps)
    {
        if (steps is null) return null;
        var failed = steps.FirstOrDefault(s => string.Equals(s.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase))?.Stage;
        if (failed is null) return null;
        // Map step tags to canonical RouteReason categories so eval cases can target
        // semantic refusal classes (e.g. "preflight-refused") without coupling to the
        // specific guard that fired. The step itself remains tagged with the guard name
        // for trace inspection; only the rolled-up RouteReason is normalized.
        return failed switch
        {
            StageNames.StepWriteIntentGuard   => StageNames.PreflightRefused,
            StageNames.StepOperationalGuard   => StageNames.PreflightRefused,
            StageNames.StepAccessPolicy       => StageNames.PreflightRefused,
            StageNames.StepOutOfScopeGuard    => StageNames.PreflightRefused,
            _ => failed
        };
    }

    /// <summary>
    /// Fire-and-forget: embed the question and write the vector + model name into the existing
    /// <c>CopilotTraceHistory</c> row. Runs in the background so the response isn't blocked on
    /// the embedder. Logged at Debug on success, Warning on failure — never throws to the caller.
    /// </summary>
    private async Task TryEmbedAndPersistAsync(int traceId, string question, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var embedder = scope.ServiceProvider.GetRequiredService<ITextEmbedder>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            var vec = await embedder.EmbedAsync(question, cancellationToken);
            if (vec.Length == 0)
            {
                _logger.LogDebug("[AnalystAgent] Embedder returned empty vector for trace {TraceId} — skipping persistence.", traceId);
                return;
            }
            await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await ctx.Set<CopilotTraceHistory>().FindAsync(new object[] { traceId }, cancellationToken);
            if (row is null) return;
            row.QuestionEmbeddingJson = System.Text.Json.JsonSerializer.Serialize(vec);
            row.EmbeddingModelName = embedder.ModelName ?? "";
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnalystAgent] Trace embedding persistence failed for {TraceId}.", traceId);
        }
    }

    /// <summary>
    /// Map our internal <see cref="PipelineStep"/> to the host's <see cref="CopilotExecutionStep"/>
    /// shape so the existing investigation-tree UI renders it without changing the UI side.
    /// Recursively maps any <see cref="PipelineStep.SubSteps"/> so the host UI's expandable
    /// sub-step rows populate (each with its own kind / prompt / response / ms).
    /// </summary>
    private static CopilotExecutionStep MapStep(
        PipelineStep s,
        IReadOnlyDictionary<string, StepLlmMetric>? metrics = null,
        IReadOnlyDictionary<string, List<TracedLlmCallRow>>? calls = null)
    {
        var mapped = new CopilotExecutionStep
        {
            Layer = MapLayer(s.Stage),
            Action = s.Stage,
            Detail = s.Detail ?? "",
            Status = MapStatus(s.Status),
            ElapsedMs = s.ElapsedMs,
            StartedAt = s.StartedAt,
            CompletedAt = s.StartedAt.AddMilliseconds(s.ElapsedMs),
            TechnicalData = s.TechnicalData,
            Location = "AnalystAgent",
        };
        // Stamp the step row with the matching LLM-call metric (cost + tokens + resolved
        // model). LlmCallStageHint values bridge "step name" → "metric key", so the lookup
        // works without the orchestrator threading metrics through every Step() call.
        if (metrics is not null && TryMatchMetric(s.Stage, metrics, out var metric))
        {
            mapped.PromptTokens = metric.PromptTokens;
            mapped.CompletionTokens = metric.CompletionTokens;
            mapped.EstimatedCostUsd = metric.EstimatedCostUsd;
            mapped.LlmModelUsed = metric.ModelUsed;
        }
        // Attach the per-call rows (prompt/response previews + tokens + cost per call). Same
        // matching logic as metrics so SpecExtractor (retry 1) joins the right rows.
        if (calls is not null && TryMatchCalls(s.Stage, calls, out var rowList) && rowList.Count > 0)
        {
            mapped.LlmCalls = rowList;
        }
        if (s.SubSteps is { Count: > 0 })
        {
            foreach (var sub in s.SubSteps)
                mapped.SubSteps.Add(MapStep(sub, metrics, calls));
        }
        return mapped;
    }

    /// <summary>Identical normalisation to <see cref="TryMatchMetric"/> but for the calls map.</summary>
    private static bool TryMatchCalls(
        string stage,
        IReadOnlyDictionary<string, List<TracedLlmCallRow>> calls,
        out List<TracedLlmCallRow> rows)
    {
        rows = new List<TracedLlmCallRow>();
        if (string.IsNullOrEmpty(stage)) return false;
        var key = stage;
        var parenIdx = key.IndexOf(" (", StringComparison.Ordinal);
        if (parenIdx > 0) key = key.Substring(0, parenIdx);
        if (calls.TryGetValue(key, out var list)) { rows = list; return true; }
        if (calls.TryGetValue("Step" + key, out list)) { rows = list; return true; }
        return false;
    }

    /// <summary>Match a pipeline-step stage name against the LlmCallScope's recorded stage
    /// hints. Stage strings include suffixes ("Compiler (retry 1)") and prefixes
    /// ("StepSpecExtractor") that the hint values don't; this method normalises both sides
    /// to find a hit.</summary>
    private static bool TryMatchMetric(
        string stage,
        IReadOnlyDictionary<string, StepLlmMetric> metrics,
        out StepLlmMetric metric)
    {
        metric = default!;
        if (string.IsNullOrEmpty(stage)) return false;
        var key = stage;
        var retryIdx = key.IndexOf(" (retry", StringComparison.Ordinal);
        if (retryIdx > 0) key = key.Substring(0, retryIdx);
        // Direct hit on canonical stage name.
        if (metrics.TryGetValue(key, out metric!)) return true;
        // Match by canonical short hint substring — "Spec extractor" step ↔ "SpecExtractor" hint.
        foreach (var (hint, m) in metrics)
        {
            if (key.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metric = m;
                return true;
            }
        }
        return false;
    }

    private static CopilotExecutionLayer MapLayer(string stage)
    {
        // Normalize the stage label so retry suffixes ("Compiler (retry 1)") and the
        // sub-question index ("Sub-question 2") collapse to their canonical Step* constant.
        // Without this, every retried step fell through to the catch-all Executor bucket and
        // the investigation UI's "Data Planning" / "Data Execution" panels stayed empty even
        // when the orchestrator did real planning + execution work — the user's complaint that
        // the investigation page no longer reflects what the pipeline actually did.
        var key = stage ?? "";
        var retryIdx = key.IndexOf(" (retry", StringComparison.Ordinal);
        if (retryIdx > 0) key = key.Substring(0, retryIdx);
        if (key.StartsWith("Sub-question ", StringComparison.Ordinal)) key = "Sub-question";

        return key switch
        {
            // Context (pre-processing, gating, schema lookup).
            StageNames.StepPreflight        => CopilotExecutionLayer.Context,
            StageNames.StepOperationalGuard => CopilotExecutionLayer.Context,
            "LLM understanding"             => CopilotExecutionLayer.Context,     // B6: Phase 1 semantic understanding
            "SemanticUnderstanding"         => CopilotExecutionLayer.Context,     // B6: alternate stage name

            // Router (intent decisions, branching, retries, sub-question fan-out).
            StageNames.StepIntentRoute      => CopilotExecutionLayer.Router,
            StageNames.StepRetry            => CopilotExecutionLayer.Router,
            StageNames.StepDecomposer       => CopilotExecutionLayer.Router,
            StageNames.StepSubQuestion      => CopilotExecutionLayer.Router,

            // DataPlanning (LLM builds the plan + safety/intent gates that operate on the plan).
            StageNames.StepRetriever        => CopilotExecutionLayer.DataPlanning,
            StageNames.StepPlanner          => CopilotExecutionLayer.DataPlanning,
            StageNames.StepSpecExtractor    => CopilotExecutionLayer.DataPlanning,
            StageNames.StepSpecRefine       => CopilotExecutionLayer.DataPlanning,
            StageNames.StepVerifiedQuery    => CopilotExecutionLayer.DataPlanning,
            StageNames.StepAccessPolicy     => CopilotExecutionLayer.DataPlanning,
            StageNames.StepCompiler         => CopilotExecutionLayer.DataPlanning,
            StageNames.StepValidator        => CopilotExecutionLayer.DataPlanning,
            StageNames.StepSqlIntentGuard   => CopilotExecutionLayer.DataPlanning,

            // DataExecution (running the actual query / vector search / external tool).
            StageNames.StepExecutor         => CopilotExecutionLayer.DataExecution,
            StageNames.StepSemanticSearch   => CopilotExecutionLayer.DataExecution,
            StageNames.StepToolDispatch     => CopilotExecutionLayer.DataExecution,
            StageNames.StepRowShapeSanity   => CopilotExecutionLayer.DataExecution,

            // Complete (final-answer producers: fast-paths + explainer).
            StageNames.StepConversational   => CopilotExecutionLayer.Complete,
            StageNames.StepKnowledgeMatch   => CopilotExecutionLayer.Complete,
            StageNames.StepExplainer        => CopilotExecutionLayer.Complete,

            _                               => CopilotExecutionLayer.Executor,
        };
    }

    private static CopilotStepStatus MapStatus(string status) => status switch
    {
        StageNames.StatusOk      => CopilotStepStatus.Ok,
        StageNames.StatusSkipped => CopilotStepStatus.Skip,
        StageNames.StatusFailed  => CopilotStepStatus.Error,
        _                    => CopilotStepStatus.Ok,
    };
}
