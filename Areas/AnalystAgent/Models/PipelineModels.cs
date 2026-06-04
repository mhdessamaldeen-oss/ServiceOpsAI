namespace AnalystAgent.Models;

public sealed record AnalystRequest(
    string Question,
    string? ConversationId = null,
    string? CaseCode = null,
    string? SourceSuite = null,
    // C.2 — Conversation memory: prior chat turns the controller fetched from the host's
    // CopilotChatMessages table. The orchestrator's ConversationContext stage scans these to
    // (a) detect the most-recently-discussed entity for follow-up resolution and (b) inject the
    // verbatim prior question into the planner prompt for refinement-style follow-ups
    // ("sort by date", "now critical only"). Empty / null = independent question, no context.
    IReadOnlyList<PriorTurn>? History = null,
    // The assessment case's curated ExpectedSql, forwarded by the eval harness so the trace
    // sink can persist it alongside the generated SQL in CopilotTraceHistory. NULL for
    // chat-driven requests and for assessment cases without an ExpectedSql.
    string? ExpectedSql = null,
    // SignalR connection id of the chat client. When present, the live progress sink targets
    // this connection directly (Clients.Client(id)) instead of the chat_{sessionId} group —
    // which avoids the brand-new-chat race where the client can't join the group until the
    // server replies with the sessionId (so all in-flight progress events would be lost).
    string? SignalRConnectionId = null);

/// <summary>
/// One prior chat turn. Kept minimal so the new copilot doesn't take a dependency on the
/// host's <c>CopilotChatMessage</c> shape — the bridge translates host turns into
/// <c>PriorTurn</c>. <see cref="IsUser"/> is true for user turns; we don't store assistant
/// turns separately because the entity-extraction algorithm only walks user turns.
/// </summary>
public sealed record PriorTurn(string Content, bool IsUser);

public sealed record AnalystResponse(
    string Reply,
    string? Sql = null,
    int? RowCount = null,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null,
    string? Trace = null,
    int? TraceId = null,
    string? Error = null,
    IReadOnlyList<PipelineStep>? Steps = null,
    // ── UX response fields (C.6 — legacy parity) ──────────────────────────
    // Populated by the orchestrator on success paths so the host's chat UI shows
    //   • SuggestedPrompts: 3 follow-up prompt chips under the answer ("show by status",
    //     "expand to top 10", etc.) — sourced from a spec-shape-aware static catalog.
    //   • SimilarEntities: result cards rendered alongside the answer when the question
    //     was a similarity / semantic-search question. Each entry is the host-free
    //     <c>SemanticSearchHit</c> carrying EntityType + NaturalKey + DisplayLabel; the
    //     host bridge maps this onto whichever card view fits the entity (CopilotTicketCitation
    //     today; future hosts can route by EntityType).
    // Both are optional — null means "show the host's defaults" (no chips, no cards).
    IReadOnlyList<string>? SuggestedPrompts = null,
    IReadOnlyList<Abstractions.SemanticSearchHit>? SimilarEntities = null,
    // Phase 2 visualization hint — lightweight chart suggestion based on result shape.
    // Host UI may pick a default view. One of:
    //   "kpi"   — 1 row, 1 numeric column (a single count / sum / avg)
    //   "bar"   — categorical column + 1 numeric (group-by counts, top-N rankings)
    //   "line"  — date column + 1 numeric (time series)
    //   "pie"   — categorical column + 1 numeric with ≤ 8 rows
    //   "table" — anything else (default)
    string? SuggestedChartType = null,
    // Phase 1 Step 7 — answer provenance. Tells the UI (and downstream policy) WHERE the
    // SQL came from so a trust badge can render. One of:
    //   "verified"        — verified-query store cosine-matched; SQL came from curated catalog
    //   "llm-cold"        — the analyst SQL generator produced the SQL fresh from the LLM
    //   "self-corrected"  — same as llm-cold but at least one retry fired
    //   "direct-emit"     — LlmDirectSqlEmitter produced raw T-SQL
    //   "conversational"  — no SQL; templated reply
    //   "knowledge"       — no SQL; FAQ-dictionary reply
    //   "tool"            — no SQL; external HTTP tool dispatched
    //   "semantic"        — semantic-search vector hit; SQL bypassed
    //   "refusal"         — write-intent or operational refusal
    //   null              — provenance unknown (legacy traces)
    string? Provenance = null,
    // Confidence score in [0..1]. Verified-match exposes the cosine similarity; LLM paths
    // approximate confidence from retry count (1.0 = no retries, 0.5 = 1 retry, etc.).
    // Useful for UI gating ("only auto-apply answers above 0.85") downstream.
    double? Confidence = null,
    // Compiler-emitted warnings about silently-dropped parts of the LLM's spec. Each entry
    // names a column/filter that didn't make it into the SQL (e.g. unknown column, rejected
    // placeholder value). UI should render these inline so the user knows the answer didn't
    // fully honor their question. Null/empty = clean compilation.
    IReadOnlyList<Abstractions.AnalystWarning>? Warnings = null,
    // End-to-end pipeline elapsed time, measured by the orchestrator's totalSw and stamped
    // in PersistAsync. Null until set; the host bridge surfaces this on the live chat
    // ExecutionDetails so the in-message latency pill (Copilot.cshtml:1223 "headerElapsedRaw")
    // renders for the just-answered turn — not just for history loaded via HostTraceSink.
    // Same value persisted to CopilotTraceHistories.TotalElapsedMs — single measurement,
    // single source of truth.
    long? TotalElapsedMs = null);

