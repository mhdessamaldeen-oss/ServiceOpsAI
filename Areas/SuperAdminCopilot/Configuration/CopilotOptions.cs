namespace SuperAdminCopilot.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Top-level configuration for the in-host copilot. Bound from the <c>SuperAdminCopilot</c>
/// section of <c>Areas/SuperAdminCopilot/Configuration/copilot-options.json</c> — NOT
/// appsettings.json (locked principle: this file is the single source of runtime knobs).
/// The host build does NOT use ConnectionString or Llm subsections — those are sourced from
/// the host (host's ConnectionStrings:DefaultConnection + host's WorkloadAwareProvider for
/// the Copilot workload).
///
/// <para><b>Config-source hierarchy</b> (highest priority last — later overrides earlier):</para>
/// <list type="number">
///   <item>C# defaults declared on each property below (lowest).</item>
///   <item>Values bound from <c>copilot-options.json</c> at startup.</item>
///   <item>Profile preset overlay (PostConfigure step in DI; selected via the
///         <c>Profile</c> field in copilot-options.json — "Cloud" | "LocalMedium" | "LocalSmall").</item>
///   <item>SystemSettings DB table overlay (<c>HostCopilotOptionsConfigurator</c>) — highest priority,
///         lets admins toggle behavior from the UI without redeploying.</item>
/// </list>
///
/// <para><b>Validation:</b> Bounds enforced at startup via DataAnnotations + ValidateOnStart.
/// Misconfigured values (e.g. MaxRows=0, negative timeouts) fail fast at app boot rather than
/// silently breaking every query at runtime.</para>
/// </summary>
public sealed class CopilotOptions
{
    public const string SectionName = "SuperAdminCopilot";

    [Range(1, 50, ErrorMessage = "RetrieverTopK must be between 1 and 50.")]
    public int RetrieverTopK { get; set; } = 5;

    [Range(1, 100_000, ErrorMessage = "MaxRows must be between 1 and 100000.")]
    public int MaxRows { get; set; } = 1000;

