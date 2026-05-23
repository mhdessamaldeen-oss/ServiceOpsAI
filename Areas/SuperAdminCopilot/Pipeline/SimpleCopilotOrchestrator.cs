namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Internal;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Slim, redesigned orchestrator for the new copilot flow. Replaces the legacy
/// <see cref="CopilotOrchestrator"/> that ran a 7-handler cascade + 16 regex shapes; this one
/// runs: conversational/knowledge fast-paths → regex decomposer (fallback to LLM) → per-sub:
/// SpecExtractor → Compiler → Validator → Executor → Explainer.
///
/// <para>Designed for local LLMs: each question makes at most 1 (decompose-fallback) + 1 (spec)
/// + 1 (retry-on-error) + 1 (explainer) = 4 LLM calls in the worst case, ~2 on the happy path.</para>
/// </summary>
internal sealed class SimpleCopilotOrchestrator : ISuperAdminCopilot
{
    private readonly Stages.IConversationalHandler _conversational;
    private readonly Stages.IIntentClassifier _intentClassifier;
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
    private readonly ILogger<SimpleCopilotOrchestrator> _logger;

    public SimpleCopilotOrchestrator(
        Stages.IConversationalHandler conversational,
        Stages.IKnowledgeMatchHandler knowledge,
        Stages.IWriteIntentGuard writeIntentGuard,
        Stages.IIntentClassifier intentClassifier,
        Stages.IScopeConfidenceGate scopeConfidenceGate,
        Stages.ICoverageChecker coverageChecker,
        Stages.ISemanticSearchHandler semanticSearch,
        Stages.IToolHandler toolHandler,
        Stages.IDecomposer regexDecomposer,
        Stages.ILlmDecomposer llmDecomposer,
        Stages.ISpecExtractor specExtractor,
        Stages.ILlmDirectSqlEmitter directSqlEmitter,
        Stages.IQuestionShapeClassifier shapeClassifier,
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
        ILogger<SimpleCopilotOrchestrator> logger)
    {
        _conversational = conversational;
        _knowledge = knowledge;
        _writeIntentGuard = writeIntentGuard;
        _intentClassifier = intentClassifier;
        _scopeConfidenceGate = scopeConfidenceGate;
        _coverageChecker = coverageChecker;
        _semanticSearch = semanticSearch;
        _toolHandler = toolHandler;
        _regexDecomposer = regexDecomposer;
        _llmDecomposer = llmDecomposer;
        _specExtractor = specExtractor;
        _directSqlEmitter = directSqlEmitter;
        _shapeClassifier = shapeClassifier;
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

    // Shortcut accessor: the catalog is reload-on-change (IOptionsMonitor), so always read
    // CurrentValue at the call site — never cache.
    private CopilotTextCatalog Text => _textCatalog.CurrentValue;

    // Build the live-progress addressing struct for a request. Prefers the SignalR connection
    // id (known the instant the chat tab opened) over the conversation-id group (which the
    // client only joins after the server has assigned a sessionId — too late for the first turn).
    private static Abstractions.ProgressTarget TargetFor(CopilotRequest r) =>
        new(r.SignalRConnectionId, r.ConversationId);

    public async Task<CopilotResponse> AskAsync(CopilotRequest request, CancellationToken cancellationToken = default)
    {
        // Open the root distributed-tracing activity for this request. No-op when no listener
        // is attached (the common deployment) — operators who want OTel/Jaeger/Datadog plug an
        // ActivityListener for "SuperAdminCopilot" externally. Tags get recorded as span
        // attributes so the consuming observability tool can filter/correlate per-question.
        using var activity = Internal.CopilotActivitySource.Instance.StartActivity("Copilot.AskAsync");
        activity?.SetTag("copilot.conversation_id", request.ConversationId);
        activity?.SetTag("copilot.question_length", (request.Question ?? "").Length);

        // Reset the per-request retry budget. Scoped service, but the eval runner reuses one
        // DI scope across many questions so this guard prevents budget exhaustion from
        // poisoning every subsequent question in a batch.
        _retryBudget.Reset();

        // Open the per-question LLM metrics scope. Every ILlmClient call inside this method's
        // async path appends a record to the scope; the trace sink reads the totals when the
        // question completes. AsyncLocal-based, so decomposer fan-out via Task.WhenAll also
        // propagates correctly.
        using var llmScope = Abstractions.LlmCallScope.Begin();
        // Open the per-question embedding cache. CachingTextEmbedder decorator memoises every
        // ITextEmbedder.EmbedAsync call by (text, model) within this scope — the same question
        // would otherwise be embedded up to 5× per call (VectorRetriever, SchemaSemanticRetriever,
        // VerifiedQueryStore, PastQuestionStore, plus the post-save writeback).
        using var embedScope = Abstractions.QuestionEmbeddingScope.Begin();

        var totalSw = Stopwatch.StartNew();
        // ProgressTarget addresses the live-progress destination: prefer the SignalR connection
        // id (always known the instant the client connects), fall back to the chat group (which
        // only works once the client has joined chat_{sessionId}, i.e. after the first reply).
        var progressTarget = new Abstractions.ProgressTarget(request.SignalRConnectionId, request.ConversationId);
        // BroadcastingStepList fires the live progress sink on every Add — every existing
        // steps.Add(Step(...)) call site automatically reports to the chat UI's timeline
        // with zero per-site changes. Any new pipeline stage someone adds in the future
        // is broadcast for free.
        var steps = new BroadcastingStepList(_progress, progressTarget);
        // Normalize the raw question once at the entry point. Schema-agnostic cleanup —
        // collapses whitespace, rewrites parenthesised column lists ("users(name, email)" →
        // "users name email"), and strips comma runs. Every downstream stage (classifier,
        // embedder, decomposer, spec extractor) sees the canonical form; the raw question
        // stays on `request.Question` for trace / UI / explainer display.
        var question = Internal.QuestionTextNormalizer.Normalize(request.Question);
        if (string.IsNullOrEmpty(question))
            return new CopilotResponse(Reply: Text.EmptyQuestion, Error: "empty question");

        // ── Operational guard ────────────────────────────────────────────
        // Always records a step — on refusal AND on pass — so the trace tells the
        // truth about what the pipeline actually did. Previously the guard ran
        // silently on pass (the common case), which broke the workflow visual
        // continuity: every trace appeared to "skip" Operational Guard.
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
        // Deterministic, entity-agnostic. Refuses delete/drop/insert/update/alter/...
        // in milliseconds before any LLM call. Multi-language (EN + AR). Safety net behind
        // the read-only executor — which would also reject DML, but only after a wasted
        // LLM round-trip on spec extraction.
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
        // Vector-similarity search over the indexed entity embeddings. Falls through on miss.
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepSemanticSearch);
        var semSw = Stopwatch.StartNew();
        var semResult = await _semanticSearch.TryHandleAsync(question, cancellationToken);
        semSw.Stop();
        if (semResult is not null)
        {
            steps.RecordSemanticSearchHit(question, semResult, semSw.ElapsedMilliseconds);
            var semExplain = await _explainer.ExplainAsync(question, semResult.Result,
                new CompiledSql(semResult.Sql, new Dictionary<string, object?>()), cancellationToken);
            // SemanticSearch result populates SimilarEntities so the host UI can render entity
            // cards alongside the table. PersistAsync overload below carries this through.
            return await PersistAsync(request, totalSw, steps,
                reply: semExplain.Reply,
                sql: semResult.Sql,
                rowCount: semResult.Result.RowCount,
                rows: semResult.Result.Rows,
                similarEntities: semResult.Hits,
                cancellationToken: cancellationToken);
        }

        // ── Tool dispatch (external knowledge: weather, FX, country lookup, etc) ──
        // Runs when the user's question matches a registered tool in CopilotToolDefinitions.
        // Falls through to the data-query path on miss.
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepToolDispatch);
        var toolSw = Stopwatch.StartNew();
        var toolResult = await _toolHandler.TryHandleAsync(question, cancellationToken);
        toolSw.Stop();
        if (toolResult is not null)
        {
            steps.RecordToolDispatch(question, toolResult, toolSw.ElapsedMilliseconds);
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
        // Embedding-match against the admin-maintained verified-queries.json. If a stored
        // question is similar enough (per per-entry or default threshold), use its SQL
        // directly — skip the LLM. This is the "no more rule patching" escape hatch.
        if (_verifiedQueryMatcher.IsAvailable)
        {
            _progress.NotifyStepStarting(TargetFor(request), StageNames.StepVerifiedQuery);
            var vqSw = Stopwatch.StartNew();
            var vqMatch = await _verifiedQueryMatcher.MatchAsync(question, cancellationToken);
            vqSw.Stop();
            if (vqMatch is not null)
            {
                steps.RecordVerifiedQuery(question, vqMatch, vqSw.ElapsedMilliseconds);
                var compiled = new CompiledSql(vqMatch.Query.Sql, new Dictionary<string, object?>());
                // Still go through validator + executor for safety (read-only enforcement, etc).
                var v = _validator.Validate(compiled);
                if (v.IsValid)
                {
                    // Record Validator pass — fixes the "silent pass" UI break on the workflow
                    // tab where Validator showed dashed/not-fired for VerifiedQuery traces.
                    steps.RecordValidatorOk(compiled, attempt: 0);
                    _progress.NotifyStepStarting(TargetFor(request), StageNames.StepExecutor);
                    var execSw = Stopwatch.StartNew();
                    var er = await _executor.ExecuteAsync(compiled, cancellationToken);
                    execSw.Stop();
                    steps.RecordExecutor(compiled, er, execSw.ElapsedMilliseconds, attempt: 0);
                    if (er.Error is null)
                    {
                        // Time + record the Explainer call so the trace contains its step.
                        // Same fix pattern as Validator above — without this the workflow tab
                        // rendered Explainer as dashed/not-fired for VerifiedQuery traces.
                        var expSw = Stopwatch.StartNew();
                        var exp = await _explainer.ExplainAsync(question, er, compiled, cancellationToken);
                        expSw.Stop();
                        steps.RecordExplainer(question, compiled, er, exp, expSw.ElapsedMilliseconds);
                        return (await PersistAsync(request, totalSw, steps,
                            reply: exp.Reply, sql: compiled.Sql, rowCount: er.RowCount, rows: er.Rows,
                            cancellationToken: cancellationToken)) with { Provenance = "verified", Confidence = (double)vqMatch.Similarity };
                    }
                    // Verified SQL failed at runtime — fall through to normal pipeline.
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

        // ── Intent classifier (LLM router) ──────────────────────────────────
        // Runs BEFORE the lightweight scope gate. Uses the AiClassifierModel workload to label
        // the question as SQL / CHAT / TOOL / OUT_OF_SCOPE / REFINEMENT. We only take action on
        // HIGH-CONFIDENCE OOS (>= 0.80) — otherwise we fall through to the existing routers,
        // which preserves all existing behaviour. The classifier's decision is logged on every
        // call so we can tune the confidence floor against real traffic.
        // RATIONALE: when AiCopilotModel points at a narrow fine-tune (e.g. copilot-nl2sql), the
        // SQL specialist has lost the ability to refuse non-SQL inputs. This stage protects it.
        var intentResult = await _intentClassifier.ClassifyAsync(question, cancellationToken);
        if (intentResult.Intent == Stages.ClassifierIntent.OutOfScope && intentResult.Confidence >= 0.80)
        {
            activity?.SetTag("copilot.outcome", "refused.out_of_scope");
            activity?.SetTag("copilot.refusal_pattern", "intent-classifier-oos");
            activity?.SetTag("copilot.classifier.confidence", intentResult.Confidence);
            var refusalText = Text.PreflightOutOfScope;
            var oosResult = new Stages.OutOfScopeResult(refusalText, "intent-classifier-oos", "en");
            steps.RecordOutOfScopeRefusal(question, oosResult);
            return (await PersistAsync(request, totalSw, steps,
                reply: refusalText,
                error: refusalText, cancellationToken: cancellationToken))
                with { Provenance = "refusal", Confidence = intentResult.Confidence, Trace = StageNames.PreflightRefused };
        }

        // ── Scope-confidence gate ───────────────────────────────────────────
        // All fast paths have missed at this point (Conversational, Knowledge, SemanticSearch,
        // Tool, VerifiedQuery). Before committing to the LLM-driven SpecExtractor + Compiler +
        // Executor path, ask: does this question even link to anything in our configured scope?
        // The gate reads (a) max verified-query cosine and (b) top schema-linker score. If both
        // are below their floors (CopilotOptions.OutOfScopeVerifiedQueryFloor and
        // OutOfScopeSchemaFloor), refuse with the catalog message — the question is residual
        // out-of-scope, defined positively from what's in scope rather than enumerated.
        //
        // Skip the gate when the LLM classifier already labelled this as SQL with high
        // confidence (symmetric with the high-conf OOS branch above). The gate's signals
        // depend on the embedder; when the embedder transiently fails for a specific input
        // (e.g. Ollama returning NaN), both signals collapse to 0f and the gate would refuse
        // a perfectly valid data question. Trust the classifier in that case.
        Stages.OutOfScopeResult? scopeRefusal = null;
        if (intentResult.Intent != Stages.ClassifierIntent.Sql || intentResult.Confidence < 0.80)
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

        // ── Decompose: LLM only, multilingual. The regex decomposer is kept in DI for tests
        //     and toggled on with EnableHeuristicDecomposer for English-only deployments where
        //     the latency saving matters. Default OFF: the heuristic relies on English
        //     conjunctions ("and", "vs", "compared to") which silently fail on Arabic / etc.
        //     and produce wrong-shape splits like the period-comparison fan-out.
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepDecomposer);
        var decompSw = Stopwatch.StartNew();
        IReadOnlyList<string>? subQuestions = null;
        string decomposerSource = "disabled";       // "regex" | "llm" | "regex-then-llm" | "disabled"
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

        // Atomic-decision step: the decomposer RAN but found nothing to split.
        // Surfacing this matters for transparency — the user can see we considered decomposition
        // and decided against it, with the LLM's prompt+output visible if they want to debug.
        if (_options.Value.EnableDecomposer)
        {
            steps.RecordDecomposerAtomic(question, decomposerSource, llmPrompt, llmRaw, decompSw.ElapsedMilliseconds);
        }

        // ── Single question path ───────────────────────────────────────
        return await RunSingleAsync(request, question, totalSw, steps, cancellationToken);
    }

    private async Task<CopilotResponse> RunDecomposedAsync(
        CopilotRequest request, IReadOnlyList<string> subQuestions,
        Stopwatch totalSw, BroadcastingStepList steps,
        CancellationToken cancellationToken)
    {
        // Fan-out the sub-questions in parallel. Each runs its own RunSingleAsync end-to-end
        // (planner → compiler → executor → explainer); under the previous sequential loop a
        // 3-way split took ~24s when it should take ~8s. LlmCallScope and QuestionEmbeddingScope
        // are AsyncLocal — both propagate through Task.WhenAll automatically, so per-question
        // cost aggregation and the embedding cache both stay correct under parallel sub-queries.
        var tasks = subQuestions
            .Select(sub => RunSingleAsync(
                new CopilotRequest(sub, request.ConversationId), sub, Stopwatch.StartNew(),
                new BroadcastingStepList(_progress, TargetFor(request)), cancellationToken))
            .ToList();
        var results = await Task.WhenAll(tasks);

        var subReplies = new List<(string Q, CopilotResponse R)>(subQuestions.Count);
        for (int i = 0; i < subQuestions.Count; i++)
        {
            var sub = subQuestions[i];
            var subResp = results[i];
            subReplies.Add((sub, subResp));
            steps.RecordSubQuestion(i, sub, subResp);
        }

        var reply = new System.Text.StringBuilder();
        var sql = new System.Text.StringBuilder();
        var anyError = false;
        var totalRows = 0;
        for (int i = 0; i < subReplies.Count; i++)
        {
            var (q, r) = subReplies[i];
            reply.Append("**Q").Append(i + 1).Append(": ").Append(q).AppendLine("**");
            reply.AppendLine(r.Reply);
            reply.AppendLine();
            if (!string.IsNullOrEmpty(r.Sql))
            {
                if (sql.Length > 0) sql.AppendLine().AppendLine();
                sql.Append("-- Q").Append(i + 1).AppendLine().Append(r.Sql);
            }
            if (!string.IsNullOrEmpty(r.Error)) anyError = true;
            if (r.RowCount.HasValue) totalRows += r.RowCount.Value;
        }
        return await PersistAsync(request, totalSw, steps,
            reply: reply.ToString().TrimEnd(),
            sql: sql.Length > 0 ? sql.ToString() : null,
            rowCount: totalRows,
            error: anyError ? Text.DecomposedFailedSummary : null,
            cancellationToken: cancellationToken);
    }

    private async Task<CopilotResponse> RunSingleAsync(
        CopilotRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps,
        CancellationToken cancellationToken)
    {
        // Phase 3 Step 12 — shape classifier fast-route. When the question contains strong
        // signals of a window function / running total / rank / percentile (anything the
        // form-filling QuerySpec can't express), skip SpecExtractor + Compiler + retry loop
        // and go straight to the LlmDirectSqlEmitter escape valve. Saves 1-3 wasted LLM
        // calls per such question. Multi-turn refinement bypasses this — refinements
        // always go through SpecExtractor so previous spec context is honored.
        if (_shapeClassifier.Classify(question) == Stages.QuestionShape.ComplexAnalytics
            && _specMemory.Recall(request.ConversationId) is null)
        {
            // We need candidate tables for the emitter — fetch a quick retrieval cycle via
            // SpecExtractor (it returns CandidateTables even when spec is null on the
            // shortest path; for the happy-path question this is just the retrieval cost).
            var probeExtraction = await _specExtractor.ExtractAsync(question, cancellationToken);
            var fastResult = await TryEscapeValveAsync(
                request, totalSw, steps, question, probeExtraction.CandidateTables, cancellationToken);
            if (fastResult is not null) return fastResult;
            // Escape valve declined — fall through to the normal form-filling path so the
            // user still gets an answer (the classifier is a hint, not a hard switch).
        }

        // Phase 2.3 multi-turn refinement: if a previous spec exists on this conversation and
        // the new question reads like a follow-up, ask the LLM to MODIFY the previous spec.
        var previousSpec = _specMemory.Recall(request.ConversationId);
        var isRefinement = previousSpec is not null && _refinementDetector.LooksLikeRefinement(question);

        _progress.NotifyStepStarting(TargetFor(request),
            isRefinement ? StageNames.StepSpecRefine : StageNames.StepSpecExtractor);
        var specSw = Stopwatch.StartNew();
        var extraction = isRefinement
            ? await _specExtractor.RefineAsync(question, previousSpec!, cancellationToken)
            : await _specExtractor.ExtractAsync(question, cancellationToken);
        specSw.Stop();
        var stageName = isRefinement ? StageNames.StepSpecRefine : StageNames.StepSpecExtractor;
        if (extraction.Spec is null)
        {
            steps.RecordSpecExtractorFailed(stageName, question, extraction, isRefinement, specSw.ElapsedMilliseconds);
            // When the extraction error names a specific cause (no schema, no embedder, no
            // candidate tables), surface it directly so the user sees what to do. Generic
            // failure falls back to the catalog message.
            var err = extraction.Error ?? "";
            var reply = (err.Contains("schema-knowledge") || err.Contains("embedder")
                         || err.StartsWith("no candidate tables matched"))
                ? err
                : Text.SpecExtractorFailed;
            return await PersistAsync(request, totalSw, steps,
                reply: reply,
                error: extraction.Error ?? "spec extraction failed", cancellationToken: cancellationToken);
        }
        steps.RecordSpecExtractorOk(stageName, question, extraction, isRefinement, specSw.ElapsedMilliseconds);

        // Clarification short-circuit
        if (string.Equals(extraction.Spec.Intent, SpecConst.Intent.Clarification, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(extraction.Spec.ClarificationQuestion))
        {
            return await PersistAsync(request, totalSw, steps,
                reply: extraction.Spec.ClarificationQuestion!,
                cancellationToken: cancellationToken);
        }

        // Access policy enforcement — refuse specs that touch blocked tables / blocked columns
        // from the admin's CopilotSchemaAccessPolicy. Runs BEFORE compile so we never even
        // generate SQL for a forbidden spec.
        var accessRefusal = _accessPolicy.Check(extraction.Spec);
        steps.RecordAccessPolicy(extraction.Spec, accessRefusal);
        if (accessRefusal is not null)
        {
            return await PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.AccessPolicyRefusalTemplate, accessRefusal),
                error: accessRefusal, cancellationToken: cancellationToken);
        }

        // Compile → Validate → IntentGuard → Execute, with up to N retries on failure.
        // Each retry re-prompts the LLM. The cap comes from CopilotOptions; the option's own
        // [Range(0, 5)] validator enforces a sane upper bound. Recommended default is 1 — empirically
        // retries 2+ rarely break a stuck-loop, and going from 5 → 1 saves ~50s per failed question
        // with no measurable accuracy loss. Operators who want to experiment with higher values can,
        // and the setting is now honored end-to-end rather than silently clamped.
        var maxRetries = Math.Max(0, _options.Value.MaxSelfCorrectionRetries);
        var spec = extraction.Spec;
        CompiledSql? compiled = null;
        ExecutionResult? execResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            // Deterministic enrichment: fill universal invariants the LLM may have missed —
            // default projection on empty select, filtered columns in select (so the user
            // sees the filter's value), FK labels resolved, group-by columns in select,
            // dedup. Runs every attempt so refined specs also benefit. Idempotent.
            _specEnricher.Enrich(spec);

            // Compile
            try { compiled = _compiler.Compile(spec); }
            catch (Exception ex)
            {
                steps.RecordCompilerFailed(spec, ex.Message, attempt);
                if (attempt < maxRetries && await TryRefineSpecAsync(question, spec, ex.Message, steps, cancellationToken) is { } refined1)
                { spec = refined1; continue; }
                return await PersistAsync(request, totalSw, steps,
                    reply: string.Format(Text.CompilerFailedTemplate, ex.Message),
                    error: ex.Message, cancellationToken: cancellationToken);
            }
            steps.RecordCompilerOk(spec, compiled, attempt);

            // Validate
            var validation = _validator.Validate(compiled);
            if (!validation.IsValid)
            {
                var msg = string.Join("; ", validation.Errors);
                steps.RecordValidatorFailed(compiled, validation.Errors, attempt);
                if (attempt < maxRetries && await TryRefineSpecAsync(question, spec, msg, steps, cancellationToken) is { } refined2)
                { spec = refined2; continue; }
                return await PersistAsync(request, totalSw, steps,
                    reply: string.Format(Text.ValidatorRejectedTemplate, msg),
                    sql: compiled.Sql, error: "validation failed", cancellationToken: cancellationToken);
            }
            steps.RecordValidatorOk(compiled, attempt);

            // SqlIntentGuard
            var guardIssue = _sqlIntentGuard.Check(question, spec, compiled.Sql);
            if (guardIssue is not null)
            {
                steps.RecordSqlIntentGuardFailed(question, spec, compiled, guardIssue, attempt);
                if (attempt < maxRetries && await TryRefineSpecAsync(question, spec,
                    guardIssue.RetryHint ?? guardIssue.Reason, steps, cancellationToken) is { } refined3)
                { spec = refined3; continue; }
                return await PersistAsync(request, totalSw, steps,
                    reply: string.Format(Text.SqlIntentMismatchTemplate, guardIssue.Reason),
                    sql: compiled.Sql, error: guardIssue.Reason, cancellationToken: cancellationToken);
            }
            steps.RecordSqlIntentGuardOk(question, spec, attempt);

            // Execute
            _progress.NotifyStepStarting(TargetFor(request), $"{StageNames.StepExecutor}{(attempt == 0 ? "" : $" (retry {attempt})")}");
            var execSw = Stopwatch.StartNew();
            execResult = await _executor.ExecuteAsync(compiled, cancellationToken);
            execSw.Stop();
            steps.RecordExecutor(compiled, execResult, execSw.ElapsedMilliseconds, attempt);

            // Row-shape sanity check: a "top N" or aggregation that returns 0 rows is almost
            // always a sign the LLM added a spurious filter. Retry once with a hint.
            if (execResult.Error is null
                && execResult.RowCount == 0
                && attempt < maxRetries
                && IsSuspiciousEmptyResult(spec))
            {
                var emptyHint = Text.EmptyResultRetryHint;
                steps.RecordRowShapeSanityFailed(DescribeShape(spec), emptyHint, attempt);
                if (await TryRefineSpecAsync(question, spec, emptyHint, steps, cancellationToken) is { } refinedEmpty)
                { spec = refinedEmpty; continue; }
            }

            if (execResult.Error is null) break;

            if (attempt < maxRetries && await TryRefineSpecAsync(question, spec, execResult.Error, steps, cancellationToken) is { } refined4)
            { spec = refined4; continue; }

            // Escape valve: form-filling QuerySpec couldn't produce runnable SQL. Try the
            // direct-SQL emitter as a last resort. Its output still passes the SAME validator
            // + executor, so the read-only / no-DML / no-multi-statement guarantees hold.
            var escapeResult = await TryEscapeValveAsync(
                request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
            if (escapeResult is not null) return escapeResult;

            return await PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.ExecutorFailedTemplate, execResult.Error),
                sql: compiled.Sql, error: execResult.Error, cancellationToken: cancellationToken);
        }

