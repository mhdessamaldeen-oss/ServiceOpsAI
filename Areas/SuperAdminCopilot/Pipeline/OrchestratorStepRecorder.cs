namespace SuperAdminCopilot.Pipeline;

using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;
using SuperAdminCopilot.Retrieval;

/// <summary>
/// Extension methods on <see cref="BroadcastingStepList"/> that collapse the orchestrator's
/// per-stage trace-recording into one-liners. Each method takes the minimum data the stage
/// produces and internally constructs the <see cref="PipelineStep"/> + structured
/// <see cref="StepPayload"/> (input / output / reason / details) the investigation UI consumes.
/// </summary>
/// <remarks>
/// <para>Why extension methods and not a Visitor: we have ONE algorithm (record into the list,
/// which broadcasts via the BroadcastingStepList). Visitor's payoff is many algorithms over a
/// shared element hierarchy — that's not what's happening here. This recorder gives the same
/// type-safe per-stage dispatch with ~10× less ceremony than the full GoF Visitor.</para>
/// <para>All methods statically type the receiver as <see cref="BroadcastingStepList"/> so
/// the C# compiler binds to the broadcasting <c>Add</c> override (<c>List&lt;T&gt;.Add</c> isn't
/// virtual; the BroadcastingStepList uses <c>new</c> hiding which only kicks in for the concrete
/// static type).</para>
/// </remarks>
internal static class OrchestratorStepRecorder
{
    // ── Small helpers ─────────────────────────────────────────────────────────────────

    private static PipelineStep Step(string stage, string status, long elapsedMs, string? detail,
        string? technical, string? kind = null, IReadOnlyList<PipelineStep>? subSteps = null) =>
        new(stage, status, elapsedMs, DateTime.UtcNow, detail, technical, kind, subSteps);

    private static string AttemptLabel(int attempt) => attempt == 0 ? "" : $" (retry {attempt})";

    private static string CostString(decimal? c) => c is null ? "n/a"
        : c.Value == 0 ? "free"
        : c.Value >= 0.01m ? $"${c.Value:F4}" : $"${c.Value:F6}";

    // ── Fast-path branches ─────────────────────────────────────────────────────────────

    // ── Pre-rail gates ────────────────────────────────────────────────────────────────