    [Range(1, 600, ErrorMessage = "CommandTimeoutSeconds must be between 1 and 600.")]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Required]
    public string QuestionSuitesPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/QuestionSuites";

    /// <summary>
    /// Maximum number of times the self-corrector reruns the planner with the previous error
    /// as context. 0 disables retry; 1 is the recommended default (so a question makes at most
    /// 2 LLM planner calls).
    /// </summary>
    [Range(0, 5, ErrorMessage = "MaxSelfCorrectionRetries must be 0..5.")]
    public int MaxSelfCorrectionRetries { get; set; } = 1;


    /// <summary>Phase 6 schema-drift safety. When true, the SchemaDriftLinter throws at
    /// startup if it finds JSON-config table/column references that no longer exist in the
    /// live DB. Off by default — most deployments prefer log warnings to crash-on-startup.</summary>
    public bool FailFastOnSchemaDrift { get; set; } = false;

    /// <summary>Exact table names hidden from the retriever. Single-source convention: C# default
    /// is empty; values live in <c>copilot-options.json</c>. Case-insensitive.</summary>
    public List<string> RetrieverHiddenTables { get; set; } = new();

    /// <summary>Wildcard patterns hidden from the retriever (e.g. <c>__*</c> for EF migrations,
    /// <c>AspNet*</c> for Identity tables). Single-source convention: C# default is empty;
    /// values live in <c>copilot-options.json</c>. Wildcard syntax: <c>*</c> matches any sequence.</summary>
    public List<string> RetrieverHiddenTablePatterns { get; set; } = new();

    /// <summary>
    /// Cosine-score penalty subtracted from auxiliary/satellite tables during schema retrieval.
    /// "Auxiliary" is determined by <c>semanticLayer.defaults.auxiliaryTableSuffixes</c>
    /// (Histories / Notifications / Audits / Logs / Snapshots / …). The penalty must be small
    /// enough to leave a clear cosine win untouched but large enough to break near-ties in
    /// favour of the main entity ("Outages" should beat "OutageHistories" for "show me outages").
    /// Default 0.05 — change in <c>copilot-options.json</c> to tune routing behaviour without
    /// recompiling. Set to 0 to disable the penalty entirely.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "AuxiliaryTableScorePenalty must be 0.0..1.0.")]
    public double AuxiliaryTableScorePenalty { get; set; } = 0.05;

    /// <summary>Path to the semantic-layer JSON config (entities, metrics, dimensions, synonyms).</summary>
    [Required]
    public string SemanticLayerPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/semantic-layer.json";

    /// <summary>Path to the few-shot examples JSON the planner retrieves from at planning time.</summary>
    [Required]
    public string FewShotExamplesPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/few-shot-examples.json";

    /// <summary>
    /// Path to the auto-generated schema-knowledge file (Layer 2 inference: soft-delete columns,
    /// label columns, PII flags, bridge/lookup/person table classification, date roles). Generated
    /// by <c>SchemaInferenceGenerator</c> at startup when missing or when the live schema hash
    /// differs from the file's stored hash. Safe to delete — will be regenerated.
    /// </summary>
    [Required]
    public string SchemaInferredPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/schema-inferred.json";

    /// <summary>
    /// Path to the human-curated overrides applied on top of the inferred file. Tiny — exists only
    /// for cases the heuristics get wrong. Lives in git; never auto-generated.
    /// </summary>
    public string SchemaOverridesPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/schema-overrides.json";

    /// <summary>
    /// Path to the verified-queries file — a domain-agnostic JSON store of
    /// hand-curated (question, SQL) pairs the matcher cosine-ranks against incoming questions.
    /// When a verified query's similarity ≥ <see cref="VerifiedQueryMinSimilarity"/>, the
    /// system uses its SQL directly and skips the LLM. Empty file = feature off (no matches,
    /// pipeline falls through to normal extraction).
    /// </summary>
    public string VerifiedQueriesPath { get; set; } = "Areas/SuperAdminCopilot/Configuration/verified-queries.json";

    /// <summary>Minimum cosine similarity for a verified-query match to be used. Higher = stricter.
    /// 0.90 keeps clear paraphrases of the verified question; below that the LLM should handle it.</summary>
    [Range(0.0, 1.0, ErrorMessage = "VerifiedQueryMinSimilarity must be 0.0..1.0.")]
    public double VerifiedQueryMinSimilarity { get; set; } = 0.90;

    /// <summary>
    /// When true, the assessment runner appends every case that passes EX-accuracy via the LLM
    /// path (provenance "llm-cold" / "self-corrected" / "direct-emit") to verified-queries.json
    /// via <see cref="Retrieval.IVerifiedQueryWriter"/>. The catalog grows organically with each
    /// run — every confirmed-correct question becomes a permanent precedent that bypasses the LLM
    /// on subsequent runs. Idempotent on canonical question text (re-running adds nothing new).
    /// Default off so experimental runs don't pollute the catalog; flip on for "blessed" runs
    /// that should grow the trusted set.
    /// </summary>
    public bool EnableAssessmentAutoPromotion { get; set; } = false;

    /// <summary>How many few-shot examples to retrieve and inject into the planner prompt.</summary>
    [Range(0, 20, ErrorMessage = "FewShotTopK must be 0..20.")]
    public int FewShotTopK { get; set; } = 3;

    /// <summary>
    /// Master switch for the learning-RAG path (past successful traces injected as worked
    /// examples into the planner prompt). When false, the planner relies only on the static
    /// FewShotExampleStore — useful during eval/benchmark runs so the suite's own early
    /// answers don't poison later questions in the same run.
    /// </summary>
    public bool UsePastQuestionRag { get; set; } = true;

    /// <summary>
    /// Minimum cosine similarity for a past trace to be eligible as a RAG hit. Higher = stricter.
    /// 0.65 was too loose — paraphrases with only a couple of overlapping content words crossed
    /// the bar and dragged the planner toward unrelated SQL shapes. 0.82 keeps clear paraphrases
    /// and rejects "kinda similar".
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "PastQuestionRagMinSimilarity must be 0.0..1.0.")]
    public double PastQuestionRagMinSimilarity { get; set; } = 0.82;

    /// <summary>
    /// When a single GeneratedScript appears in this many or more past traces, exclude every
    /// trace that produced it from the RAG corpus. Defends against the contamination loop where
    /// one bad SQL shape gets emitted for many questions and then dominates retrieval for the
    /// next batch of questions, reinforcing itself. Set to 0 to disable the filter.
    /// </summary>
    [Range(0, 1000, ErrorMessage = "PastQuestionRagDegenerateScriptThreshold must be 0..1000.")]
    public int PastQuestionRagDegenerateScriptThreshold { get; set; } = 5;

    /// <summary>Max LLM calls per question. Default 7 covers: classifier + cue-parser + spec + 1 retry + coverage + explainer = 6, plus 1 margin.</summary>
    [Range(0, 20, ErrorMessage = "MaxLlmCallsPerQuestion must be 0..20.")]
    public int MaxLlmCallsPerQuestion { get; set; } = 7;

    /// <summary>
    /// Per-question hard cap on the cumulative estimated USD cost of LLM calls. Checked
    /// after each call lands in <see cref="SuperAdminCopilot.Abstractions.LlmCallScope"/>;
    /// when the running total exceeds this cap, the next LLM call short-circuits with a
    /// <c>cost-budget exceeded</c> error and the orchestrator falls back to the
    /// best-available answer (or refuses if nothing has been produced yet). 0 disables the
    /// gate — useful for local-only deployments where every model's rate is 0.
    /// </summary>
    [Range(0.0, 1000.0, ErrorMessage = "MaxCostPerQuestionUsd must be 0..1000.")]
    public decimal MaxCostPerQuestionUsd { get; set; } = 0m;

    /// <summary>
    /// Per-question hard cap on cumulative LLM tokens (prompt + completion across every
    /// call). Same enforcement point as <see cref="MaxCostPerQuestionUsd"/>; the two caps
    /// are independent (whichever trips first wins). 0 disables.
    /// </summary>
    [Range(0, 10_000_000, ErrorMessage = "MaxTokensPerQuestion must be 0..10M.")]
    public int MaxTokensPerQuestion { get; set; } = 0;

    /// <summary>
    /// Maximum wall-clock time per question, in seconds. After this elapses, no further LLM
    /// calls fire — the budget is exhausted. The currently-running request still completes,
    /// but if it depends on more LLM work it surfaces with trace token RetryBudgetExhausted.
    /// 0 disables the cap.
    /// <para>Default 1800s (30 min) covers cold-start + worst-case retry chains. Steady-state
    /// per-question wall-clock is ~10–15s, so this is a generous ceiling, not a target.
    /// Must be ≥ <see cref="LlmCallTimeoutSeconds"/>.</para>
    /// </summary>
    [Range(0, 3600, ErrorMessage = "MaxQuestionWallClockSeconds must be 0..3600 (1h).")]
    public int MaxQuestionWallClockSeconds { get; set; } = 1800;

    /// <summary>
    /// Per-LLM-call timeout in seconds. Wraps every provider GenerateAsync / GenerateJsonAsync
    /// so a hung model deployment can't block the eval-runner or a user request indefinitely.
    /// Default 900s (15 min) — generous for large hosted models; lower for fast small ones.
    /// Must be ≤ <see cref="MaxQuestionWallClockSeconds"/>.
    /// </summary>
    [Range(5, 1800, ErrorMessage = "LlmCallTimeoutSeconds must be 5..1800 (30min).")]
    public int LlmCallTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// P1 #80 — Trace history retention in days. The background prune job deletes
    /// CopilotTraceHistories rows older than this. Set to 0 to disable pruning entirely
    /// (useful in dev). Default 90 days balances learning-RAG corpus size vs unbounded growth.
    /// </summary>
    [Range(0, 3650, ErrorMessage = "TraceRetentionDays must be 0..3650 (10 years).")]
    public int TraceRetentionDays { get; set; } = 90;

    /// <summary>How often the trace-prune background job runs, in hours. Default 24h (once
    /// per day). Lower = more frequent prune cycles; higher = less DB load but laggier cleanup.</summary>
    [Range(1, 168, ErrorMessage = "TracePruneIntervalHours must be 1..168 (1 week).")]
    public int TracePruneIntervalHours { get; set; } = 24;

    /// <summary>
    /// How verbose the schema prompt sent to the planner should be:
    /// <list type="bullet">
    ///   <item><b>Full</b>: every column on every retrieved table (~10K chars). Use when the
    ///   planner needs to reference esoteric columns the Minimal mode would skip.</item>
    ///   <item><b>Minimal</b> (default): only PK / FK / label / date / natural-key columns
    ///   (~5K chars). Cuts planner-prompt cost in half on a typical schema; works for the
    ///   90%+ of questions that don't need obscure columns.</item>
    /// </list>
    /// </summary>
    public Schema.SchemaPromptStrategy SchemaPromptStrategy { get; set; } = Schema.SchemaPromptStrategy.Minimal;

    /// <summary>
    /// Master switch for the embedding-based vector retriever (§2 stage 2). When true, the
    /// retriever embeds the question and ranks tables by cosine similarity to per-table
    /// summaries (computed once and cached). When false, the older keyword-overlap retriever
    /// is used instead — useful when the host's Rag-workload provider is unavailable or the
    /// embedding model is not yet warmed up. Defaults to true; the retriever falls back to
    /// keyword automatically when the embedder returns empty (provider down).
    /// </summary>
    public bool UseVectorRetriever { get; set; } = true;

    /// <summary>
    /// Master switch for the LLM-driven explainer. When false the orchestrator falls back to the
    /// templated explainer (faster, no LLM call on the success path).
    /// </summary>
    public bool UseLlmExplainer { get; set; } = false;

    /// <summary>
    /// Master switch for the post-Explainer Coverage Checker. When true, an extra LLM call runs
    /// after every successful answer to verify that the reply fully addresses the question.
    /// If the LLM identifies missing aspects (e.g. compound question half-answered, wrong column
    /// joined so result data lacks the asked dimension), the orchestrator prefixes the reply with
    /// a warning naming the gap. Adds one LLM call per question — feature-flagged so operators
    /// can disable on cost-sensitive deployments. Default ON.
    /// </summary>
    public bool EnableCoverageChecker { get; set; } = true;

    /// <summary>
    /// How the orchestrator reacts when the CoverageChecker reports a gap.
    /// <list type="bullet">
    ///   <item><b>Warn</b> (default): prepend a "⚠ This answer may not cover…" warning to the
    ///   reply. User sees both the answer and the gap. Same behavior as before this flag.</item>
    ///   <item><b>Refuse</b>: replace the reply with a refusal that names the gap. For deployments
    ///   that prefer "no answer" over "partial answer". Costs one LLM call to detect the gap,
    ///   same as Warn.</item>
    ///   <item><b>Off</b>: disable coverage checking entirely (equivalent to EnableCoverageChecker
    ///   = false). Saves the LLM call.</item>
    /// </list>
    /// </summary>
    public CoverageCheckMode CoverageCheckMode { get; set; } = CoverageCheckMode.Warn;

    /// <summary>
    /// When true (default), a coverage-checker gap triggers a one-shot RETRY through the raw-SQL
    /// escape valve (<c>LlmDirectSqlEmitter</c>). This is the 2026-06-01 replacement for the
    /// brittle phrase-matching shape router: instead of guessing from English keywords whether
    /// the form-filling QuerySpec can express the question, we run form-filling, let the
    /// multilingual semantic coverage checker judge the ACTUAL answer, and escalate to raw SQL
    /// only when it finds a gap (subquery comparison, window function, recursive shape the form
    /// can't express). Detection over prediction; multilingual; no hardcoded vocab.
    ///
    /// <para>Cost: one extra escape-valve LLM call ONLY on the questions where coverage finds a
    /// gap (~5–10% of analytical traffic). Set false to keep the old warn-only behavior.</para>
    /// </summary>
    public bool EnableCoverageEscapeRetry { get; set; } = true;

    /// <summary>
    /// Max number of CoverageChecker LLM calls per question. Tracked in a SEPARATE counter from
    /// the main planner/explainer retry budget so coverage verification stays reliable on hard
    /// questions where the planner has already burned its budget. Default 1 — only one audit
    /// pass per question. Set to 0 to disable (equivalent to <see cref="EnableCoverageChecker"/>=false).
    /// </summary>
    [Range(0, 5, ErrorMessage = "CoverageCheckMaxCallsPerQuestion must be 0..5.")]
    public int CoverageCheckMaxCallsPerQuestion { get; set; } = 1;

    /// <summary>Skip the CoverageChecker on first-attempt single-root specs with no joins/group-by and ≤<see cref="TrivialAnswerMaxFilters"/> filter/≤<see cref="TrivialAnswerMaxAggregations"/> aggregation. Joined / grouped / retry-recovered specs still go through it.</summary>
    public bool SkipCoverageCheckOnTrivialAnswers { get; set; } = true;

    /// <summary>
    /// Filter-count threshold used by <c>IsTrivialAnswer</c> to decide whether to skip the
    /// CoverageChecker. Previously hardcoded to 1 in the orchestrator; surfaced here so
    /// operators can tune without recompiling. Default 1 keeps the original behavior.
    /// </summary>
    [Range(0, 10, ErrorMessage = "TrivialAnswerMaxFilters must be 0..10.")]
    public int TrivialAnswerMaxFilters { get; set; } = 1;

    /// <summary>
    /// Aggregation-count threshold used by <c>IsTrivialAnswer</c>. Previously hardcoded to 1
    /// in the orchestrator; surfaced for operator tuning. Default 1 keeps original behavior.
    /// </summary>
    [Range(0, 10, ErrorMessage = "TrivialAnswerMaxAggregations must be 0..10.")]
    public int TrivialAnswerMaxAggregations { get; set; } = 1;

    /// <summary>
    /// Aggregate-function alias prefixes that <c>StripAggregatePrefix</c> recognises when
    /// matching an aggregation alias to a Computed alias (e.g. "AvgAgeDays" → "AgeDays").
    /// Previously a hardcoded English array in the compiler; surfaced here so multilingual
    /// deployments can add "Total" variants in Arabic, French, etc., without recompiling.
    /// Defaults match the prior hardcoded list: Avg / Sum / Min / Max / Total / Average.
    /// </summary>
    public System.Collections.Generic.List<string> AggregateAliasPrefixes { get; set; }
        = new() { "Avg", "Sum", "Min", "Max", "Total", "Average" };

    /// <summary>
    /// Master switch for the compound-question decomposer. When true the orchestrator detects
    /// "X and Y" / "X versus Y" / "X compared to Y" patterns and splits them into sub-questions.
    /// </summary>
    public bool EnableDecomposer { get; set; } = true;

    /// <summary>
    /// When true, the orchestrator runs the English-keyword <c>HeuristicDecomposer</c> first
    /// and only falls back to the multilingual LLM decomposer when the heuristic returns no
    /// split. Default OFF — the heuristic's regexes are English-only ("and", "vs",
    /// "compared to"), so they silently fail on non-English inputs and have historically
    /// caused mis-splits (e.g. period-comparison fan-out). LLM-only decomposition is one extra
    /// LLM call per question but works in any language and produces correct splits more often.
    /// </summary>
    public bool EnableHeuristicDecomposer { get; set; } = false;

    /// <summary>
    /// C.2 conversation memory — how many prior user turns to scan when looking for the most
    /// recently mentioned entity in a multi-turn chat. 4 covers the realistic refinement window
    /// without adding too much prompt noise. Set to 0 to disable conversation context entirely.
    /// </summary>
    [Range(0, 20, ErrorMessage = "ConversationLookbackTurns must be 0..20.")]
    public int ConversationLookbackTurns { get; set; } = 4;

    /// <summary>
    /// C.6 — How many follow-up suggested-prompt chips to surface under each answer. 3 fits the
    /// common chat UI; raise to 5 if your UI has horizontal real estate.
    /// </summary>
    [Range(0, 10, ErrorMessage = "SuggestedPromptCount must be 0..10.")]
    public int SuggestedPromptCount { get; set; } = 3;

    // ── §10 Layer 2 result cache (Tier 2 #13) ──────────────────────────────────────────────

    /// <summary>Master switch for the SQL-hash result cache wrapper around the executor.</summary>
    public bool EnableResultCache { get; set; } = true;

    /// <summary>How long a cached result lives. 60s suits "today's data" questions; raise for
    /// slower-moving facts. 0 disables caching even when <see cref="EnableResultCache"/> is true.</summary>
    [Range(0, 86400, ErrorMessage = "ResultCacheTtlSeconds must be 0..86400 (24h).")]
    public int ResultCacheTtlSeconds { get; set; } = 60;

    // ── §8 cost gate (Tier 2 #14) ───────────────────────────────────────────────────────────

    /// <summary>Master switch for the SHOWPLAN_XML pre-execute cost check. Off by default —
    /// it adds 10–50ms per query and is most useful in production once a cost threshold has
    /// been calibrated against real workload.</summary>
    public bool EnableCostGate { get; set; } = false;

    /// <summary>SQL Server estimated subtree-cost threshold. Queries above this get refused
    /// with a "narrow your question" message. Calibrate by checking SHOWPLAN_XML on real
    /// queries. Default 100 = generous (most index seeks are well below 1).</summary>
    [Range(0.01, 100_000, ErrorMessage = "MaxEstimatedQueryCost must be > 0.")]
    public double MaxEstimatedQueryCost { get; set; } = 100;

    // ── §8 kill switch + rate limit (Tier 2 #16) ────────────────────────────────────────────

    /// <summary>Global kill switch. When true, the orchestrator refuses every request with a
    /// "copilot disabled" message — no LLM call, no DB call. Flip via config in an incident.</summary>
    public bool KillSwitch { get; set; } = false;

    /// <summary>Sliding-window cap. Set to 0 to disable rate limiting.</summary>
    [Range(0, 100_000, ErrorMessage = "RateLimitMaxRequestsPerWindow must be 0..100000.")]
    public int RateLimitMaxRequestsPerWindow { get; set; } = 30;

    /// <summary>Window size for the rate limiter, in seconds. 60s + 30 requests = 1 question
    /// every 2s on average per conversation. Set to 0 to disable.</summary>
    [Range(0, 86400, ErrorMessage = "RateLimitWindowSeconds must be 0..86400 (24h).")]
    public int RateLimitWindowSeconds { get; set; } = 60;

    // ── Metadata-introspection policy ───────────────────────────────────────────────────────
    // The MetadataHandler answers questions like "what columns are in AspNetUsers" by reading
    // INFORMATION_SCHEMA / sys.foreign_keys directly. The SqlAstValidator blocks the same
    // queries on user-generated SQL, so without a policy gate, the metadata path is a strict
    // recon back door — a user could enumerate PasswordHash / SecurityStamp / connection-string
    // column names. These two switches close that gap.

    /// <summary>
    /// Master switch for the metadata-introspection handler ("what tables", "what columns in X",
    /// "data type of X.Y", "how X and Y relate"). When false, every metadata pattern is refused
    /// with a generic message. When true (default), metadata answers are returned BUT (a) rows
    /// naming a column listed in <see cref="EntityDefinition.SensitiveColumns"/> are stripped
    /// from the result and (b) when <see cref="RestrictMetadataToConfiguredEntities"/> is on,
    /// only tables registered in the semantic layer are revealed.
    /// </summary>
    public bool EnableSchemaIntrospection { get; set; } = true;

    /// <summary>
    /// When true, metadata queries return only tables registered as <c>EntityDefinition.Table</c>
    /// in the semantic layer. Tables that exist in the database but aren't part of the copilot's
    /// semantic model are hidden — preventing chat users from enumerating audit logs, migration
    /// tables, identity stamps, or any table the host didn't intentionally expose. Defaults to
    /// false to preserve existing behavior; production deployments should flip this on.
    /// </summary>
    public bool RestrictMetadataToConfiguredEntities { get; set; } = false;

    /// <summary>
    /// Governs which discovered database tables are visible to the Copilot. AllExceptBlocked is
    /// package-friendly for new apps: introspect everything, then deny dangerous/internal tables.
    /// ConfiguredOnly is stricter for production deployments that want the semantic layer to be
    /// the explicit allowlist.
    /// </summary>
    public TableExposureMode TableExposureMode { get; set; } = TableExposureMode.AllExceptBlocked;

    /// <summary>Exact table names or schema-qualified names denied to every Copilot layer.
    /// Single-source convention: C# default is empty; values live in <c>copilot-options.json</c>.</summary>
    public List<string> BlockedTables { get; set; } = new();

    /// <summary>Wildcard table patterns denied to every Copilot layer. Single-source convention:
    /// C# default is empty; values live in <c>copilot-options.json</c>.</summary>
    public List<string> BlockedTablePatterns { get; set; } = new();

    /// <summary>Wildcard column patterns denied in prompts, SQL, metadata answers, and output.
    /// Single-source convention: C# default is empty; values live in <c>copilot-options.json</c>.
    /// <b>SECURITY-CRITICAL:</b> if the JSON is missing entries like <c>*.PasswordHash</c>,
    /// <c>*.Token</c>, etc., sensitive columns CAN leak. Every deployment is responsible for
    /// declaring its own column denylist explicitly.</summary>
    public List<string> BlockedColumns { get; set; } = new();

    /// <summary>Wildcard column patterns treated as sensitive even when not listed by the semantic layer.</summary>
    public List<string> SensitiveColumns { get; set; } = new();

    /// <summary>When the top two resolver candidates are closer than this delta, clarify instead of guessing.</summary>
    [Range(0.0, 1.0, ErrorMessage = "AmbiguityClarificationThreshold must be 0.0..1.0.")]
    public double AmbiguityClarificationThreshold { get; set; } = 0.08;

    /// <summary>Minimum score accepted from package-backed entity/table/column resolvers.</summary>
    [Range(0.0, 1.0, ErrorMessage = "ResolverMinConfidence must be 0.0..1.0.")]
    public double ResolverMinConfidence { get; set; } = 0.60;

    /// <summary>
    /// Default join kind the compiler emits for projection joins (no aggregation, no filter on
    /// the target table, no explicit kind from the planner). Historically the compiler always
    /// emitted INNER, which silently drops root rows whose nullable FK is null — "tickets per
    /// assignee" lost every unassigned ticket. The B8 auto-promote already flips nullable-FK
    /// projection joins to LEFT; this option controls the promotion target so an operator can
    /// disable it (set to "inner") or extend it.
    ///
    /// <para>One of: <c>"left"</c> (default — preserve root rows), <c>"inner"</c> (only return
    /// root rows that have a related row). Invalid values fall back to <c>"left"</c>.</para>
    /// </summary>
    public string DefaultProjectionJoinKind { get; set; } = "left";

    // ── Scope-confidence gate (positive OOS definition) ──────────────────────────────
    // The gate refuses a question only when ALL fast paths missed AND both signals below
    // are below their floors. In-scope is defined positively from config (schema-inferred.json,
    // verified-queries.json, registered tools, conversational/knowledge fast-paths); out-of-scope
    // is the residual. Replaces the prior regex-bank OutOfScopeHandler.

    /// <summary>Master switch for the positive scope-confidence gate. When false the orchestrator
    /// proceeds to the LLM-driven SpecExtractor for every question that wasn't caught by the
    /// fast paths — same behavior as before the gate existed. Default true.</summary>
    public bool EnableScopeConfidenceGate { get; set; } = true;

    /// <summary>
    /// Schema-linker floor for the scope-confidence gate. If the top schema-semantic retrieval
    /// score is at or above this, the question is considered in-scope (it cosine-matches some
    /// table summary). Defaults to 0.25, which is intentionally low — a real data question
    /// against a relevant table typically scores 0.6–0.8, and we want the floor to catch the
    /// long tail of paraphrases. Operators on schemas with very generic table descriptions may
    /// need to raise this; operators on schemas with rich descriptions can leave it.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "OutOfScopeSchemaFloor must be 0.0..1.0.")]
    public double OutOfScopeSchemaFloor { get; set; } = 0.20;

    /// <summary>
    /// Verified-query catalog floor for the scope-confidence gate. If the highest catalog cosine
    /// is at or above this, the question is in-scope (it loosely resembles something curated).
    /// Note this is much lower than <see cref="VerifiedQueryMinSimilarity"/> (the catalog-trust
    /// threshold, defaulting to 0.90) — the gate only needs "is this in the neighborhood?" not
    /// "is this trustworthy?". Default 0.55.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "OutOfScopeVerifiedQueryFloor must be 0.0..1.0.")]
    public double OutOfScopeVerifiedQueryFloor { get; set; } = 0.55;

    /// <summary>High-confidence floor for the IntentClassifier — at or above this, OOS verdicts refuse outright and SQL verdicts skip the scope gate. Raised to 0.90 to reduce false-refusals on entity-heavy data questions ("bills issued by …", "explain why … jumped").</summary>
    [Range(0.0, 1.0, ErrorMessage = "IntentClassifierDecisiveConfidence must be 0.0..1.0.")]
    public double IntentClassifierDecisiveConfidence { get; set; } = 0.90;

    // ── Profile selector (Phase 4) ──────────────────────────────────────────────────────────
    // Names a preset block in copilot-options.json that overlays threshold properties at
    // load time. Null/empty = use the explicit values above with no overlay. Built-in
    // presets: "Cloud" | "LocalMedium" | "LocalSmall". Operators can add new presets by
    // editing copilot-options.json — no code change required.

    /// <summary>Selects a Profile preset block from copilot-options.json to overlay thresholds.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Planner capability tier. Drives which SpecRepair phases fire. Default <c>Weak</c>
    /// preserves the current behaviour (every phase fires) — flipping to <c>Medium</c> or
    /// <c>Strong</c> skips phases that exist only to compensate for weak local NLU (Arabic
    /// dispatch, English aggregate-verb regex, possessive markers, anti-join cues, etc.).
    /// Set this via the active <see cref="Profile"/> preset; see <c>copilot-options.json</c>.
    /// </summary>
    public PlannerCapabilityTier PlannerCapabilityTier { get; set; } = PlannerCapabilityTier.Weak;

    /// <summary>
    /// Maximum characters of each LLM prompt + response captured into the trace's
    /// <c>LlmCalls[]</c> entries so the investigation page can render "what did we send /
    /// what came back" without per-call log lookups. Truncated beyond this cap; full lengths
    /// are recorded separately so the UI can flag truncation. Set to 0 to disable preview
    /// capture entirely (PII-sensitive deployments).
    /// </summary>
    [Range(0, 64_000, ErrorMessage = "LlmTracePreviewMaxChars must be 0..64000.")]
    public int LlmTracePreviewMaxChars { get; set; } = 4000;

    // ── Magic-number replacements promoted to options (Phase 3) ─────────────────────────────
    // Previously hardcoded in pipeline code. Defaults match the prior literal values so the
    // behaviour change is purely "tuneable from one place" — no functional drift.

    /// <summary>Cap on the number of candidate tables the SpecExtractor includes in the planner
    /// prompt after neighbor-expansion. Previously the literal <c>6</c> in
    /// <c>SpecExtractor.ExpandWithNeighbors</c>. Smaller = tighter prompt for local models;
    /// larger = more context for cloud models.</summary>
    [Range(1, 50, ErrorMessage = "SpecPromptMaxTables must be 1..50.")]
    public int SpecPromptMaxTables { get; set; } = 6;

    /// <summary>Minimum cosine similarity for a verified-query catalog entry to be eligible as a
    /// few-shot example in the planner prompt (NOT to be used as the answer — that's
    /// <see cref="VerifiedQueryMinSimilarity"/>). Previously the literal <c>0.65f</c> in
    /// <c>SpecExtractor</c>'s <c>FindTopAsync</c> call.</summary>
    [Range(0.0, 1.0, ErrorMessage = "VqFewShotMinSimilarity must be 0.0..1.0.")]
    public double VqFewShotMinSimilarity { get; set; } = 0.65;

    /// <summary>Number of verified-query catalog entries to surface as few-shot examples.
    /// Previously the literal <c>3</c> in <c>SpecExtractor</c>.</summary>
    [Range(0, 20, ErrorMessage = "VqFewShotTopK must be 0..20.")]
    public int VqFewShotTopK { get; set; } = 3;

    /// <summary>Top-K passed to the schema-semantic retriever from the ScopeConfidenceGate.
    /// Previously the literal <c>1</c> in <c>ScopeConfidenceGate</c>. Higher values let the
    /// gate consider lower-ranked matches when deciding whether a question is in-scope.</summary>
    [Range(1, 20, ErrorMessage = "ScopeGateRetrieverTopK must be 1..20.")]
    public int ScopeGateRetrieverTopK { get; set; } = 1;
}

