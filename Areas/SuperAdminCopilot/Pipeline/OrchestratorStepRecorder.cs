namespace SuperAdminCopilot.Pipeline;

using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Stages;

/// <summary>
/// Extension methods on <see cref="BroadcastingStepList"/> that collapse the orchestrator's
/// per-stage trace-recording into one-liners. Each method takes the minimum data the stage
/// produces and internally constructs the <see cref="PipelineStep"/> + structured
/// <see cref="StepPayload"/> (input / output / reason / details) the investigation UI consumes.
/// </summary>
/// <remarks>
/// Trimmed 2026-06-02 to the lean analyst pipeline: the form-filling recorders (SpecExtractor /
/// SpecRefine / Compiler / SqlIntentGuard / AccessPolicy / CoverageCheck / VerifiedQuery) were
/// deleted along with the QuerySpec machinery they traced. What remains records the gates, the
/// fast-path branches, the decomposer, and the validate/execute/explain steps of the live path.
/// </remarks>
internal static class OrchestratorStepRecorder
{
    // ── Small helpers ─────────────────────────────────────────────────────────────────

    private static PipelineStep Step(string stage, string status, long elapsedMs, string? detail,
        string? technical, string? kind = null, IReadOnlyList<PipelineStep>? subSteps = null) =>
        new(stage, status, elapsedMs, DateTime.UtcNow, detail, technical, kind, subSteps);

    private static string AttemptLabel(int attempt) => attempt == 0 ? "" : $" (retry {attempt})";

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

    // ── Fast-path branches ─────────────────────────────────────────────────────────────

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

    // ── Decomposer ────────────────────────────────────────────────────────────────────

    public static void RecordDecomposerSplit(this BroadcastingStepList steps, string question,
        IReadOnlyList<string> subQuestions, string source, string? llmPrompt, string? llmRaw, long elapsedMs) =>
        steps.Add(Step(StageNames.StepDecomposer, StageNames.StatusOk, elapsedMs,
            $"split into {subQuestions.Count} sub-questions",
            technical: StepPayload.Of(StepPayloadKinds.Decompose,
                input: llmPrompt ?? question,
                output: llmRaw ?? string.Join("\n", subQuestions.Select((s, i) => $"Q{i + 1}: {s}")),
                reason: $"Question is compound — split via {source}. {subQuestions.Count} independent sub-questions each run the analyst loop (ground → generate → validate → execute → explain).",
                details: new { count = subQuestions.Count, subQuestions = subQuestions.ToArray(), source })));

    public static void RecordDecomposerAtomic(this BroadcastingStepList steps, string question,
        string source, string? llmPrompt, string? llmRaw, long elapsedMs) =>
        steps.Add(Step(StageNames.StepDecomposer, StageNames.StatusOk, elapsedMs,
            "atomic — single-question path",
            technical: StepPayload.Of(StepPayloadKinds.Decompose,
                input: llmPrompt ?? question,
                output: llmRaw ?? "(no LLM call — regex returned single segment)",
                reason: $"Decomposer (source: {source}) found no compound structure. Question stays atomic and runs the analyst loop once.",
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

    // ── Validator / Executor / Explainer (live analyst-loop steps) ─────────────────────

    public static void RecordValidatorFailed(this BroadcastingStepList steps, CompiledSql compiled,
        IReadOnlyList<string> errors, int attempt) =>
        steps.Add(Step($"{StageNames.StepValidator}{AttemptLabel(attempt)}", StageNames.StatusFailed, 0,
            string.Join("; ", errors),
            technical: StepPayload.Of(StepPayloadKinds.SqlValidate,
                input: compiled.Sql,
                output: $"REJECTED: {string.Join("; ", errors)}",
                reason: "ScriptDom AST walk rejected the SQL. Trips on DML, multi-statement scripts, or disallowed functions. Retrying with the error fed back to the LLM.",
                details: new { errors, attempt })));

    public static void RecordValidatorOk(this BroadcastingStepList steps, CompiledSql compiled, int attempt) =>
        steps.Add(Step($"{StageNames.StepValidator}{AttemptLabel(attempt)}", StageNames.StatusOk, 0, "passed",
            technical: StepPayload.Of(StepPayloadKinds.SqlValidate,
                input: compiled.Sql,
                output: "PASSED",
                reason: "ScriptDom AST walk found no DML, no multi-statement scripts, no disallowed functions. Safe to execute against the read-only connection.",
                details: new { sqlLength = compiled.Sql.Length, attempt })));

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