        // Loop is exhaustive — only path out is via `break` on a successful execute, which sets
        // both variables. The throws below document the invariant; if a future refactor adds a
        // new break path that doesn't set them, we'd rather fail here than NRE downstream.
        var finalCompiled = compiled ?? throw new InvalidOperationException("compiled was null after pipeline loop");
        var finalExec = execResult ?? throw new InvalidOperationException("execResult was null after pipeline loop");

        // 6. Explain (LLM generates a natural-language reply over the result rows)
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepExplainer);
        var explainSw = Stopwatch.StartNew();
        var explanation = await _explainer.ExplainAsync(question, finalExec, finalCompiled, cancellationToken);
        explainSw.Stop();
        steps.RecordExplainer(question, finalCompiled, finalExec, explanation, explainSw.ElapsedMilliseconds);

        // Phase 2.3: remember the successful spec for the next turn's refinement.
        _specMemory.Remember(request.ConversationId, spec);

        // Phase 2 visualization hint: suggest a chart type for the host UI.
        var chartType = _chartSuggester.Suggest(spec, finalExec);

        // ── Coverage Check ───────────────────────────────────────────────
        // Entity-agnostic verification pass: did the answer fully address the question?
        // Catches compound-question half-answers (decomposer missed), wrong-FK-column joins
        // (asked for "created by" got "assigned to"), and other cases where the pipeline
        // technically succeeded but didn't actually answer what the user asked.
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepCoverageCheck);
        var coverageSw = Stopwatch.StartNew();
        var coverageGap = await _coverageChecker.CheckAsync(
            question, finalCompiled.Sql, finalExec, explanation.Reply, cancellationToken);
        coverageSw.Stop();
        var finalReply = explanation.Reply;
        if (coverageGap is not null)
        {
            steps.RecordCoverageCheckIncomplete(question, coverageGap, coverageSw.ElapsedMilliseconds);
            // CoverageCheckMode (D3) — operators choose how to react to a detected gap:
            // - Warn (default): prepend one-line warning, keep the answer (legacy behavior).
            // - Refuse: replace the answer with a refusal that names the gap.
            // - Off: shouldn't reach here (EnableCoverageChecker=false short-circuits earlier),
            //   but treat as Warn defensively if it does.
            switch (_options.Value.CoverageCheckMode)
            {
                case CoverageCheckMode.Refuse:
                    finalReply = $"I couldn't fully answer that — the result is missing: {coverageGap.Missing}. Try rephrasing the question to ask one thing at a time, or be more specific about the dimension you want.";
                    break;
                case CoverageCheckMode.Warn:
                case CoverageCheckMode.Off:
                default:
                    finalReply = $"⚠ This answer may not cover everything you asked — missing: {coverageGap.Missing}\n\n" + explanation.Reply;
                    break;
            }
        }
        else
        {
            steps.RecordCoverageCheckComplete(question, coverageSw.ElapsedMilliseconds);
        }

        // Provenance: "self-corrected" if any retry fired during the compile/validate/execute
        // loop, otherwise "llm-cold". Confidence shrinks with each retry: 1.0 -> 0.8 -> 0.6.
        // The attempt counter lives inside the for loop above and isn't in scope here, so we
        // detect retries by scanning the recorded steps for a SpecRefine entry.
        var retryFired = steps.Any(s => string.Equals(s.Stage, StageNames.StepSpecRefine, StringComparison.OrdinalIgnoreCase));
        var prov = retryFired ? "self-corrected" : "llm-cold";
        var conf = retryFired ? 0.6 : 0.85;

        return (await PersistAsync(request, totalSw, steps,
            reply: finalReply,
            sql: finalCompiled.Sql,
            rowCount: finalExec.RowCount,
            rows: finalExec.Rows,
            chartType: chartType,
            cancellationToken: cancellationToken)) with { Provenance = prov, Confidence = conf };
    }

    // Refinement detection moved to RefinementDetector.cs.

    // A spec is "suspiciously empty" when the user clearly asked for results AND we got zero.
    // Top-N (LIMIT > 0) almost always implies at least 1 row should exist. Same for aggregation
    // queries — COUNT/SUM always return at least one row in correct SQL (even if it's 0).
    // Filtered LIST queries are also suspicious: when the LLM emits filters and the result is
    // empty, the filters are almost always too restrictive (wrong column / wrong value / spurious
    // join). One exception: anti-joins legitimately return root rows with no match — empty IS
    // the valid answer ("users with no tickets" when every user has at least one ticket).
    private static bool IsSuspiciousEmptyResult(QuerySpec spec)
    {
        if (spec.Limit is > 0) return true;
        if (spec.Aggregations.Count > 0 && spec.GroupBy.Count == 0) return true;
        // Filtered list — flag, unless this is an anti-join (empty is correct).
        var hasFilters = spec.Filters.Count > 0;
        var hasAntiJoin = spec.Joins.Any(j =>
            string.Equals(j.Kind, SpecConst.JoinKinds.Anti, StringComparison.OrdinalIgnoreCase));
        if (hasFilters && !hasAntiJoin) return true;
        return false;
    }

    private static string DescribeShape(QuerySpec spec)
    {
        if (spec.Limit is > 0) return $"top {spec.Limit}";
        if (spec.Aggregations.Count > 0) return "aggregation";
        if (spec.Filters.Count > 0) return "filtered list";
        return "list";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private async Task<QuerySpec?> TryRefineSpecAsync(
        string question, QuerySpec previousSpec, string error,
        BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        var refineSw = Stopwatch.StartNew();
        // Small jittered backoff before re-prompting the LLM. Reasons:
        //   - if the failure was a transient provider blip (network reset, rate-limit
        //     burst), a 100-400ms pause is enough for the provider to recover
        //   - if the failure was deterministic (bad JSON), the pause is irrelevant
        //     and the retry will fail again anyway — capped low so we don't waste time
        //   - jitter avoids thundering-herd retries when many concurrent questions
        //     fail at once against the same provider
        var jitter = 100 + Random.Shared.Next(300);
        try { await Task.Delay(jitter, cancellationToken); }
        catch (OperationCanceledException) { return null; }
        var retry = await _specExtractor.RetryWithErrorAsync(question, previousSpec, error, cancellationToken);
        refineSw.Stop();
        if (retry.Spec is null)
        {
            steps.RecordSpecRefineFailed(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
            return null;
        }
        // ── Convergence detection ───────────────────────────────────────────────────
        // Compare structural hashes: if the refined spec is identical to the previous
        // one, the LLM produced the same answer it just had rejected. Continuing to
        // retry would burn LLM calls without progress (the canonical "legitimately empty
        // result" case in trace #6 cost 36k tokens / 4 min before this guard existed).
        // Returning null signals "give up this retry path"; the caller's `if (... is { } refined)`
        // pattern treats null exactly like a refine-failure, so the outer pipeline naturally
        // accepts the previous attempt's outcome instead of looping.
        if (QuerySpecHasher.Hash(previousSpec) == QuerySpecHasher.Hash(retry.Spec))
        {
            steps.RecordSpecRefineConverged(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
            return null;
        }
        steps.RecordSpecRefineOk(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
        return retry.Spec;
    }

    // Escape valve: when the form-filling SpecExtractor + Compiler path has exhausted its
    // retries, ask the LLM to emit raw T-SQL directly. The output passes the SAME validator +
    // read-only executor, so safety guarantees are preserved (no DML, no multi-statement,
    // no unknown tables). Returns the success response or null on any failure (so the caller
    // falls through to the original error reply).
    private async Task<CopilotResponse?> TryEscapeValveAsync(
        CopilotRequest request, Stopwatch totalSw, BroadcastingStepList steps,
        string question, IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken)
    {
        if (candidateTableNames is null || candidateTableNames.Count == 0) return null;

        var emitSw = Stopwatch.StartNew();
        var emit = await _directSqlEmitter.EmitAsync(question, candidateTableNames, cancellationToken);
        emitSw.Stop();
        if (emit.Sql is null) return null;
        steps.RecordLlmDirectSql(question, emit.Sql, emitSw.ElapsedMilliseconds);

        var compiled = new CompiledSql(emit.Sql, new Dictionary<string, object?>());

        var validation = _validator.Validate(compiled);
        if (!validation.IsValid) return null;

        var execSw = Stopwatch.StartNew();
        var execResult = await _executor.ExecuteAsync(compiled, cancellationToken);
        execSw.Stop();
        if (execResult.Error is not null) return null;

        var explanation = await _explainer.ExplainAsync(question, execResult, compiled, cancellationToken);

        return (await PersistAsync(request, totalSw, steps,
            reply: explanation.Reply,
            sql: compiled.Sql,
            rowCount: execResult.RowCount,
            rows: execResult.Rows,
            cancellationToken: cancellationToken)) with { Provenance = "direct-emit", Confidence = 0.7 };
    }

    private async Task<CopilotResponse> PersistAsync(
        CopilotRequest request, Stopwatch totalSw, List<PipelineStep> steps,
        string reply, string? sql = null, int? rowCount = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        string? error = null, string? chartType = null,
        IReadOnlyList<Abstractions.SemanticSearchHit>? similarEntities = null,
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
            SuggestedChartType: chartType);
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
                rows: rows,
                steps: steps,
                expectedSql: request.ExpectedSql,
                cancellationToken: cancellationToken);
            response = response with { TraceId = traceId };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SimpleCopilotOrchestrator] trace persist failed (non-fatal).");
        }
        return response;
    }

}