/// <summary>
/// One stage of the orchestrator pipeline (Preflight, Retriever, Planner, Compiler, Validator,
/// Executor, Explainer, Retry). Captured per-question and surfaced into the host's investigation
/// tree via the trace-sink bridge so the per-step panel renders.
///
/// <para>A step can have <see cref="SubSteps"/> — typed sub-events that happened inside the
/// stage (LLM-call prompt+response, SQL-execution row count, tool dispatch endpoint+response).
/// The host's Mermaid investigation graph + step detail panel render these as expandable
/// children. Pre-B.4 the trace was flat; with SubSteps the user can drill into a Planner step
/// and see "Attempt 1: prompt + response" / "Attempt 2: prompt + response" without parsing JSON.</para>
/// </summary>
public sealed record PipelineStep(
    string Stage,
    string Status,           // "ok" | "skipped" | "failed"
    long ElapsedMs,
    DateTime StartedAt,
    string? Detail = null,   // one-line summary (e.g. "5 tables retrieved", "spec: COUNT over Tickets")
    string? TechnicalData = null,   // optional verbose payload (SQL, JSON spec) for debugging
    string? Kind = null,            // "llm-call" | "sql-execution" | "function-call" | "tool-dispatch"
    IReadOnlyList<PipelineStep>? SubSteps = null,
    // Per-step LLM metrics — set only for steps that fired an LLM call (Kind = "llm-call").
    // Stamped by HostTraceSink.MapStep from the matching LlmCallRecord in the per-question
    // LlmCallScope. Null on non-LLM steps and on legacy traces from before the scope landed.
    int? PromptTokens = null,
    int? CompletionTokens = null,
    decimal? EstimatedCostUsd = null,
    string? LlmModelUsed = null,
    // Per-step LLM trace payload — populated by HostTraceSink when the step has at least one
    // LLM call. Each entry mirrors one LlmCallRecord (incl. prompt + response preview) so the
    // investigation page can render "what we sent, what came back" without grepping the log.
    // Null on non-LLM steps. May contain multiple entries when a step fires multiple calls
    // (e.g. SpecExtractor with self-correction retries).
    IReadOnlyList<TracedLlmCall>? LlmCalls = null,
    // Schema-versioning hint for forward compatibility. Phases that emit new payload shapes
    // bump this. Investigation deserialiser uses it to apply per-version migrations.
    int TraceSchemaVersion = 2);

/// <summary>
/// Verbatim summary of one LLM call attached to a pipeline step. The
/// <see cref="PromptPreview"/> and <see cref="ResponsePreview"/> fields hold up to a few
/// thousand characters each (the bridge truncates beyond a configurable cap so the trace
/// JSON stays bounded). <see cref="PromptFullLength"/> / <see cref="ResponseFullLength"/>
/// tell the UI when truncation happened — operators can fetch the full text from the log
/// using the timestamp + stage name when needed.
/// </summary>
public sealed record TracedLlmCall(
    string Stage,
    string Provider,
    string? Model,
    int? PromptTokens,
    int? CompletionTokens,
    long ElapsedMs,
    decimal? EstimatedCostUsd,
    bool Success,
    string? Error,
    string? PromptPreview,
    string? ResponsePreview,
    int? PromptFullLength,
    int? ResponseFullLength,
    int RetryAttempt);

public sealed record SchemaSlice(
    IReadOnlyList<string> Tables,
    string SchemaPromptText,
    /// <summary>Optional per-table relevance score (embedding cosine). Used by the orchestrator
    /// to emit a "why this table ranked #1" trace breadcrumb. Null when the linker doesn't
    /// compute scores.</summary>
    IReadOnlyDictionary<string, double>? Scores = null);

/// <summary>
/// Optional retry context passed to <see cref="Abstractions.IPlanner.PlanAsync"/> on a
/// self-correction attempt. Carries the previous attempt's compiled SQL and the failure
/// reason so the LLM can revise its plan.
/// </summary>
public sealed record PlannerRetryContext(
    string PreviousSql,
    string PreviousError,
    string FailedStage);

public sealed record CompiledSql(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<Abstractions.AnalystWarning>? Warnings = null);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed record ExecutionResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    TimeSpan Elapsed,
    string? Error = null,
    // Telemetry breadcrumbs the executor chain stamps on the result so the orchestrator can
    // surface them in the trace's sql-execution typed payload. All optional — null when the
    // wrapper layer was off / didn't run.
    bool? CacheHit = null,                   // CachingExecutor: true when result came from cache
    bool? CostGated = null,                  // CostGateExecutor: true when refused by SHOWPLAN cost
    double? EstimatedCost = null,            // CostGateExecutor: SHOWPLAN_XML estimated subtree cost
    int? PiiRedactionCount = null,           // PiiRedactingExecutor: cells masked across the result
    bool IsTruncated = false);               // ReadOnlyExecutor: true when the read loop stopped at MaxRows
