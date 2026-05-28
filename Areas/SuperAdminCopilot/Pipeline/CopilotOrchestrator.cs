namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Internal;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;

/// <summary>
/// Entry-point orchestrator. AskAsync routes through preflight guards → fast paths (conversational,
/// knowledge, semantic, tool, verified-query) → IntentClassifier → ScopeConfidenceGate → Decomposer,
/// then dispatches to RunSingleAsync or RunDecomposedAsync.
/// </summary>
// Class split across 3 partial files:
//   - CopilotOrchestrator.cs           : AskAsync, fields, ctor, shared helpers (TimedAsync, PersistAsync)
//   - CopilotOrchestrator.RunSingle.cs : single-question execution loop + spec retry helpers
//   - CopilotOrchestrator.Decomposed.cs: multi-sub-question fan-out (parallel + sequential)
internal sealed partial class CopilotOrchestrator : ISuperAdminCopilot
{
    private readonly Stages.IConversationalHandler _conversational;
    private readonly Stages.IIntentClassifier _intentClassifier;
    private readonly Retrieval.ISchemaSemanticRetriever _schemaRetriever;
    private readonly Pipeline.EntityResolution.IFuzzyEntityResolver _entityResolver;
    private readonly Pipeline.Stages.Decomposed.IStructuralCueParser _cueParser;
    private readonly Stages.IScopeConfidenceGate _scopeConfidenceGate;
    private readonly Stages.IKnowledgeMatchHandler _knowledge;
    private readonly Stages.IWriteIntentGuard _writeIntentGuard;
    private readonly Stages.ICoverageChecker _coverageChecker;
    private readonly Stages.ISemanticSearchHandler _semanticSearch;
    private readonly Stages.IToolHandler _toolHandler;
    private readonly Stages.IDecomposer _regexDecomposer;
    private readonly Stages.ILlmDecomposer _llmDecomposer;
    private readonly Stages.ISpecExtractor _specExtractor;
    private readonly Stages.ILlmDirectSqlEmitter _directSqlEmitter;
    private readonly Stages.IQuestionShapeClassifier _shapeClassifier;
    private readonly Stages.IMetadataHandler _metadataHandler;
    private readonly Stages.IConversationSpecMemory _specMemory;
    private readonly Stages.IRefinementDetector _refinementDetector;
    private readonly Stages.IChartTypeSuggester _chartSuggester;
    private readonly Stages.ISpecEnricher _specEnricher;
    private readonly Retrieval.IVerifiedQueryMatcher _verifiedQueryMatcher;
    private readonly ICompiler _compiler;
    private readonly IValidator _validator;
    private readonly Stages.ISqlIntentGuard _sqlIntentGuard;
    private readonly Stages.IQuerySpecAccessPolicyValidator _accessPolicy;
    private readonly IExecutor _executor;
    private readonly IExplainer _explainer;
    private readonly ITraceSink _traceSink;
    private readonly IOperationalGuard _operationalGuard;
    private readonly IRetryBudget _retryBudget;
    private readonly IPipelineStepProgressSink _progress;
    private readonly IOptions<CopilotOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<CopilotOrchestrator> _logger;

