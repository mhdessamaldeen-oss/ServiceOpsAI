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
/// Entry-point orchestrator. <see cref="AskAsync"/> routes through preflight guards → fast paths
/// (conversational, knowledge, semantic, tool, verified-query) → IntentClassifier →
/// ScopeConfidenceGate → Decomposer, then dispatches to <see cref="ISingleQuestionExecutor"/>
/// or <see cref="RunDecomposedAsync"/>.
///
/// <para>2026-06-01 — single-question execution loop extracted to <see cref="ISingleQuestionExecutor"/>;
/// trace persistence extracted to <see cref="IResponsePersister"/>. This class is now a thin
/// dispatcher: ~160 LOC of routing / preflight / fast-path logic, zero SQL-compilation concern.</para>
/// </summary>
// Class split across 2 partial files:
//   - CopilotOrchestrator.cs           : AskAsync, fields, ctor, shared helpers (TimedAsync)
//   - CopilotOrchestrator.Decomposed.cs: multi-sub-question fan-out (parallel + sequential)
internal sealed partial class CopilotOrchestrator : ISuperAdminCopilot
{
    // ── Preflight / fast-path stages ────────────────────────────────────────────────────────────
    private readonly Stages.IConversationalHandler _conversational;
    private readonly Stages.IKnowledgeMatchHandler _knowledge;
    private readonly Stages.IWriteIntentGuard _writeIntentGuard;
    private readonly Stages.IIntentClassifier _intentClassifier;
    private readonly Retrieval.ISchemaSemanticRetriever _schemaRetriever;
    private readonly Stages.IScopeConfidenceGate _scopeConfidenceGate;
    private readonly Stages.ISemanticSearchHandler _semanticSearch;
    private readonly Stages.IToolHandler _toolHandler;
    private readonly Stages.IDecomposer _regexDecomposer;
    private readonly Stages.ILlmDecomposer _llmDecomposer;
    private readonly Retrieval.IVerifiedQueryMatcher _verifiedQueryMatcher;
    private readonly IValidator _validator;
    private readonly IExecutor _executor;
    private readonly IExplainer _explainer;

    // ── Dispatcher deps ─────────────────────────────────────────────────────────────────────────
    private readonly ISingleQuestionExecutor _singleExecutor;
    private readonly IResponsePersister _persister;
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
        Stages.IScopeConfidenceGate scopeConfidenceGate,
        Stages.ISemanticSearchHandler semanticSearch,
        Stages.IToolHandler toolHandler,
        Stages.IDecomposer regexDecomposer,
        Stages.ILlmDecomposer llmDecomposer,
        Retrieval.IVerifiedQueryMatcher verifiedQueryMatcher,
        IValidator validator,
        IExecutor executor,
        IExplainer explainer,
        ISingleQuestionExecutor singleExecutor,
        IResponsePersister persister,
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
        _scopeConfidenceGate = scopeConfidenceGate;
        _semanticSearch = semanticSearch;
        _toolHandler = toolHandler;
        _regexDecomposer = regexDecomposer;
        _llmDecomposer = llmDecomposer;
        _verifiedQueryMatcher = verifiedQueryMatcher;
        _validator = validator;
        _executor = executor;
        _explainer = explainer;
        _singleExecutor = singleExecutor;
        _persister = persister;
        _operationalGuard = operationalGuard;
        _retryBudget = retryBudget;
        _progress = progress;
        _options = options;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    private CopilotTextCatalog Text => _textCatalog.CurrentValue;
    private static Abstractions.ProgressTarget TargetFor(CopilotRequest r) =>
        new(r.SignalRConnectionId, r.ConversationId);

