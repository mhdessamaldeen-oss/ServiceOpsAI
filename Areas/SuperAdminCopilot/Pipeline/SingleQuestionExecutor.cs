namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Executes the single-question pipeline: entity resolution → spec extraction → compile/validate/
/// execute loop with retry → explainer → coverage check. Extracted from <c>CopilotOrchestrator</c>
/// on 2026-06-01 so the orchestrator stays a thin dispatcher (preflight, fast-paths, decomposer)
/// and the per-question work lives in its own focused class.
///
/// <para>Called by the orchestrator's <c>AskAsync</c> after preflight/fast-paths pass, and by
/// <c>RunDecomposedAsync</c> per sub-question. Behavior is byte-identical to the previous
/// <c>CopilotOrchestrator.RunSingleAsync</c> — this is a refactor, not a rewrite.</para>
/// </summary>
internal interface ISingleQuestionExecutor
{
    Task<CopilotResponse> ExecuteAsync(
        CopilotRequest request,
        string question,
        Stopwatch totalSw,
        BroadcastingStepList steps,
        CancellationToken cancellationToken);
}

internal sealed class SingleQuestionExecutor : ISingleQuestionExecutor
{
    private readonly Stages.IMetadataHandler _metadataHandler;
    private readonly Pipeline.EntityResolution.IFuzzyEntityResolver _entityResolver;
    private readonly Stages.Decomposed.IStructuralCueParser _cueParser;
    private readonly Stages.IConversationSpecMemory _specMemory;
    private readonly Stages.IRefinementDetector _refinementDetector;
    private readonly Stages.ISpecExtractor _specExtractor;
    private readonly Stages.IQuerySpecAccessPolicyValidator _accessPolicy;
    private readonly Stages.ISpecEnricher _specEnricher;
    private readonly ICompiler _compiler;
    private readonly IValidator _validator;
    private readonly Stages.ISqlIntentGuard _sqlIntentGuard;
    private readonly IExecutor _executor;
    private readonly IExplainer _explainer;
    private readonly Stages.IChartTypeSuggester _chartSuggester;
    private readonly Stages.ICoverageChecker _coverageChecker;
    private readonly Stages.ILlmDirectSqlEmitter _directSqlEmitter;
    private readonly Prompts.IPromptShapeClassifier _shapeClassifier;
    private readonly IPipelineStepProgressSink _progress;
    private readonly IOptions<CopilotOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly IResponsePersister _persister;
    private readonly ILogger<SingleQuestionExecutor> _logger;

    public SingleQuestionExecutor(
        Stages.IMetadataHandler metadataHandler,
        Pipeline.EntityResolution.IFuzzyEntityResolver entityResolver,
        Stages.Decomposed.IStructuralCueParser cueParser,
        Stages.IConversationSpecMemory specMemory,
        Stages.IRefinementDetector refinementDetector,
        Stages.ISpecExtractor specExtractor,
        Stages.IQuerySpecAccessPolicyValidator accessPolicy,
        Stages.ISpecEnricher specEnricher,
        ICompiler compiler,
        IValidator validator,
        Stages.ISqlIntentGuard sqlIntentGuard,
        IExecutor executor,
        IExplainer explainer,
        Stages.IChartTypeSuggester chartSuggester,
        Stages.ICoverageChecker coverageChecker,
        Stages.ILlmDirectSqlEmitter directSqlEmitter,
        Prompts.IPromptShapeClassifier shapeClassifier,
        IPipelineStepProgressSink progress,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        IResponsePersister persister,
        ILogger<SingleQuestionExecutor> logger)
    {
        _metadataHandler = metadataHandler;
        _entityResolver = entityResolver;
        _cueParser = cueParser;
        _specMemory = specMemory;
        _refinementDetector = refinementDetector;
        _specExtractor = specExtractor;
        _accessPolicy = accessPolicy;
        _specEnricher = specEnricher;
        _compiler = compiler;
        _validator = validator;
        _sqlIntentGuard = sqlIntentGuard;
        _executor = executor;
        _explainer = explainer;
        _chartSuggester = chartSuggester;
        _coverageChecker = coverageChecker;
        _directSqlEmitter = directSqlEmitter;
        _shapeClassifier = shapeClassifier;
        _progress = progress;
        _options = options;
        _textCatalog = textCatalog;
        _persister = persister;
        _logger = logger;
    }