    public CopilotOrchestrator(
        Stages.IConversationalHandler conversational,
        Stages.IKnowledgeMatchHandler knowledge,
        Stages.IWriteIntentGuard writeIntentGuard,
        Stages.IIntentClassifier intentClassifier,
        Retrieval.ISchemaSemanticRetriever schemaRetriever,
        Pipeline.EntityResolution.IFuzzyEntityResolver entityResolver,
        Pipeline.Stages.Decomposed.IStructuralCueParser cueParser,
        Stages.IScopeConfidenceGate scopeConfidenceGate,
        Stages.ICoverageChecker coverageChecker,
        Stages.ISemanticSearchHandler semanticSearch,
        Stages.IToolHandler toolHandler,
        Stages.IDecomposer regexDecomposer,
        Stages.ILlmDecomposer llmDecomposer,
        Stages.ISpecExtractor specExtractor,
        Stages.ILlmDirectSqlEmitter directSqlEmitter,
        Stages.IQuestionShapeClassifier shapeClassifier,
        Stages.IMetadataHandler metadataHandler,
        Stages.IConversationSpecMemory specMemory,
        Stages.IRefinementDetector refinementDetector,
        Stages.IChartTypeSuggester chartSuggester,
        Stages.ISpecEnricher specEnricher,
        Retrieval.IVerifiedQueryMatcher verifiedQueryMatcher,
        ICompiler compiler,
        IValidator validator,
        Stages.ISqlIntentGuard sqlIntentGuard,
        Stages.IQuerySpecAccessPolicyValidator accessPolicy,
        IExecutor executor,
        IExplainer explainer,
        ITraceSink traceSink,
        IOperationalGuard operationalGuard,
        IRetryBudget retryBudget,
        IPipelineStepProgressSink progress,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<CopilotOrchestrator> logger)
    {
        _conversational = conversational;
        _knowledge = knowledge;
        _writeIntentGuard = writeIntentGuard;
        _intentClassifier = intentClassifier;
        _schemaRetriever = schemaRetriever;
        _entityResolver = entityResolver;
        _cueParser = cueParser;
        _scopeConfidenceGate = scopeConfidenceGate;
        _coverageChecker = coverageChecker;
        _semanticSearch = semanticSearch;
        _toolHandler = toolHandler;
        _regexDecomposer = regexDecomposer;
        _llmDecomposer = llmDecomposer;
        _specExtractor = specExtractor;
        _directSqlEmitter = directSqlEmitter;
        _shapeClassifier = shapeClassifier;
        _metadataHandler = metadataHandler;
        _specMemory = specMemory;
        _refinementDetector = refinementDetector;
        _chartSuggester = chartSuggester;
        _specEnricher = specEnricher;
        _verifiedQueryMatcher = verifiedQueryMatcher;
        _compiler = compiler;
        _validator = validator;
        _sqlIntentGuard = sqlIntentGuard;
        _accessPolicy = accessPolicy;
        _executor = executor;
        _explainer = explainer;
        _traceSink = traceSink;
        _operationalGuard = operationalGuard;
        _retryBudget = retryBudget;
        _progress = progress;
        _options = options;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    // Hot-reloadable text catalog; always read CurrentValue at the call site.
    private CopilotTextCatalog Text => _textCatalog.CurrentValue;

    // Prefer the SignalR connection id (known on tab-open) over the conversation-id group (only joined after first reply).
    private static Abstractions.ProgressTarget TargetFor(CopilotRequest r) =>
        new(r.SignalRConnectionId, r.ConversationId);

    public async Task<CopilotResponse> AskAsync(CopilotRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = Internal.CopilotActivitySource.Instance.StartActivity("Copilot.AskAsync");
        activity?.SetTag("copilot.conversation_id", request.ConversationId);
        activity?.SetTag("copilot.question_length", (request.Question ?? "").Length);

        // Per-question scopes: retry budget reset, LLM metrics, embedding cache (all AsyncLocal so decomposer fan-out propagates).
        _retryBudget.Reset();
        using var llmScope = Abstractions.LlmCallScope.Begin();
        using var embedScope = Abstractions.QuestionEmbeddingScope.Begin();

        var totalSw = Stopwatch.StartNew();
        var progressTarget = new Abstractions.ProgressTarget(request.SignalRConnectionId, request.ConversationId);
        var steps = new BroadcastingStepList(_progress, progressTarget);

        // Canonical question for downstream stages; raw text stays on request for trace/UI display.
        var question = Internal.QuestionTextNormalizer.Normalize(request.Question);
        if (string.IsNullOrEmpty(question))
            return new CopilotResponse(Reply: Text.EmptyQuestion, Error: "empty question");

        // ── Operational guard ────────────────────────────────────────────
        var guardRefusal = _operationalGuard.CheckOrRefuse(request.ConversationId);
        if (guardRefusal is not null)
        {
            steps.RecordOperationalGuardRefusal(question, guardRefusal);
            return (await PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.OperationalGuardRefusalTemplate, guardRefusal),
                error: guardRefusal, cancellationToken: cancellationToken))
                with { Trace = StageNames.PreflightRefused, Provenance = "refusal", Confidence = 1.0 };
        }
        steps.RecordOperationalGuardPassed(question);

