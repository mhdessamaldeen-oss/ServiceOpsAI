namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

// Single-question execution: entity-resolution → spec extraction → compile/validate/execute
// loop with retry → explainer → coverage check. Also the spec-retry + direct-emit-escape helpers.
internal sealed partial class CopilotOrchestrator
{
    private async Task<CopilotResponse> RunSingleAsync(
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
            _logger.LogWarning(ex, "[MetadataHandler] failed; continuing");
        }

        // Phase 6 — fuzzy entity resolution. Append canonical-form hints to the question
        // before any downstream stage sees it, so the LLM uses real DB values instead of
        // fabricating placeholders like '<RegionId for Damascus>' or 'RegionIdDamascus'.
        // The hints are a SQL-style comment that the LLM treats as ground-truth context;
        // SpecExtractor / DirectSqlEmitter both prepend the question into their prompts so
        // both paths benefit automatically. Returns empty list when no entities match —
        // zero overhead on the happy path.
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
            // Fail-open: entity resolution is a helper, never a blocker. Log and continue.
            _logger.LogWarning(ex, "[EntityResolver] failed; continuing without hints");
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
            // Fail-open: cue parsing is a helper, never a blocker.
            _logger.LogWarning(ex, "[StructuralCueParser] failed; continuing without cues");
        }

        // Phase 3 Step 12 — shape classifier fast-route. When the question contains strong
        // signals of a window function / running total / rank / percentile (anything the
        // form-filling QuerySpec can't express), skip SpecExtractor + Compiler + retry loop
        // and go straight to the LlmDirectSqlEmitter escape valve. Saves 1-3 wasted LLM
        // calls per such question. Multi-turn refinement bypasses this — refinements
        // always go through SpecExtractor so previous spec context is honored.
        if (_shapeClassifier.Classify(question) == Stages.QuestionShape.ComplexAnalytics
            && _specMemory.Recall(request.ConversationId) is null
            && _schemaRetriever.IsAvailable)
        {
            // Retriever-only probe (no LLM call); the emitter just needs candidate table names.
            var retrieval = await _schemaRetriever.RetrieveAsync(question, _options.Value.RetrieverTopK, cancellationToken);
            var candidateTables = retrieval.Tables.Select(t => t.Table.Name).ToList();
            if (candidateTables.Count > 0)
            {
                var fastResult = await TryEscapeValveAsync(
                    request, totalSw, steps, question, candidateTables, cancellationToken);
                if (fastResult is not null) return fastResult;
            }
            // Escape valve declined — fall through to the form-filling path.
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
        // Initial spec is null OR has empty Root → treat as failed extraction. Escape valve gets a fresh shot at producing real SQL; only fall back to patching when escape valve declines.
        var emptyRoot = extraction.Spec is not null && string.IsNullOrWhiteSpace(extraction.Spec.Root);
        if (extraction.Spec is null || emptyRoot)
        {
            steps.RecordSpecExtractorFailed(stageName, question, extraction, isRefinement, specSw.ElapsedMilliseconds);

            // Empty-spec recovery — when SpecExtractor couldn't produce a spec but DID retrieve
            // candidate tables, route the question through the direct-SQL emitter (same escape
            // valve used for complex-analytics shapes). The output still passes the SAME
            // validator + read-only executor chain, so safety invariants hold. Diagnosed from
            // session 3: 12 of 52 failures ("Compiler: unknown root table ''") had valid
            // candidate tables but the form-filling extractor collapsed — the direct emitter
            // can often produce working SQL from the same retrieval context.
            if (extraction.CandidateTables.Count > 0)
            {
                var recoveryResult = await TryEscapeValveAsync(
                    request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
                if (recoveryResult is not null) return recoveryResult;
            }

            // When the extraction error names a specific cause (no schema, no embedder, no
            // candidate tables), surface a helpful reply rather than the raw error. The
            // no-candidate-tables case carries the top-K table names in the error string —
            // we lift them out and pass them into NoCandidateTablesTemplate so the user gets
            // an actionable suggestion ("did you mean: X, Y, Z?") instead of a bare error.
            var err = extraction.Error ?? "";
            string reply;
            if (err.StartsWith("no candidate tables matched", StringComparison.OrdinalIgnoreCase))
            {
                // err looks like: "no candidate tables matched. Available: T1, T2, T3"
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
                if (await TryRetryWithRefinementAsync(question, spec, ex.Message, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
                // Last-resort escape valve: spec had an unrecoverable structural problem (e.g., empty root) — try direct SQL emission against the same candidate tables.
                if (extraction.CandidateTables.Count > 0)
                {
                    var compileEscape = await TryEscapeValveAsync(
                        request, totalSw, steps, question, extraction.CandidateTables, cancellationToken);
                    if (compileEscape is not null) return compileEscape;
                }
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
                if (await TryRetryWithRefinementAsync(question, spec, msg, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
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
                var guardHint = guardIssue.RetryHint ?? guardIssue.Reason;
                if (await TryRetryWithRefinementAsync(question, spec, guardHint, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
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
                && IsSuspiciousEmptyResult(spec))
            {
                var emptyHint = Text.EmptyResultRetryHint;
                steps.RecordRowShapeSanityFailed(DescribeShape(spec), emptyHint, attempt);
                if (await TryRetryWithRefinementAsync(question, spec, emptyHint, attempt, maxRetries, steps, cancellationToken) is { } refined)
                { spec = refined; continue; }
            }

            if (execResult.Error is null) break;

            if (await TryRetryWithRefinementAsync(question, spec, execResult.Error, attempt, maxRetries, steps, cancellationToken) is { } refinedAfterExec)
            { spec = refinedAfterExec; continue; }

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

        // Loop is exhaustive — only path out is via `break` on a successful execute.
        var finalCompiled = compiled ?? throw new InvalidOperationException("compiled was null after pipeline loop");
        var finalExec = execResult ?? throw new InvalidOperationException("execResult was null after pipeline loop");

        // Explainer: LLM generates a natural-language reply over the result rows.
        _progress.NotifyStepStarting(TargetFor(request), StageNames.StepExplainer);
        var explainSw = Stopwatch.StartNew();
        var explanation = await _explainer.ExplainAsync(question, finalExec, finalCompiled, cancellationToken);
        explainSw.Stop();
        steps.RecordExplainer(question, finalCompiled, finalExec, explanation, explainSw.ElapsedMilliseconds);

        // Remember the successful spec for the next turn's refinement.
        _specMemory.Remember(request.ConversationId, spec);

        var chartType = _chartSuggester.Suggest(spec, finalExec);

        // Used here to gate coverage-check skipping and below for provenance.
        var retryFired = steps.Any(s => string.Equals(s.Stage, StageNames.StepSpecRefine, StringComparison.OrdinalIgnoreCase));

        // Skipped on trivial first-attempt answers; runs on joined/grouped/multi-filter or retry-recovered specs.
        var skipCoverage = _options.Value.SkipCoverageCheckOnTrivialAnswers
            && !retryFired
            && IsTrivialAnswer(spec);
        CoverageGap? coverageGap = null;
        long coverageElapsedMs = 0;
        if (!skipCoverage)
        {
            _progress.NotifyStepStarting(TargetFor(request), StageNames.StepCoverageCheck);
            var coverageSw = Stopwatch.StartNew();
            coverageGap = await _coverageChecker.CheckAsync(
                question, finalCompiled.Sql, finalExec, explanation.Reply, cancellationToken);
            coverageSw.Stop();
            coverageElapsedMs = coverageSw.ElapsedMilliseconds;
        }
        else
        {
            // Record the skip so the trace UI doesn't show a missing step.
            steps.RecordCoverageCheckSkipped(question,
                "Trivial answer (first-attempt single-root, no joins/group-by/multi-filter).");
        }
        var finalReply = explanation.Reply;
        if (coverageGap is not null)
        {
            steps.RecordCoverageCheckIncomplete(question, coverageGap, coverageElapsedMs);
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
        else if (!skipCoverage)
        {
            steps.RecordCoverageCheckComplete(question, coverageElapsedMs);
        }

        // Provenance: "self-corrected" if any retry fired during the compile/validate/execute loop, otherwise "llm-cold".
        var prov = retryFired ? "self-corrected" : "llm-cold";
        var conf = retryFired ? 0.6 : 0.85;

        return (await PersistAsync(request, totalSw, steps,
            reply: finalReply,
            sql: finalCompiled.Sql,
            rowCount: finalExec.RowCount,
            rows: finalExec.Rows,
            chartType: chartType,
            warnings: finalCompiled.Warnings,
            cancellationToken: cancellationToken)) with { Provenance = prov, Confidence = conf };
    }

    // Suspicious-empty: top-N, aggregate-without-group, or filtered list (anti-joins excepted).
    private static bool IsSuspiciousEmptyResult(QuerySpec spec)
    {
        if (spec.Limit is > 0) return true;
        if (spec.Aggregations.Count > 0 && spec.GroupBy.Count == 0) return true;
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

    // Single-root, no-join, ≤1-filter/≤1-aggregation spec — the CoverageChecker's two failure modes (compound half-answers, wrong-FK joins) can't manifest here.
    private static bool IsTrivialAnswer(QuerySpec spec)
    {
        if (spec.Joins.Count > 0) return false;
        if (spec.GroupBy.Count > 0) return false;
        if (spec.Filters.Count > 1) return false;
        if (spec.Aggregations.Count > 1) return false;
        return true;
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
        // Small jittered backoff before re-prompting the LLM. Avoids thundering-herd retries and gives transient provider blips a moment to recover.
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
        // Guard: refinement that empties Root makes things WORSE (compile throws "unknown root").
        // Keep the previous Root in that case — the rest of the refined spec may still be useful.
        if (string.IsNullOrWhiteSpace(retry.Spec.Root) && !string.IsNullOrWhiteSpace(previousSpec.Root))
        {
            retry.Spec.Root = previousSpec.Root;
        }
        // Convergence: identical hash means LLM re-emitted the rejected spec — stop the loop.
        if (QuerySpecHasher.Hash(previousSpec) == QuerySpecHasher.Hash(retry.Spec))
        {
            steps.RecordSpecRefineConverged(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
            return null;
        }
        steps.RecordSpecRefineOk(question, previousSpec, error, retry, refineSw.ElapsedMilliseconds);
        return retry.Spec;
    }

    // Escape valve: ask the LLM for raw T-SQL when the form-filling path can't produce runnable SQL. Output still passes the SAME validator + read-only executor.
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
            warnings: compiled.Warnings,
            cancellationToken: cancellationToken)) with { Provenance = "direct-emit", Confidence = 0.7 };
    }
}