public enum TableExposureMode
{
    AllExceptBlocked = 0,
    ConfiguredOnly = 1,
}

/// <summary>
/// Capability tier of the active planner LLM. Used by the SpecRepair orchestrator to skip
/// phases whose only purpose is patching weak-model NLU gaps. Ordering is
/// <c>Weak &lt; Medium &lt; Strong</c> — phases declare a maximum tier above which they
/// stop firing.
/// </summary>
public enum PlannerCapabilityTier
{
    /// <summary>Local 7B class (qwen2.5-coder:7b). All crutch phases fire.</summary>
    Weak = 0,
    /// <summary>Local 14B-32B class (Qwen2.5-Coder-32B, OmniSQL, DeepSeek-Coder-V2-Lite) AND
    /// fast cloud models (Gemini Flash, Claude Haiku, GPT-4o-mini). The language-pattern
    /// crutches stop; structural and FK-enrichment phases continue.</summary>
    Medium = 1,
    /// <summary>Frontier-class (Claude Sonnet/Opus, GPT-4o, Gemini Pro, DeepSeek-V3). Only
    /// universal schema/safety/structural phases fire — all language-vocab crutches are skipped.</summary>
    Strong = 2,
}

/// <summary>
/// Derives the planner capability tier from the active Copilot model name. This is the 2026-06-01
/// fix for the "tier disconnected from model" bug: previously the tier came from a manual
/// <c>Profile</c> preset in copilot-options.json, so an operator who switched the model to Gemini
/// via the admin UI left the tier at the local-model default — and every weak-model crutch kept
/// firing against a capable cloud model.
///
/// <para>Now the tier follows the model automatically (an explicit
/// <c>CopilotPlannerCapabilityTier</c> SystemSettings row still overrides, for testing). The
/// host overlay (<c>HostCopilotOptionsConfigurator</c>) calls this with the resolved Copilot model.</para>
/// </summary>
public static class PlannerTierDeriver
{
    public static PlannerCapabilityTier FromModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return PlannerCapabilityTier.Weak;
        var m = model.ToLowerInvariant();