        // ── Write-intent preflight ───────────────────────────────────────
        // Deterministic refuse for delete/drop/insert/update/alter/... before any LLM call; multi-language (EN+AR).
        var writeIntent = _writeIntentGuard.Check(question);
        if (writeIntent is not null)
        {
            steps.RecordWriteIntentRefusal(question, writeIntent);
            return (await PersistAsync(request, totalSw, steps,
                reply: writeIntent.Reason,
                error: writeIntent.Reason, cancellationToken: cancellationToken))
                with { Provenance = "refusal", Confidence = 1.0, Trace = StageNames.PreflightRefused };
        }
        steps.RecordWriteIntentPassed(question);

        // ── Conversational fast-path (greetings, "what can you do", thanks) ──
        var convReply = _conversational.TryHandle(question);
        if (convReply is not null)
        {
            steps.RecordConversational(question, convReply);
            return (await PersistAsync(request, totalSw, steps,
                reply: convReply.Reply, cancellationToken: cancellationToken)) with { Provenance = "conversational", Confidence = 1.0 };
        }

        // ── Knowledge-match fast-path (concept questions like "what is a ticket") ──
        var kmReply = _knowledge.TryHandle(question);
        if (kmReply is not null)
        {
            steps.RecordKnowledgeMatch(question, kmReply);
            return (await PersistAsync(request, totalSw, steps,
                reply: kmReply.Reply, cancellationToken: cancellationToken)) with { Provenance = "knowledge", Confidence = 1.0 };
        }

        // ── Semantic search fast-path ("tickets similar to TCK-X" / "tickets about login") ──
        var (semResult, semElapsed) = await TimedAsync(request, StageNames.StepSemanticSearch,
            ct => _semanticSearch.TryHandleAsync(question, ct), cancellationToken);
        if (semResult is not null)
        {
            steps.RecordSemanticSearchHit(question, semResult, semElapsed);
            var semExplain = await _explainer.ExplainAsync(question, semResult.Result,
                new CompiledSql(semResult.Sql, new Dictionary<string, object?>()), cancellationToken);
            return await PersistAsync(request, totalSw, steps,
                reply: semExplain.Reply,
                sql: semResult.Sql,
                rowCount: semResult.Result.RowCount,
                rows: semResult.Result.Rows,
                similarEntities: semResult.Hits,
                cancellationToken: cancellationToken);
        }

        // ── Tool dispatch (external knowledge: weather, FX, country lookup, etc) ──
        var (toolResult, toolElapsed) = await TimedAsync(request, StageNames.StepToolDispatch,
            ct => _toolHandler.TryHandleAsync(question, ct), cancellationToken);
        if (toolResult is not null)
        {
            steps.RecordToolDispatch(question, toolResult, toolElapsed);
            if (!string.IsNullOrWhiteSpace(toolResult.ClarificationQuestion))
            {
                return await PersistAsync(request, totalSw, steps,
                    reply: toolResult.ClarificationQuestion!,
                    sql: toolResult.Sql,
                    cancellationToken: cancellationToken);
            }
            var toolExplain = await _explainer.ExplainAsync(question, toolResult.Result,
                new CompiledSql(toolResult.Sql, new Dictionary<string, object?>()), cancellationToken);
            return await PersistAsync(request, totalSw, steps,
                reply: toolExplain.Reply,
                sql: toolResult.Sql,
                rowCount: toolResult.Result.RowCount,
                rows: toolResult.Result.Rows,
                error: toolResult.Result.Error,
                cancellationToken: cancellationToken);
        }

