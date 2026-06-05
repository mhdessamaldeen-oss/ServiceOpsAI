namespace AnalystAgent.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Internal;
using AnalystAgent.Models;
using AnalystAgent.Pipeline.Stages;

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
//   - AnalystOrchestrator.cs           : AskAsync, fields, ctor, shared helpers (TimedAsync)
//   - AnalystOrchestrator.Decomposed.cs: multi-sub-question fan-out (parallel + sequential)
internal sealed partial class AnalystOrchestrator : IAnalystAgent
{
    // ── Preflight / fast-path stages ────────────────────────────────────────────────────────────
    private readonly Stages.IConversationalHandler _conversational;
    private readonly Stages.IKnowledgeMatchHandler _knowledge;
    private readonly Stages.IWriteIntentGuard _writeIntentGuard;
    private readonly Stages.IIntentClassifier _intentClassifier;
    private readonly Stages.IScopeConfidenceGate _scopeConfidenceGate;
    private readonly Stages.ISemanticSearchHandler _semanticSearch;
    private readonly Stages.IToolHandler _toolHandler;
    private readonly Stages.IDecomposer _regexDecomposer;
    private readonly Stages.ILlmDecomposer _llmDecomposer;
    private readonly Schema.ISchemaLinker _schemaLinker;
    private readonly IExplainer _explainer;

    // ── Dispatcher deps ─────────────────────────────────────────────────────────────────────────
    private readonly ISingleQuestionExecutor _singleExecutor;
    private readonly IResponsePersister _persister;
    private readonly IOperationalGuard _operationalGuard;
    private readonly IRetryBudget _retryBudget;
    private readonly IPipelineStepProgressSink _progress;
    private readonly IOptions<AnalystOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<AnalystOrchestrator> _logger;