    private CopilotTextCatalog Text => _textCatalog.CurrentValue;
    private static ProgressTarget TargetFor(CopilotRequest r) => new(r.SignalRConnectionId, r.ConversationId);

    public async Task<CopilotResponse> ExecuteAsync(
        CopilotRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps,
        CancellationToken cancellationToken)
    {
        // Schema-metadata fast path — "what tables exist" / "columns of X" / "how are X and Y
        // related" run as deterministic INFORMATION_SCHEMA / sys.foreign_keys queries with no
        // LLM. Bypass all downstream stages on a match. Returns null on miss; pipeline continues.
        try
        {
            var meta = await _metadataHandler.TryHandleAsync(question, cancellationToken);
            if (meta is not null)
            {
                totalSw.Stop();
                var rows = meta.Result?.Rows ?? System.Array.Empty<IReadOnlyDictionary<string, object?>>();
                var rowText = rows.Count == 0 ? "No rows." : $"Returned {rows.Count} row(s).";
                return new CopilotResponse(
                    Reply: rowText,
                    Sql: meta.Sql,
                    RowCount: rows.Count,
                    Rows: rows,
                    Steps: steps);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Tagged [copilot.silent_failure] so log-based alerts can fan out per stage when
            // these helpers fail systemically (embedding service down, schema cache poisoned).
            _logger.LogWarning(ex, "[copilot.silent_failure][stage=MetadataHandler] failed; continuing");
        }

        // Phase 6 — fuzzy entity resolution. Append canonical-form hints to the question
        // before any downstream stage sees it, so the LLM uses real DB values instead of
        // fabricating placeholders like '<RegionId for Damascus>' or 'RegionIdDamascus'.
        try
        {
            var resolvedEntities = await _entityResolver.ResolveAsync(question, cancellationToken);
            if (resolvedEntities.Count > 0)
            {
                var hints = string.Join("; ", resolvedEntities
                    .Take(5)
                    .Select(e => $"'{e.Surface}' = {e.Table}.{e.Column} '{e.Canonical}'"));
                question = $"{question}\n-- resolved: {hints}";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[copilot.silent_failure][stage=EntityResolver] failed; continuing without hints");
        }

        // LLM-based structural cue extraction: handles any separator (brackets, dashes, words,
        // mixed, none) by understanding language rather than enumerating punctuation.
        try
        {
            var cues = await _cueParser.ParseAsync(question, cancellationToken);
            if (cues.HasExplicitColumnRequest && cues.DisplayColumns.Count > 0)
            {
                var cols = string.Join(", ", cues.DisplayColumns.OrderBy(d => d.Order).Select(d => d.Name));
                question = $"{question}\n-- requested columns: {cols}";
            }
            if (cues.GroupingHints.Count > 0)
            {
                question = $"{question}\n-- grouping hints: {string.Join(", ", cues.GroupingHints)}";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[copilot.silent_failure][stage=StructuralCueParser] failed; continuing without cues");
        }
        // 2026-06-01 — REMOVED the phrase-matching "shape classifier fast-route". It used a
        // hardcoded English keyword list (QuestionShapeComplexHints) to predict whether the
        // form-filling QuerySpec could express the question — brittle, English-only, and at
        // odds with the rest of the pipeline (config-driven, multilingual, LLM-for-the-long-
        // tail). Replaced by the coverage-gap → escape-valve RETRY below: we run form-filling,
        // and if the semantic coverage checker (multilingual, LLM-judged) finds the answer
        // doesn't cover the question, THEN we escalate to the raw-SQL escape valve. Detection
        // beats prediction — the verdict is grounded in the actual answer, in any language.

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
        var emptyRoot = extraction.Spec is not null && string.IsNullOrWhiteSpace(extraction.Spec.Root);
        if (extraction.Spec is null || emptyRoot)
        {
            steps.RecordSpecExtractorFailed(stageName, question, extraction, isRefinement, specSw.ElapsedMilliseconds);

            if (extraction.CandidateTables.Count > 0)
            {
                var recoveryResult = await TryEscapeValveAsync(
                    request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
                if (recoveryResult is not null) return recoveryResult;
            }

            var err = extraction.Error ?? "";
            string reply;
            if (err.StartsWith("no candidate tables matched", StringComparison.OrdinalIgnoreCase))
            {
                var idx = err.IndexOf(':');
                var suggestions = idx >= 0 && idx + 1 < err.Length
                    ? err[(idx + 1)..].Trim()
                    : "the configured tables";
                reply = string.Format(Text.NoCandidateTablesTemplate, suggestions);
            }
            else if (err.Contains("schema-knowledge") || err.Contains("embedder"))
            {
                reply = err;
            }
            else
            {
                reply = Text.SpecExtractorFailed;
            }
            return await _persister.PersistAsync(request, totalSw, steps,
                reply: reply,
                error: extraction.Error ?? "spec extraction failed", cancellationToken: cancellationToken);
        }
        steps.RecordSpecExtractorOk(stageName, question, extraction, isRefinement, specSw.ElapsedMilliseconds);

        // Clarification short-circuit
        if (string.Equals(extraction.Spec.Intent, SpecConst.Intent.Clarification, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(extraction.Spec.ClarificationQuestion))
        {
            return await _persister.PersistAsync(request, totalSw, steps,
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
            return await _persister.PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.AccessPolicyRefusalTemplate, accessRefusal),
                error: accessRefusal, cancellationToken: cancellationToken);
        }

        // Compile → Validate → IntentGuard → Execute, with up to N retries on failure.
        var maxRetries = Math.Max(0, _options.Value.MaxSelfCorrectionRetries);
        var spec = extraction.Spec;
        CompiledSql? compiled = null;
        ExecutionResult? execResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            _specEnricher.Enrich(spec);

            // Compile
            try { compiled = _compiler.Compile(spec); }
            catch (Exception ex)
            {
                steps.RecordCompilerFailed(spec, ex.Message, attempt);
                if (await TryRetryWithRefinementAsync(question, spec, ex.Message, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
                if (extraction.CandidateTables.Count > 0)
                {
                    var compileEscape = await TryEscapeValveAsync(
                        request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
                    if (compileEscape is not null) return compileEscape;
                }
                return await _persister.PersistAsync(request, totalSw, steps,
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
                if (await TryRetryWithRefinementAsync(question, spec, msg, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
                return await _persister.PersistAsync(request, totalSw, steps,
                    reply: string.Format(Text.ValidatorRejectedTemplate, msg),
                    sql: compiled.Sql, error: "validation failed", cancellationToken: cancellationToken);
            }
            steps.RecordValidatorOk(compiled, attempt);

            // SqlIntentGuard
            var guardIssue = _sqlIntentGuard.Check(question, spec, compiled.Sql);
            if (guardIssue is not null)
            {
                steps.RecordSqlIntentGuardFailed(question, spec, compiled, guardIssue, attempt);
                var guardHint = guardIssue.RetryHint ?? guardIssue.Reason;
                if (await TryRetryWithRefinementAsync(question, spec, guardHint, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
                return await _persister.PersistAsync(request, totalSw, steps,
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
                && SingleQuestionHelpers.IsSuspiciousEmptyResult(spec))
            {
                var emptyHint = Text.EmptyResultRetryHint;
                steps.RecordRowShapeSanityFailed(SingleQuestionHelpers.DescribeShape(spec), emptyHint, attempt);
                if (await TryRetryWithRefinementAsync(question, spec, emptyHint, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
            }

            if (execResult.Error is null) break;

            if (await TryRetryWithRefinementAsync(question, spec, execResult.Error, attempt, maxRetries, steps, cancellationToken) is { } refinedAfterExec)
            { spec = refinedAfterExec; continue; }

            // Escape valve: form-filling QuerySpec couldn't produce runnable SQL.
            var escapeResult = await TryEscapeValveAsync(
                request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
            if (escapeResult is not null) return escapeResult;

            return await _persister.PersistAsync(request, totalSw, steps,
                reply: string.Format(Text.ExecutorFailedTemplate, execResult.Error),
                sql: compiled.Sql, error: execResult.Error, cancellationToken: cancellationToken);
        }

        var finalCompiled = compiled ?? throw new InvalidOperationException("compiled was null after pipeline loop");
        var finalExec = execResult ?? throw new InvalidOperationException("execResult was null after pipeline loop");

        // Explainer
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepExplainer);
        var explainSw = Stopwatch.StartNew();
        var explanation = await _explainer.ExplainAsync(question, finalExec, finalCompiled, cancellationToken);
        explainSw.Stop();
        steps.RecordExplainer(question, finalCompiled, finalExec, explanation, explainSw.ElapsedMilliseconds);

        _specMemory.Remember(request.ConversationId, spec);

        var chartType = _chartSuggester.Suggest(spec, finalExec);

        var retryFired = steps.Any(s => string.Equals(s.Stage, StageNames.StepSpecRefine, StringComparison.OrdinalIgnoreCase));

        // Skip the coverage check ONLY when the produced spec is trivial AND the QUESTION itself is a
        // genuinely simple COUNT ("how many X"). The spec-shape test alone was unsafe: when a weak
        // model COLLAPSES a complex question into a trivially-shaped wrong SQL (e.g. "top 5 customers
        // by total" → SELECT TOP 5 SUM(...), no GROUP BY), the spec looks trivial and coverage was
        // skipped exactly when it was needed most. Gating on the config-driven 8-shape classifier
        // (TOPN/AGGREGATE/COMPARE/JOIN/etc. never skip) closes that hole. (2026-06-02, issue #1)
        var skipCoverage = _options.Value.SkipCoverageCheckOnTrivialAnswers
            && !retryFired
            && _shapeClassifier.Classify(question) == Prompts.PromptShape.COUNT
            && SingleQuestionHelpers.IsTrivialAnswer(spec, _options.Value.TrivialAnswerMaxFilters, _options.Value.TrivialAnswerMaxAggregations);
        CoverageGap? coverageGap = null;
        long coverageElapsedMs = 0;
        if (!skipCoverage)
        {
            _progress.NotifyStepStarting(TargetFor(request), StageNames.StepCoverageCheck);
            var coverageSw = Stopwatch.StartNew();
            // CheckAsync THROWS only on genuine caller/wall-clock cancellation (by design — see
            // CoverageChecker's catch): that propagates unhandled to abort the torn-down request.
            // Verifier FAILURES (timeout / rate-limit / provider error) do NOT throw — they return a
            // degraded gap (IsDegraded=true) handled by the IsDegraded branches below.
            coverageGap = await _coverageChecker.CheckAsync(
                question, finalCompiled.Sql, finalExec, explanation.Reply, cancellationToken);
            coverageSw.Stop();
            coverageElapsedMs = coverageSw.ElapsedMilliseconds;
        }
        else
        {
            steps.RecordCoverageCheckSkipped(question,
                "Trivial answer (first-attempt single-root, no joins/group-by/multi-filter).");
        }
        var finalReply = explanation.Reply;
        if (coverageGap is not null)
        {
            // A DEGRADED gap means the verifier itself FAILED (timeout / rate-limit) — it is NOT a
            // real coverage gap. Record it distinctly so the trace says "unverified", not "missing X".
            if (coverageGap.IsDegraded)
                steps.RecordCoverageCheckUnverified(question, coverageGap, coverageElapsedMs);
            else
                steps.RecordCoverageCheckIncomplete(question, coverageGap, coverageElapsedMs);

            // Option B (2026-06-01) — coverage-gap → escape-valve RETRY. The semantic coverage
            // checker judged the form-filling answer incomplete — e.g. "regions where ticket
            // count exceeds the average" returned all 37 regions instead of the above-average
            // subset. That signature means the question needs a shape the form-filling QuerySpec
            // structurally CANNOT express (subquery comparison, window function, recursive). The
            // raw-SQL escape valve CAN express those, so escalate ONCE.
            //
            // Why this replaced the old phrase-matching router: the decision is made by the
            // multilingual, LLM-judged coverage check on the ACTUAL answer — detection, not a
            // brittle English keyword guess. Works for Arabic ("أكثر من المتوسط") and the long
            // tail alike. Reaching here implies the form-filling execution SUCCEEDED (a gap, not
            // an error), so the escape valve has not yet run for this question — no double-escape.
            //
            // A DEGRADED gap (2026-06-02) reaches here too: we couldn't confirm completeness, so we
            // still escalate ONCE — the escape valve is the best recovery (it produced the correct
            // CTE in the 158s incident) and it declines gracefully when it can't help.
            if (_options.Value.EnableCoverageEscapeRetry && extraction.CandidateTables.Count > 0)
            {
                var coverageEscape = await TryEscapeValveAsync(
                    request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
                if (coverageEscape is not null) return coverageEscape;
                // Escape valve declined (emitter returned null / SQL failed validation or
                // execution) → keep the form-filling answer, annotated below.
            }

            if (coverageGap.IsDegraded)
            {
                // Verification couldn't run AND the escape valve didn't supersede. Never REFUSE an
                // answer we actually have just because the checker was down — present it, but flag
                // it as unverified so the user knows it was not confirmed complete.
                finalReply = $"⚠ This answer could not be automatically verified for completeness (the verifier was unavailable), so treat it as unconfirmed.\n\n" + explanation.Reply;
            }
            else
            {
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
        }
        else if (!skipCoverage)
        {
            steps.RecordCoverageCheckComplete(question, coverageElapsedMs);
        }

        var prov = retryFired ? "self-corrected" : "llm-cold";
        var conf = retryFired ? 0.6 : 0.85;

        return (await _persister.PersistAsync(request, totalSw, steps,
            reply: finalReply,
            sql: finalCompiled.Sql,
            rowCount: finalExec.RowCount,
            rows: finalExec.Rows,
            chartType: chartType,
            warnings: finalCompiled.Warnings,
            cancellationToken: cancellationToken)) with { Provenance = prov, Confidence = conf };
    }

    // Budget-check + refine in one call; lets the retry-loop call sites stay one line each.
    private Task<QuerySpec?> TryRetryWithRefinementAsync(
        string question, QuerySpec previousSpec, string error,
        int attempt, int maxRetries,
        BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        if (attempt >= maxRetries) return Task.FromResult<QuerySpec?>(null);
        return TryRefineSpecAsync(question, previousSpec, error, steps, cancellationToken);
    }

    private async Task<QuerySpec?> TryRefineSpecAsync(
        string question, QuerySpec previousSpec, string error,
        BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        var refineSw = Stopwatch.StartNew();
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
        if (string.IsNullOrWhiteSpace(retry.Spec.Root) && !string.IsNullOrWhiteSpace(previousSpec.Root))
        {
            retry.Spec.Root = previousSpec.Root;
        }
        if (QuerySpecHasher.Hash(previousSpec) == QuerySpecHasher.Hash(retry.Spec))
        {
            steps.RecordSpecRefineConverged(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
            return null;
        }
        steps.RecordSpecRefineOk(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
        return retry.Spec;
    }

    // Escape valve: ask the LLM for raw T-SQL when the form-filling path can't produce runnable SQL.
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

        // The escape valve's SQL is now the FINAL answer. If a prior form-filling Executor step
        // ran and succeeded (the coverage-gap retry case), mark it SUPERSEDED so the trace doesn't
        // headline the rejected 37-row SQL as "what ran" — the user opened trace 34 and saw the
        // form-filling SQL looking like the answer, when the CTE below actually produced it.
        MarkFormFillingExecutorSuperseded(steps);

        var explanation = await _explainer.ExplainAsync(question, execResult, compiled, cancellationToken);

        return (await _persister.PersistAsync(request, totalSw, steps,
            reply: explanation.Reply,
            sql: compiled.Sql,
            rowCount: execResult.RowCount,
            rows: execResult.Rows,
            warnings: compiled.Warnings,
            cancellationToken: cancellationToken)) with { Provenance = "direct-emit", Confidence = 0.7 };
    }

    // Find the most recent successful form-filling Executor step and downgrade it to Warn with a
    // SUPERSEDED note, so the Investigation tree de-emphasizes the rejected SQL and the escape-
    // valve step (labeled "✓ FINAL ANSWER") reads as the source of the answer.
    private static void MarkFormFillingExecutorSuperseded(BroadcastingStepList steps)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            if (string.Equals(steps[i].Stage, StageNames.StepExecutor, StringComparison.OrdinalIgnoreCase)
                && string.Equals(steps[i].Status, StageNames.StatusOk, StringComparison.OrdinalIgnoreCase))
            {
                steps[i] = steps[i] with
                {
                    Status = StageNames.StatusWarn,
                    Detail = (steps[i].Detail ?? "") +
                             " — ⚠ SUPERSEDED: this form-filling SQL was rejected by the coverage check; " +
                             "the escape-valve raw SQL produced the final answer.",
                };
                return;
            }
        }
    }
}