        // ── Verified-query fast-path (Snowflake pattern: hand-curated Q→SQL pairs) ──
        if (_verifiedQueryMatcher.IsAvailable)
        {
            var (vqMatch, vqElapsed) = await TimedAsync(request, StageNames.StepVerifiedQuery,
                ct => _verifiedQueryMatcher.MatchAsync(question, ct), cancellationToken);
            if (vqMatch is not null)
            {
                steps.RecordVerifiedQuery(question, vqMatch, vqElapsed);
                var compiled = new CompiledSql(vqMatch.Query.Sql, new Dictionary<string, object?>());
                // Still through validator + executor for safety (read-only enforcement, etc).
                var v = _validator.Validate(compiled);
                if (v.IsValid)
                {
                    steps.RecordValidatorOk(compiled, attempt: 0);
                    _progress.NotifyStepStarting(TargetFor(request), StageNames.StepExecutor);
                    var execSw = Stopwatch.StartNew();
                    var er = await _executor.ExecuteAsync(compiled, cancellationToken);
                    execSw.Stop();
                    steps.RecordExecutor(compiled, er, execSw.ElapsedMilliseconds, attempt: 0);
                    if (er.Error is null)
                    {
                        var expSw = Stopwatch.StartNew();
                        var exp = await _explainer.ExplainAsync(question, er, compiled, cancellationToken);
                        expSw.Stop();
                        steps.RecordExplainer(question, compiled, er, exp, expSw.ElapsedMilliseconds);
                        return (await PersistAsync(request, totalSw, steps,
                            reply: exp.Reply, sql: compiled.Sql, rowCount: er.RowCount, rows: er.Rows,
                            cancellationToken: cancellationToken)) with { Provenance = "verified", Confidence = (double)vqMatch.Similarity };
                    }
                    _logger.LogWarning("[VerifiedQuery] match {Id} executed with error '{Err}' — falling through.",
                        vqMatch.Query.Id, er.Error);
                }
                else
                {
                    _logger.LogWarning("[VerifiedQuery] match {Id} failed validation: {Errs} — falling through.",
                        vqMatch.Query.Id, string.Join(";", v.Errors));
                }
            }
        }

        // ── Intent classifier (LLM router) — used to skip the ScopeGate on high-confidence SQL.
        // Note: the OOS refusal branch was removed — small classifier models over-flagged entity-heavy data questions as opinion/prediction (e.g., "bills issued by Aleppo electricity department"). Refusal is now the sole responsibility of the better-calibrated ScopeConfidenceGate. ──
        var intentResult = await _intentClassifier.ClassifyAsync(question, cancellationToken);
        var decisiveConf = _options.Value.IntentClassifierDecisiveConfidence;

        // ── Scope-confidence gate — residual OOS check. Skipped on high-confidence SQL verdict to avoid embedder-flap false refusals. ──
        Stages.OutOfScopeResult? scopeRefusal = null;
        if (intentResult.Intent != Stages.ClassifierIntent.Sql || intentResult.Confidence < decisiveConf)
        {
            scopeRefusal = await _scopeConfidenceGate.CheckAsync(question, cancellationToken);
        }
        if (scopeRefusal is not null)
        {
            activity?.SetTag("copilot.outcome", "refused.out_of_scope");
            activity?.SetTag("copilot.refusal_pattern", scopeRefusal.MatchedPattern);
            steps.RecordOutOfScopeRefusal(question, scopeRefusal);
            return (await PersistAsync(request, totalSw, steps,
                reply: scopeRefusal.Reason,
                error: scopeRefusal.Reason, cancellationToken: cancellationToken))
                with { Provenance = "refusal", Confidence = 1.0, Trace = StageNames.PreflightRefused };
        }