    public AnalystOrchestrator(
        Stages.IConversationalHandler conversational,
        Stages.IKnowledgeMatchHandler knowledge,
        Stages.IWriteIntentGuard writeIntentGuard,
        Stages.IIntentClassifier intentClassifier,
        Stages.IScopeConfidenceGate scopeConfidenceGate,
        Stages.ISemanticSearchHandler semanticSearch,
        Stages.IToolHandler toolHandler,
        Stages.IDecomposer regexDecomposer,
        Stages.ILlmDecomposer llmDecomposer,
        Schema.ISchemaLinker schemaLinker,
        IExplainer explainer,
        ISingleQuestionExecutor singleExecutor,
        IResponsePersister persister,
        IOperationalGuard operationalGuard,
        IRetryBudget retryBudget,
        IPipelineStepProgressSink progress,
        IOptions<AnalystOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<AnalystOrchestrator> logger)
    {
        _conversational = conversational;
        _knowledge = knowledge;
        _writeIntentGuard = writeIntentGuard;
        _intentClassifier = intentClassifier;
        _scopeConfidenceGate = scopeConfidenceGate;
        _semanticSearch = semanticSearch;
        _toolHandler = toolHandler;
        _regexDecomposer = regexDecomposer;
        _llmDecomposer = llmDecomposer;
        _schemaLinker = schemaLinker;
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
    private static Abstractions.ProgressTarget TargetFor(AnalystRequest r) =>
        new(r.SignalRConnectionId, r.ConversationId);

    /// <summary>Outcome of <see cref="DecideScope"/> — what to do with the scope-confidence gate.</summary>
    internal enum ScopeDecision { RefuseOutOfScope, SkipGate, RunGate }

    /// <summary>What to do with the scope gate, given the classifier verdict. Pure + unit-testable.
    /// <list type="bullet">
    ///   <item><b>RefuseOutOfScope</b> — a high-confidence OUT_OF_SCOPE verdict: refuse outright (don't
    ///     defer to the weak cosine gate that leaks on name-table brushes).</item>
    ///   <item><b>SkipGate</b> — a high-confidence SQL verdict: the question is decisively in-scope; skip
    ///     the cosine gate (prior behavior).</item>
    ///   <item><b>RunGate</b> — anything else (non-SQL non-OOS, or any verdict below the floor): let the
    ///     cosine scope gate decide.</item>
    /// </list></summary>
    internal static ScopeDecision DecideScope(Stages.ClassifierIntent intent, double confidence, double decisiveConf)
    {
        if (intent == Stages.ClassifierIntent.OutOfScope && confidence >= decisiveConf)
            return ScopeDecision.RefuseOutOfScope;
        if (intent == Stages.ClassifierIntent.Sql && confidence >= decisiveConf)
            return ScopeDecision.SkipGate;
        return ScopeDecision.RunGate;
    }

    public async Task<AnalystResponse> AskAsync(AnalystRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = Internal.AnalystActivitySource.Instance.StartActivity("Copilot.AskAsync");
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
            return new AnalystResponse(Reply: Text.EmptyQuestion, Error: "empty question");

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
        // Async: a small-talk hit may spend exactly ONE LLM call for a warm reply (fail-open to canned).
        // Greetings/thanks/farewell/capabilities stay 0-LLM. Either way this short-circuits before the planner.
        var convReply = await _conversational.TryHandleAsync(question, cancellationToken);
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

        // ── Intent classifier ────────────────────────────────────────────────
        var intentResult = await _intentClassifier.ClassifyAsync(question, cancellationToken);
        var decisiveConf = _options.Value.IntentClassifierDecisiveConfidence;

        // ── Scope-confidence gate ────────────────────────────────────────────
        // The decision (refuse-OOS / run-gate / skip-gate) is a pure function of the classifier
        // verdict + the decisive-confidence floor — extracted to DecideScope so it is unit-testable
        // without standing up the whole orchestrator.
        Stages.OutOfScopeResult? scopeRefusal = null;
        Stages.ScopeSignals? scopeSignals = null;   // the two cosines vs floors — surfaced into the trace ("the moat")
        switch (DecideScope(intentResult.Intent, intentResult.Confidence, decisiveConf))
        {
            // Decisive classifier OOS — normally refuse OUTRIGHT instead of deferring to the weak cosine
            // gate (which leaks on name-table brushes like "who won the world cup" → Countries.NameEn,
            // ~0.20 in bge-m3's noise band). BUT the 7B classifier mislabels many VALID questions as OOS
            // — natural-key lookups ("find ticket TKT-2026-00050") and entity questions on non-embedded
            // tables ("show me the tariffs"). A deterministic in-scope signal (lexical anchor or a
            // natural-key format match) overrides the verdict and routes to the data path. A truly-OOS
            // question ("best recipe") has neither signal, so it is still refused.
            case ScopeDecision.RefuseOutOfScope:
                if (_schemaLinker.HasInScopeSignal(question))
                {
                    _logger.LogInformation(
                        "[Scope] classifier said OUT_OF_SCOPE but the question has a decisive in-scope "
                        + "signal (anchor/natural-key) — overriding to data path. q='{Q}'", question);
                }
                else
                {
                    scopeRefusal = new Stages.OutOfScopeResult(
                        Text.IntentOutOfScope, MatchedPattern: "intent-out-of-scope", Language: "auto");
                }
                break;
            // Not decisively SQL nor decisively OOS → the cosine scope gate decides.
            case ScopeDecision.RunGate:
            {
                var scopeOutcome = await _scopeConfidenceGate.CheckAsync(question, cancellationToken);
                scopeRefusal = scopeOutcome.Refusal;
                scopeSignals = scopeOutcome.Signals;
                // On an in-scope PASS, record the moat scores here (no refusal step will run). On a REFUSE
                // they ride along on RecordOutOfScopeRefusal below so the refusal carries its own scores.
                if (scopeSignals is not null && scopeRefusal is null)
                    steps.RecordScopeGate(question, scopeSignals);
                break;
            }
            // ScopeDecision.SkipGate (decisive SQL) → leave scopeRefusal null (prior behavior).
        }
        if (scopeRefusal is not null)
        {
            activity?.SetTag("copilot.outcome", "refused.out_of_scope");
            activity?.SetTag("copilot.refusal_pattern", scopeRefusal.MatchedPattern);
            steps.RecordOutOfScopeRefusal(question, scopeRefusal, scopeSignals);
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
                // Pre-gate (ENH-1): only spend the LLM decomposer call when the question carries a
                // compound signal (per the same config cues). Obviously-atomic questions — the large
                // majority — would get an "atomic" verdict from the LLM anyway, so skip the round-trip.
                if (!_options.Value.EnableDecomposerCompoundPreGate || _regexDecomposer.MightBeCompound(question))
                {
                    var traced = await _llmDecomposer.SplitWithTraceAsync(question, cancellationToken);
                    subQuestions = traced.SubQuestions;
                    llmPrompt = traced.Prompt;
                    llmRaw = traced.RawLlmOutput;
                    // This branch only runs when subQuestions is still null (the regex decomposer found
                    // no split), so the source is always "llm".
                    decomposerSource = "llm";
                }
                else
                {
                    decomposerSource = "pregate-atomic";   // no compound signal → LLM call skipped
                }
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
        AnalystRequest request, string stageName, Func<CancellationToken, Task<T>> dispatch,
        CancellationToken cancellationToken)
    {
        _progress.NotifyStepStarting(TargetFor(request), stageName);
        var sw = Stopwatch.StartNew();
        var result = await dispatch(cancellationToken);
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }
}