        // MEDIUM markers are checked FIRST so a "mini" / "lite" / "flash" / "haiku" variant of an
        // otherwise-strong family is NOT mis-promoted. Two traps handled here:
        //   • "gpt-4o-mini" must match Medium, never fall through to the "gpt-4o" Strong rule.
        //   • "geMINI" CONTAINS the substring "mini" — so the size qualifier must be hyphen-
        //     anchored ("-mini" / "-lite"), or every Gemini model would be mis-classed Medium.
        if (m.Contains("-mini") || m.Contains("-lite") || m.Contains("flash") || m.Contains("haiku")
            || m.Contains("14b") || m.Contains("32b")
            || m.Contains("deepseek-coder"))            // v2-lite class
            return PlannerCapabilityTier.Medium;

        // STRONG markers — frontier models.
        if (m.Contains("opus") || m.Contains("sonnet")
            || m.Contains("gpt-4o") || m.Contains("gpt-4.1") || m.Contains("gpt-4-") || m == "gpt-4"
            || m.Contains("deepseek-v3") || m.Contains("deepseek-r1")
            || m.Contains("gemini-1.5-pro") || m.Contains("gemini-2.5-pro") || m.Contains("gemini-pro")
            || m.Contains("claude-3.5") || m.Contains("claude-3.7")
            || m.Contains("claude-4") || m.Contains("claude-opus") || m.Contains("claude-sonnet"))
            return PlannerCapabilityTier.Strong;

        // WEAK — local 7B class and anything unrecognized. Safe default: every crutch fires.
        return PlannerCapabilityTier.Weak;
    }
}

/// <summary>How the orchestrator reacts to a CoverageChecker-detected gap. See
/// <see cref="CopilotOptions.CoverageCheckMode"/>.</summary>
public enum CoverageCheckMode
{
    /// <summary>Disable coverage checking entirely. No extra LLM call. Equivalent to
    /// <see cref="CopilotOptions.EnableCoverageChecker"/> = false.</summary>
    Off = 0,
    /// <summary>Default. Prepend a one-line warning naming the gap; keep the original answer.</summary>
    Warn = 1,
    /// <summary>Replace the answer with a refusal that names the gap. "No answer" preferred over
    /// "partial answer".</summary>
    Refuse = 2,
}