        // ── Decompose ────────────────────────────────────────────────────
        // LLM-first; regex decomposer opt-in via EnableHeuristicDecomposer (English-only fallback).
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepDecomposer);
        var decompSw = Stopwatch.StartNew();
        IReadOnlyList<string>? subQuestions = null;
        string decomposerSource = "disabled";
        string? llmPrompt = null;
        string? llmRaw = null;
        if (_options.Value.EnableDecomposer)
        {
            if (_options.Value.EnableHeuristicDecomposer)
            {
                var regexResult = _regexDecomposer.Decompose(question);
                if (regexResult is { SubQuestions.Count: > 1 })
                {
                    subQuestions = regexResult.SubQuestions;
                    decomposerSource = "regex";
                }
            }
            if (subQuestions is null)
            {
                var traced = await _llmDecomposer.SplitWithTraceAsync(question, cancellationToken);
                subQuestions = traced.SubQuestions;
                llmPrompt = traced.Prompt;
                llmRaw = traced.RawLlmOutput;
                decomposerSource = decomposerSource == "regex" ? "regex-then-llm" : "llm";
            }
        }
        decompSw.Stop();

        if (subQuestions is not null && subQuestions.Count > 1)
        {
            steps.RecordDecomposerSplit(question, subQuestions, decomposerSource, llmPrompt, llmRaw, decompSw.ElapsedMilliseconds);
            return await RunDecomposedAsync(request, subQuestions, totalSw, steps, cancellationToken);
        }

        if (_options.Value.EnableDecomposer)
        {
            steps.RecordDecomposerAtomic(question, decomposerSource, llmPrompt, llmRaw, decompSw.ElapsedMilliseconds);
        }

        return await RunSingleAsync(request, question, totalSw, steps, cancellationToken);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    // Wraps progress-notify + stopwatch around an async dispatch; returns (result, elapsedMs).
    private async Task<(T Result, long ElapsedMs)> TimedAsync<T>(
        CopilotRequest request, string stageName, Func<CancellationToken, Task<T>> dispatch,
        CancellationToken cancellationToken)
    {
        _progress.NotifyStepStarting(TargetFor(request), stageName);
        var sw = Stopwatch.StartNew();
        var result = await dispatch(cancellationToken);
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }

    private async Task<CopilotResponse> PersistAsync(
        CopilotRequest request, Stopwatch totalSw, List<PipelineStep> steps,
        string reply, string? sql = null, int? rowCount = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        string? error = null, string? chartType = null,
        IReadOnlyList<Abstractions.SemanticSearchHit>? similarEntities = null,
        IReadOnlyList<Abstractions.CopilotWarning>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        totalSw.Stop();
        var response = new CopilotResponse(
            Reply: reply,
            Sql: sql,
            RowCount: rowCount,
            Rows: rows,
            Trace: error is null ? StageNames.StatusOk : StageNames.StatusFailed,
            Error: error,
            Steps: steps,
            SimilarEntities: similarEntities,
            SuggestedChartType: chartType,
            Warnings: warnings);
        try
        {
            var traceId = await _traceSink.RecordAsync(
                question: request.Question ?? "",
                sql: sql,
                rowCount: rowCount,
                elapsedMs: totalSw.ElapsedMilliseconds,
                error: error,
                reply: reply,
                caseCode: request.CaseCode,
                sourceSuite: request.SourceSuite,
                sessionId: int.TryParse(request.ConversationId, out var sid) ? sid : (int?)null,
                rows: rows,
                steps: steps,
                expectedSql: request.ExpectedSql,
                cancellationToken: cancellationToken);
            response = response with { TraceId = traceId };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CopilotOrchestrator] trace persist failed (non-fatal).");
        }
        return response;
    }
}