    public async Task<CopilotResponse> AskAsync(CopilotRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = Internal.CopilotActivitySource.Instance.StartActivity("Copilot.AskAsync");
        activity?.SetTag("copilot.conversation_id", request.ConversationId);
        activity?.SetTag("copilot.question_length", (request.Question ?? "").Length);

        _retryBudget.Reset();
        using var llmScope = Abstractions.LlmCallScope.Begin();
        using var embedScope = Abstractions.QuestionEmbeddingScope.Begin();

        var totalSw = Stopwatch.StartNew();
        var progressTarget = new Abstractions.ProgressTarget(request.SignalRConnectionId, request.ConversationId);
        var steps = new BroadcastingStepList(_progress, progressTarget);

        var question = Internal.QuestionTextNormalizer.Normalize(request.Question);
        if (string.IsNullOrEmpty(question))
            return new CopilotResponse(Reply: Text.EmptyQuestion, Error: "empty question");

        // ── Operational guard ────────────────────────────────────────────────
        var guardRefusal = _operationalGuard.CheckOrRefuse(request.ConversationId);
        if (guardRefusal is not null)
        {
            steps.RecordOperationalGuardRefusal(question, guardRefusal);
            return (await _persister.PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.OperationalGuardRefusalTemplate, guardRefusal),
                error: guardRefusal, cancellationToken: cancellationToken))
                with { Trace = StageNames.PreflightRefused, Provenance = "refusal", Confidence = 1.0 };
        }
        steps.RecordOperationalGuardPassed(question);

        // ── Write-intent preflight ───────────────────────────────────────────
        var writeIntent = _writeIntentGuard.Check(question);
        if (writeIntent is not null)
        {
            steps.RecordWriteIntentRefusal(question, writeIntent);
            return (await _persister.PersistAsync(request, totalSw, steps,
                reply: writeIntent.Reason,
                error: writeIntent.Reason, cancellationToken: cancellationToken))
                with { Provenance = "refusal", Confidence = 1.0, Trace = StageNames.PreflightRefused };
        }
        steps.RecordWriteIntentPassed(question);

        // ── Conversational fast-path ─────────────────────────────────────────
        var convReply = _conversational.TryHandle(question);
        if (convReply is not null)
        {
            steps.RecordConversational(question, convReply);
            return (await _persister.PersistAsync(request, totalSw, steps,
                reply: convReply.Reply, cancellationToken: cancellationToken))
                with { Provenance = "conversational", Confidence = 1.0 };
        }

        // ── Knowledge-match fast-path ────────────────────────────────────────
        var kmReply = _knowledge.TryHandle(question);
        if (kmReply is not null)
        {
            steps.RecordKnowledgeMatch(question, kmReply);
            return (await _persister.PersistAsync(request, totalSw, steps,
                reply: kmReply.Reply, cancellationToken: cancellationToken))
                with { Provenance = "knowledge", Confidence = 1.0 };
        }

        // ── Semantic search fast-path ────────────────────────────────────────
        var (semResult, semElapsed) = await TimedAsync(request, StageNames.StepSemanticSearch,
            ct => _semanticSearch.TryHandleAsync(question, ct), cancellationToken);
        if (semResult is not null)
        {
            steps.RecordSemanticSearchHit(question, semResult, semElapsed);
            var semExplain = await _explainer.ExplainAsync(question, semResult.Result,
                new CompiledSql(semResult.Sql, new Dictionary<string, object?>()), cancellationToken);
            return await _persister.PersistAsync(request, totalSw, steps,
                reply: semExplain.Reply,
                sql: semResult.Sql,
                rowCount: semResult.Result.RowCount,
                rows: semResult.Result.Rows,
                similarEntities: semResult.Hits,
                cancellationToken: cancellationToken);
        }

        // ── Tool dispatch ────────────────────────────────────────────────────
        var (toolResult, toolElapsed) = await TimedAsync(request, StageNames.StepToolDispatch,
            ct => _toolHandler.TryHandleAsync(question, ct), cancellationToken);
        if (toolResult is not null)
        {
            steps.RecordToolDispatch(question, toolResult, toolElapsed);
            if (!string.IsNullOrWhiteSpace(toolResult.ClarificationQuestion))
            {
                return await _persister.PersistAsync(request, totalSw, steps,
                    reply: toolResult.ClarificationQuestion!,
                    sql: toolResult.Sql,
                    cancellationToken: cancellationToken);
            }
            var toolExplain = await _explainer.ExplainAsync(question, toolResult.Result,
                new CompiledSql(toolResult.Sql, new Dictionary<string, object?>()), cancellationToken);
            return await _persister.PersistAsync(request, totalSw, steps,
                reply: toolExplain.Reply,
                sql: toolResult.Sql,
                rowCount: toolResult.Result.RowCount,
                rows: toolResult.Result.Rows,
                error: toolResult.Result.Error,
                cancellationToken: cancellationToken);
        }

        // ── Verified-query fast-path ─────────────────────────────────────────
        if (_verifiedQueryMatcher.IsAvailable)
        {
            var (vqMatch, vqElapsed) = await TimedAsync(request, StageNames.StepVerifiedQuery,
                ct => _verifiedQueryMatcher.MatchAsync(question, ct), cancellationToken);
            if (vqMatch is not null)
            {
                steps.RecordVerifiedQuery(question, vqMatch, vqElapsed);
                var compiled = new CompiledSql(vqMatch.Query.Sql, new Dictionary<string, object?>());
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
                        return (await _persister.PersistAsync(request, totalSw, steps,
                            reply: exp.Reply, sql: compiled.Sql, rowCount: er.RowCount, rows: er.Rows,
                            cancellationToken: cancellationToken))
                            with { Provenance = "verified", Confidence = (double)vqMatch.Similarity };
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

        // ── Intent classifier ────────────────────────────────────────────────
        var intentResult = await _intentClassifier.ClassifyAsync(question, cancellationToken);
        var decisiveConf = _options.Value.IntentClassifierDecisiveConfidence;

        // ── Scope-confidence gate ────────────────────────────────────────────
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
            return (await _persister.PersistAsync(request, totalSw, steps,
                reply: scopeRefusal.Reason,
                error: scopeRefusal.Reason, cancellationToken: cancellationToken))
                with { Provenance = "refusal", Confidence = 1.0, Trace = StageNames.PreflightRefused };
        }

        // ── Decompose ────────────────────────────────────────────────────────
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

        // ── Dispatch to single-question executor ─────────────────────────────
        return await _singleExecutor.ExecuteAsync(request, question, totalSw, steps, cancellationToken);
    }

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
}