    public static void RecordOperationalGuardRefusal(this BroadcastingStepList steps, string question, string reason) =>
        steps.Add(Step(StageNames.StepOperationalGuard, StageNames.StatusFailed, 0,
            $"refused: {reason}",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: question,
                output: $"REFUSED: {reason}",
                reason: "Per-session retry-budget / cost-gate refusing further LLM calls in this conversation.")));

    public static void RecordOperationalGuardPassed(this BroadcastingStepList steps, string question) =>
        steps.Add(Step(StageNames.StepOperationalGuard, StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: question,
                output: "PASSED",
                reason: "Per-session retry-budget / cost-gate OK — safe to continue.")));

    public static void RecordWriteIntentRefusal(this BroadcastingStepList steps, string question, WriteIntentResult result) =>
        steps.Add(Step(StageNames.StepWriteIntentGuard, StageNames.StatusFailed, 0,
            $"refused: {result.MatchedVerb} ({result.Language})",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: question,
                output: $"REFUSED: {result.Reason}",
                reason: $"Deterministic preflight detected a write verb. The pipeline is read-only by design — no LLM call needed to reject this.",
                details: new { matchedVerb = result.MatchedVerb, language = result.Language, fullReason = result.Reason })));

    public static void RecordOutOfScopeRefusal(this BroadcastingStepList steps, string question, OutOfScopeResult result) =>
        steps.Add(Step(StageNames.StepOutOfScopeGuard, StageNames.StatusFailed, 0,
            $"refused: {result.MatchedPattern} ({result.Language})",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: question,
                output: $"REFUSED: {result.Reason}",
                reason: "Deterministic preflight detected a non-data question (philosophical / opinion / prediction / ethics-religion). The copilot answers from the database only — no LLM call needed.",
                details: new { matchedPattern = result.MatchedPattern, language = result.Language, fullReason = result.Reason })));

    public static void RecordWriteIntentPassed(this BroadcastingStepList steps, string question) =>
        steps.Add(Step(StageNames.StepWriteIntentGuard, StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: question,
                output: "PASSED",
                reason: "No write verbs (delete / drop / insert / update / alter / create-table / truncate) detected in the question — safe to continue.")));

    public static void RecordConversational(this BroadcastingStepList steps, string question, ConversationalReply reply) =>
        steps.Add(Step(StageNames.StepConversational, StageNames.StatusOk, 0, $"{reply.Kind}",
            technical: StepPayload.Of(StepPayloadKinds.Branch,
                input: question,
                output: reply.Reply,
                reason: $"Question matched the '{reply.Kind}' conversational pattern (greeting / meta-question / thanks). Pipeline short-circuits here — no LLM or SQL needed.")));

    public static void RecordKnowledgeMatch(this BroadcastingStepList steps, string question, KnowledgeMatchResult reply) =>
        steps.Add(Step(StageNames.StepKnowledgeMatch, StageNames.StatusOk, 0, $"term={reply.Term}",
            technical: StepPayload.Of(StepPayloadKinds.Branch,
                input: question,
                output: reply.Reply,
                reason: $"Question matched the knowledge term '{reply.Term}' from the FAQ dictionary. Answered from the curated definition — no LLM call.")));

    public static void RecordSemanticSearchHit(this BroadcastingStepList steps, string question,
        SemanticSearchHandlerResult result, long elapsedMs) =>
        steps.Add(Step(StageNames.StepSemanticSearch, StageNames.StatusOk, elapsedMs,
            $"{result.Mode}: {result.Result.RowCount} hit(s)",
            technical: StepPayload.Of(StepPayloadKinds.Branch,
                input: question,
                output: result.Result.RowCount > 0
                    ? $"{result.Result.RowCount} hit(s); top match: {result.Hits.FirstOrDefault()?.DisplayLabel ?? "(no preview)"}"
                    : "no hits above threshold",
                reason: $"Vector-similarity search ran in '{result.Mode}' mode. The question referenced an indexed entity (by id or by topic) so we resolved it directly instead of generating SQL.",
                details: new
                {
                    mode = result.Mode,
                    rowCount = result.Result.RowCount,
                    hits = result.Hits.Take(5).Select(h => new {
                        entityType = h.EntityType,
                        naturalKey = h.NaturalKey,
                        label = h.DisplayLabel,
                        score = h.Score
                    }),
                    sql = result.Sql
                })));

    public static void RecordToolDispatch(this BroadcastingStepList steps, string question,
        ToolHandlerResult result, long elapsedMs)
    {
        var firstRow = result.Result.Rows?.FirstOrDefault();
        steps.Add(Step(StageNames.StepToolDispatch,
            result.Result.Error is null ? StageNames.StatusOk : StageNames.StatusFailed,
            elapsedMs,
            $"{result.ToolKey} ({result.ToolTitle}): {result.Result.RowCount} row(s)",
            technical: StepPayload.Of(StepPayloadKinds.ToolDispatch,
                input: question,
                output: result.Result.Error is null
                    ? $"{result.ToolKey} returned {result.Result.RowCount} row(s)"
                    : $"{result.ToolKey} errored: {result.Result.Error}",
                reason: !string.IsNullOrWhiteSpace(result.ClarificationQuestion)
                    ? $"Tool '{result.ToolKey}' matched but is missing a required parameter — asking the user for it before dispatch."
                    : $"Question matched registered tool '{result.ToolKey}' ({result.ToolTitle}). Delegated to the tool instead of writing SQL.",
                details: new
                {
                    toolKey = result.ToolKey,
                    toolTitle = result.ToolTitle,
                    rowCount = result.Result.RowCount,
                    firstRow,
                    clarification = result.ClarificationQuestion,
                    error = result.Result.Error,
                    sqlComment = result.Sql
                }),
            kind: StageNames.KindToolDispatch));
    }

    // Step 14 escape valve — record the LLM-direct-SQL fallback. Fires only on the path
    // where form-filling exhausted retries and the emitter produced a runnable SQL.
    public static void RecordLlmDirectSql(this BroadcastingStepList steps, string question,
        string sql, long elapsedMs) =>
        steps.Add(Step(StageNames.StepLlmDirectSql, StageNames.StatusOk, elapsedMs,
            "raw SQL emitted (form-filling exhausted)",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: question,
                output: sql,
                reason: "Form-filling QuerySpec couldn't produce runnable SQL after retries. Asked the LLM to emit T-SQL directly. Output still passes the same Validator + read-only Executor guards.",
                details: new { sql }),
            kind: StageNames.KindLlmCall));

    public static void RecordVerifiedQuery(this BroadcastingStepList steps, string question,
        VerifiedMatch match, long elapsedMs)
    {
        var provenance = !string.IsNullOrWhiteSpace(match.Query.VerifiedBy)
            ? $" verifiedBy={match.Query.VerifiedBy}"
            : string.Empty;
        var when = !string.IsNullOrWhiteSpace(match.Query.VerifiedAt)
            ? $" verifiedAt={match.Query.VerifiedAt}"
            : string.Empty;
        steps.Add(Step(StageNames.StepVerifiedQuery, StageNames.StatusOk, elapsedMs,
            $"matched id={match.Query.Id} sim={match.Similarity:F2}{provenance}{when}",
            technical: StepPayload.Of(StepPayloadKinds.Branch,
                input: question,
                output: match.Query.Sql,
                reason: $"Question's embedding cosine-matched verified question id={match.Query.Id} at similarity {match.Similarity:F3} (above the configured threshold). Using the curated SQL directly — skipping the LLM entirely.",
                details: new
                {
                    verifiedId = match.Query.Id,
                    verifiedQuestion = match.Query.Question,
                    similarity = match.Similarity,
                    verifiedBy = match.Query.VerifiedBy,
                    verifiedAt = match.Query.VerifiedAt,
                    shape = match.Query.Shape,
                    sql = match.Query.Sql
                })));
    }

    // ── Decomposer ────────────────────────────────────────────────────────────────────

    public static void RecordDecomposerSplit(this BroadcastingStepList steps, string question,
        IReadOnlyList<string> subQuestions, string source, string? llmPrompt, string? llmRaw, long elapsedMs) =>
        steps.Add(Step(StageNames.StepDecomposer, StageNames.StatusOk, elapsedMs,
            $"split into {subQuestions.Count} sub-questions",
            technical: StepPayload.Of(StepPayloadKinds.Decompose,
                input: llmPrompt ?? question,
                output: llmRaw ?? string.Join("\n", subQuestions.Select((s, i) => $"Q{i + 1}: {s}")),
                reason: $"Question is compound — split via {source}. {subQuestions.Count} independent sub-questions will run in parallel, each its own SpecExtractor → Compiler → Executor → Explainer pipeline.",
                details: new { count = subQuestions.Count, subQuestions = subQuestions.ToArray(), source })));

    public static void RecordDecomposerAtomic(this BroadcastingStepList steps, string question,
        string source, string? llmPrompt, string? llmRaw, long elapsedMs) =>
        steps.Add(Step(StageNames.StepDecomposer, StageNames.StatusOk, elapsedMs,
            "atomic — single SELECT path",
            technical: StepPayload.Of(StepPayloadKinds.Decompose,
                input: llmPrompt ?? question,
                output: llmRaw ?? "(no LLM call — regex returned single segment)",
                reason: $"Decomposer (source: {source}) found no compound structure. Question stays atomic; runs as a single SELECT through the SpecExtractor → Compiler → Executor → Explainer pipeline.",
                details: new { source })));

    public static void RecordSubQuestion(this BroadcastingStepList steps, int index, string sub, CopilotResponse response) =>
        steps.Add(Step($"{StageNames.StepSubQuestion} {index + 1}",
            string.IsNullOrEmpty(response.Error) ? StageNames.StatusOk : StageNames.StatusFailed,
            0, $"Q{index + 1}: \"{Internal.CopilotStrings.Truncate(sub, 60)}\" → {response.RowCount ?? 0} row(s)",
            technical: StepPayload.Of(StepPayloadKinds.SubQuestion,
                input: sub,
                output: response.Reply,
                reason: string.IsNullOrEmpty(response.Error)
                    ? $"One branch of the parallel fan-out completed: {response.RowCount ?? 0} row(s)."
                    : $"This sub-question failed: {response.Error}",
                details: new
                {
                    index = index + 1,
                    rowCount = response.RowCount,
                    sql = response.Sql,
                    error = response.Error,
                    steps = response.Steps?.Count ?? 0
                })));

    // ── SpecExtractor (initial + refine) ──────────────────────────────────────────────

    public static void RecordSpecExtractorFailed(this BroadcastingStepList steps, string stageName,
        string question, SpecExtractionResult extraction, bool isRefinement, long elapsedMs) =>
        steps.Add(Step(stageName, StageNames.StatusFailed, elapsedMs, extraction.Error ?? "no spec",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: extraction.Prompt ?? question,
                output: extraction.RawLlmOutput ?? "(no output)",
                reason: $"The form-filling LLM call failed to produce a valid QuerySpec. Error: {extraction.Error ?? "unknown"}",
                details: new
                {
                    mode = isRefinement ? "refinement" : "fresh",
                    candidateTables = extraction.CandidateTables,
                    error = extraction.Error
                })));

    public static void RecordSpecExtractorOk(this BroadcastingStepList steps, string stageName,
        string question, SpecExtractionResult extraction, bool isRefinement, long elapsedMs) =>
        steps.Add(Step(stageName, StageNames.StatusOk, elapsedMs,
            $"root={extraction.Spec!.Root}, candidates=[{string.Join(",", extraction.CandidateTables)}]",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: extraction.Prompt ?? question,
                output: extraction.RawLlmOutput ?? "(empty)",
                reason: isRefinement
                    ? "Refinement mode: previous QuerySpec exists on this conversation and the new question looks like a follow-up. The LLM was asked to MODIFY the previous spec rather than start fresh."
                    : "Fresh extraction: no previous spec or no refinement signal. The LLM filled a QuerySpec form from the question + retrieved schema slice.",
                details: new
                {
                    mode = isRefinement ? "refinement" : "fresh",
                    candidateTables = extraction.CandidateTables,
                    specRoot = extraction.Spec.Root,
                    spec = extraction.Spec,
                    // SpecRepair phase mutations — one entry per phase that fired and reported
                    // a diagnostic. The investigation page renders this as "Auto-fixes ran:
                    // AutoQualifyColumns: qualified 3 refs; EnsureDisplayColumns: +5 columns".
                    // Operators see what the pipeline corrected without grepping the log.
                    repairDiagnostics = extraction.RepairDiagnostics?.Select(d => new
                    {
                        phase = d.PhaseName,
                        detail = d.Detail,
                    }).ToList()
                }),
            kind: StageNames.KindLlmCall));

    public static void RecordSpecRefineFailed(this BroadcastingStepList steps, string question,
        QuerySpec previousSpec, string error, SpecExtractionResult retry, long elapsedMs) =>
        steps.Add(Step(StageNames.StepSpecRefine, StageNames.StatusFailed, elapsedMs, retry.Error ?? "no spec",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: $"Question: {question}\nPrevious spec error: {error}\nPrevious spec: {System.Text.Json.JsonSerializer.Serialize(previousSpec)}",
                output: retry.RawLlmOutput ?? "(no output)",
                reason: $"The retry-with-error LLM call failed to produce a fixed QuerySpec. Underlying error: {retry.Error ?? "unknown"}.",
                details: new { error = retry.Error, previousError = error })));

    /// <summary>
    /// Records a SpecRefine attempt that produced a spec STRUCTURALLY IDENTICAL to the
    /// previous one — the LLM converged. We surface this as a successful step (not failed:
    /// nothing went wrong) but with a clear reason so the user understands the pipeline
    /// stopped retrying because further retries would be wasted, not because of an error.
    /// </summary>
    public static void RecordSpecRefineConverged(this BroadcastingStepList steps, string question,
        QuerySpec previousSpec, string error, SpecExtractionResult retry, long elapsedMs) =>
        steps.Add(Step(StageNames.StepSpecRefine, StageNames.StatusOk, elapsedMs,
            "converged — refined spec identical to previous",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: $"Question: {question}\nPrevious spec error: {error}",
                output: retry.RawLlmOutput ?? "(empty)",
                reason: "Structural hash of the refined spec matched the previous attempt — the LLM produced the same answer it just had rejected. Further retries would burn tokens without progress, so the pipeline accepts the previous outcome and stops the retry loop.",
                details: new
                {
                    convergenceHash = QuerySpecHasher.Hash(previousSpec),
                    previousError = error
                }),
            kind: StageNames.KindLlmCall));

    public static void RecordSpecRefineOk(this BroadcastingStepList steps, string question,
        QuerySpec previousSpec, string error, SpecExtractionResult retry, long elapsedMs) =>
        steps.Add(Step(StageNames.StepSpecRefine, StageNames.StatusOk, elapsedMs,
            $"refined after: {error[..Math.Min(80, error.Length)]}",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: $"Question: {question}\nPrevious spec error: {error}\nPrevious spec: {System.Text.Json.JsonSerializer.Serialize(previousSpec)}",
                output: retry.RawLlmOutput ?? "(empty)",
                reason: $"Previous attempt failed with: {error}. The LLM was given the error + previous spec and produced a fixed spec.",
                details: new { previousError = error, refinedSpec = retry.Spec }),
            kind: StageNames.KindLlmCall));

    // ── Access policy ────────────────────────────────────────────────────────────────

    public static void RecordAccessPolicy(this BroadcastingStepList steps, QuerySpec spec, string? refusal)
    {
        var allTables = new[] { spec.Root }
            .Concat(spec.Joins.Select(j => j.Table))
            .Distinct()
            .ToArray();
        if (refusal is not null)
        {
            steps.Add(Step(StageNames.StepAccessPolicy, StageNames.StatusFailed, 0, refusal,
                technical: StepPayload.Of(StepPayloadKinds.Gate,
                    input: $"Tables: [{string.Join(", ", allTables)}]; columns: [{string.Join(", ", spec.Select)}]",
                    output: $"REFUSED: {refusal}",
                    reason: "Access policy blocks one or more of the tables or columns referenced in the spec. The pipeline aborts BEFORE compiling SQL so a forbidden column never even appears in a generated query.",
                    details: new { refusalReason = refusal, tables = allTables, columns = spec.Select })));
            return;
        }
        steps.Add(Step(StageNames.StepAccessPolicy, StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: $"Tables: [{string.Join(", ", allTables)}]; columns: [{string.Join(", ", spec.Select)}]",
                output: "PASSED",
                reason: "None of the referenced tables or columns are on the admin's blocklist (no PasswordHash, ApiKey, Secret, etc. exposure).",
                details: new { tables = allTables, columns = spec.Select })));
    }

    // ── Compiler / Validator / SqlIntentGuard / Executor (in the retry loop) ───────

    public static void RecordCompilerFailed(this BroadcastingStepList steps, QuerySpec spec, string error, int attempt) =>
        steps.Add(Step($"{StageNames.StepCompiler}{AttemptLabel(attempt)}", StageNames.StatusFailed, 0, error,
            technical: StepPayload.Of(StepPayloadKinds.SqlCompile,
                input: System.Text.Json.JsonSerializer.Serialize(spec),
                output: $"FAILED: {error}",
                reason: "The deterministic spec → SQL compiler threw. Usually a malformed spec (unknown column, invalid join). Retrying with the error fed back to the LLM.",
                details: new { error, attempt })));

    public static void RecordCompilerOk(this BroadcastingStepList steps, QuerySpec spec, CompiledSql compiled, int attempt) =>
        steps.Add(Step($"{StageNames.StepCompiler}{AttemptLabel(attempt)}", StageNames.StatusOk, 0, $"SQL length: {compiled.Sql.Length}",
            technical: StepPayload.Of(StepPayloadKinds.SqlCompile,
                input: System.Text.Json.JsonSerializer.Serialize(spec),
                output: compiled.Sql,
                reason: "QuerySpec compiled deterministically into parameterised T-SQL. Applied: join graph resolution, soft-delete filter (where present), MaxRows cap, FK label projection.",
                details: new { sqlLength = compiled.Sql.Length, paramCount = compiled.Parameters.Count, attempt })));

    public static void RecordValidatorFailed(this BroadcastingStepList steps, CompiledSql compiled,
        IReadOnlyList<string> errors, int attempt) =>
        steps.Add(Step($"{StageNames.StepValidator}{AttemptLabel(attempt)}", StageNames.StatusFailed, 0,
            string.Join("; ", errors),
            technical: StepPayload.Of(StepPayloadKinds.SqlValidate,
                input: compiled.Sql,
                output: $"REJECTED: {string.Join("; ", errors)}",
                reason: "ScriptDom AST walk rejected the compiled SQL. Trips on DML, multi-statement scripts, or disallowed functions. Retrying with the error fed back to the LLM.",
                details: new { errors, attempt })));

    public static void RecordValidatorOk(this BroadcastingStepList steps, CompiledSql compiled, int attempt) =>
        steps.Add(Step($"{StageNames.StepValidator}{AttemptLabel(attempt)}", StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.SqlValidate,
                input: compiled.Sql,
                output: "PASSED",
                reason: "ScriptDom AST walk found no DML, no multi-statement scripts, no disallowed functions. Safe to execute against the read-only connection.",
                details: new { sqlLength = compiled.Sql.Length, attempt })));

    public static void RecordSqlIntentGuardFailed(this BroadcastingStepList steps, string question,
        QuerySpec spec, CompiledSql compiled, SqlIntentGuardResult issue, int attempt) =>
        steps.Add(Step($"{StageNames.StepSqlIntentGuard}{AttemptLabel(attempt)}", StageNames.StatusFailed, 0, issue.Reason,
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: $"Question: {question}\nSpec.Intent: {spec.Intent}\nSQL: {compiled.Sql}",
                output: $"REJECTED: {issue.Reason}",
                reason: "Spec-shape sanity check failed. Catches issues the syntactic validator misses (TOP-N without ORDER BY, anti-join with redundant filter, COUNT for a list question, etc.). Retrying with the hint fed back.",
                details: new { reason = issue.Reason, retryHint = issue.RetryHint, attempt })));

    public static void RecordSqlIntentGuardOk(this BroadcastingStepList steps, string question, QuerySpec spec, int attempt) =>
        steps.Add(Step($"{StageNames.StepSqlIntentGuard}{AttemptLabel(attempt)}", StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.Gate,
                input: $"Question: {question}\nSpec.Intent: {spec.Intent}",
                output: "PASSED",
                reason: "Spec shape (intent / TOP-N / aggregation / filters) matches what the question asked for. SQL is intent-aligned and safe to execute.",
                details: new { intent = spec.Intent, attempt })));

    public static void RecordExecutor(this BroadcastingStepList steps, CompiledSql compiled,
        ExecutionResult result, long elapsedMs, int attempt) =>
        steps.Add(Step($"{StageNames.StepExecutor}{AttemptLabel(attempt)}",
            result.Error is null ? StageNames.StatusOk : StageNames.StatusFailed,
            elapsedMs,
            $"{result.RowCount} row(s){(result.Error is null ? "" : $", error: {result.Error}")}",
            technical: StepPayload.Of(StepPayloadKinds.SqlExecution,
                input: compiled.Sql,
                output: result.Error is null
                    ? $"{result.RowCount} row(s) returned. Columns: {(result.Rows?.FirstOrDefault() is { } fr ? string.Join(", ", fr.Keys) : "(none)")}"
                    : $"FAILED: {result.Error}",
                reason: result.Error is null
                    ? $"SQL executed against the read-only DB connection. {result.RowCount} row(s) returned in {elapsedMs}ms."
                    : $"SQL failed at runtime — feeding the error back to the LLM for retry. Error: {result.Error}",
                details: new
                {
                    rowCount = result.RowCount,
                    columns = result.Rows?.FirstOrDefault()?.Keys,
                    firstRow = result.Rows?.FirstOrDefault(),
                    error = result.Error,
                    elapsedMs,
                    attempt
                }),
            kind: StageNames.KindSqlExecution));

    public static void RecordRowShapeSanityFailed(this BroadcastingStepList steps, string shape, string hint, int attempt) =>
        steps.Add(Step($"{StageNames.StepRowShapeSanity}{AttemptLabel(attempt)}", StageNames.StatusFailed, 0,
            $"empty result for {shape} — refining",
            technical: StepPayload.Of(StepPayloadKinds.ShapeCheck,
                input: $"Shape: {shape}; row count: 0",
                output: $"FLAGGED — retrying with hint: {hint}",
                reason: $"A '{shape}' query returning 0 rows is suspicious — almost always a sign of a spurious filter the LLM added. Retrying with a hint instead of accepting the empty result.",
                details: new { shape, attempt, hint })));

    // ── Coverage Check (post-Explainer verification) ─────────────────────────────────

    public static void RecordCoverageCheckComplete(this BroadcastingStepList steps, string question, long elapsedMs) =>
        steps.Add(Step(StageNames.StepCoverageCheck, StageNames.StatusOk, elapsedMs, "COMPLETE",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: question,
                output: "COMPLETE",
                reason: "Post-Explainer verification: the LLM auditor confirmed the reply fully addresses the question. No coverage gap detected."),
            kind: StageNames.KindLlmCall));

    public static void RecordCoverageCheckIncomplete(this BroadcastingStepList steps, string question,
        CoverageGap gap, long elapsedMs) =>
        steps.Add(Step(StageNames.StepCoverageCheck, StageNames.StatusWarn, elapsedMs,
            $"MISSING: {gap.Missing}",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: gap.Prompt ?? question,
                output: gap.RawLlmOutput ?? $"MISSING: {gap.Missing}",
                reason: "Post-Explainer verification flagged coverage gaps. The reply has been prefixed with a warning so the user sees the missing aspects explicitly. The pipeline doesn't retry — visibility is the win.",
                details: new { missingAspects = gap.Missing }),
            kind: StageNames.KindLlmCall));

    /// <summary>Records that the coverage check was deliberately skipped (trivial-answer fast-path).</summary>
    public static void RecordCoverageCheckSkipped(this BroadcastingStepList steps, string question, string reason) =>
        steps.Add(Step(StageNames.StepCoverageCheck, StageNames.StatusOk, elapsedMs: 0L,
            "SKIPPED",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: question,
                output: "SKIPPED",
                reason: reason),
            kind: StageNames.KindLlmCall));

    // ── Explainer ─────────────────────────────────────────────────────────────────────

    public static void RecordExplainer(this BroadcastingStepList steps, string question,
        CompiledSql compiled, ExecutionResult exec, ExplainerResult explanation, long elapsedMs)
    {
        var llmSub = explanation.SubSteps.FirstOrDefault(s => string.Equals(s.Kind, "llm-call", StringComparison.OrdinalIgnoreCase));
        steps.Add(Step(StageNames.StepExplainer, StageNames.StatusOk, elapsedMs,
            $"{explanation.Reply.Length} chars",
            technical: StepPayload.Of(StepPayloadKinds.LlmCall,
                input: $"Question: {question}\nSQL: {compiled.Sql}\nRow count: {exec.RowCount}",
                output: explanation.Reply,
                reason: llmSub is null
                    ? "Templated explainer used (LLM disabled, or trivial result, or budget exhausted). The reply is the data table itself with a deterministic preamble."
                    : "LLM explainer summarised the result rows into one short paragraph. The data table is appended to the reply so the user can verify against the prose.",
                details: new
                {
                    replyLength = explanation.Reply.Length,
                    subStepCount = explanation.SubSteps.Count,
                    usedLlm = llmSub is not null
                }),
            kind: StageNames.KindLlmCall,
            subSteps: explanation.SubSteps));
    }
}
