namespace AnalystAgent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Top-level configuration for the in-host copilot. Bound from the <c>AnalystAgent</c>
/// section of <c>Areas/AnalystAgent/Configuration/copilot-options.json</c> — NOT
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
///   <item>SystemSettings DB table overlay (<c>HostAnalystOptionsConfigurator</c>) — highest priority,
///         lets admins toggle behavior from the UI without redeploying.</item>
/// </list>
///
/// <para><b>Validation:</b> Bounds enforced at startup via DataAnnotations + ValidateOnStart.
/// Misconfigured values (e.g. MaxRows=0, negative timeouts) fail fast at app boot rather than
/// silently breaking every query at runtime.</para>
/// </summary>
public sealed class AnalystOptions
{
    public const string SectionName = "AnalystAgent";

    [Range(1, 50, ErrorMessage = "RetrieverTopK must be between 1 and 50.")]
    public int RetrieverTopK { get; set; } = 5;

    [Range(1, 100_000, ErrorMessage = "MaxRows must be between 1 and 100000.")]
    public int MaxRows { get; set; } = 1000;

    [Range(1, 600, ErrorMessage = "CommandTimeoutSeconds must be between 1 and 600.")]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Required]
    public string QuestionSuitesPath { get; set; } = "Areas/AnalystAgent/Eval/QuestionSuites";

    /// <summary>
    /// Maximum number of times the self-corrector reruns the planner with the previous error
    /// as context. 0 disables retry; 1 is the recommended default (so a question makes at most
    /// 2 LLM planner calls).
    /// </summary>
    [Range(0, 5, ErrorMessage = "MaxSelfCorrectionRetries must be 0..5.")]
    public int MaxSelfCorrectionRetries { get; set; } = 1;

    /// <summary>
    /// Minimal "direct analyst" path: route → ground → generate-SQL-directly (with grounding
    /// injected) → validate → execute → explain, running BEFORE the heavy form-filling QuerySpec
    /// pipeline. On any miss/failure it falls through to the existing pipeline unchanged, so this
    /// flag can never regress today's behavior. OFF by default; flip to A/B the thin path.
    /// The grounded prompt this path uses was validated live against qwen2.5-coder:7b (2026-06-02):
    /// 12/12 shapes — incl. above-average subqueries, window functions, multi-join, Arabic — produced
    /// schema-correct T-SQL, where the same model without grounding silently joined the wrong column.
    /// </summary>
    public bool EnableDirectSqlPath { get; set; } = false;


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
    public string SemanticLayerPath { get; set; } = "Areas/AnalystAgent/Configuration/semantic-layer.json";

    /// <summary>Path to the conversational short-circuit detection patterns (greeting / capabilities /
    /// thanks / farewell regexes, per locale). Absent → byte-identical in-code English fallback. Reply TEXT
    /// is separate (copilot-text.json). Edit this file (not code) to tune detection or add a language.</summary>
    public string ConversationalCuesPath { get; set; } = "Areas/AnalystAgent/Configuration/conversational-cues.json";

    /// <summary>When true, a matched SMALL-TALK cue ("how are you", "tell me a joke") gets exactly ONE
    /// LLM call (Classifier workload) for a warm one-sentence reply — fail-open to a canned redirect when the
    /// model is unavailable. When false, the canned redirect is used with zero LLM. Pure greetings / thanks /
    /// farewell / capabilities stay 0-LLM either way (they never call the model). The handler still
    /// short-circuits BEFORE the intent classifier + planner, so small-talk never reaches the SQL path.</summary>
    public bool SmallTalkUseLlm { get; set; } = true;

    /// <summary>When true, the unrequested-status-filter strip treats GROUNDING as the sole authority on
    /// which status equality to keep — it drops a model-invented <c>Status='X'</c> whose literal the value-linker
    /// did NOT ground, even if the word appears in the question. This kills the verb-blind over-filter
    /// ("bills ISSUED so far this year" → an unrequested <c>Status='Issued'</c>) now that the value-linker does
    /// proper attributive-vs-verb grounding. Adjective cases stay ("overdue bills" → 'Overdue' grounded → kept).
    /// Set false to restore the legacy question-contains-the-word fallback. Schema/grounding-driven, no vocab.</summary>
    public bool StripStatusFilterTrustGroundingOnly { get; set; } = true;

    /// <summary>Path to the compound/sequential question DECOMPOSITION cues (split + no-decompose-guard
    /// regexes, per locale). Absent → byte-identical in-code English fallback. Edit this file (not code) to
    /// tune how compound questions are split or add a language.</summary>
    public string DecompositionCuesPath { get; set; } = "Areas/AnalystAgent/Configuration/decomposition-cues.json";

    /// <summary>
    /// Path to the auto-generated schema-knowledge file (Layer 2 inference: soft-delete columns,
    /// label columns, PII flags, bridge/lookup/person table classification, date roles). Generated
    /// by <c>SchemaInferenceGenerator</c> at startup when missing or when the live schema hash
    /// differs from the file's stored hash. Safe to delete — will be regenerated.
    /// </summary>
    [Required]
    public string SchemaInferredPath { get; set; } = "Areas/AnalystAgent/Configuration/schema-inferred.json";

    /// <summary>
    /// Path to the human-curated overrides applied on top of the inferred file. Tiny — exists only
    /// for cases the heuristics get wrong. Lives in git; never auto-generated.
    /// </summary>
    public string SchemaOverridesPath { get; set; } = "Areas/AnalystAgent/Configuration/schema-overrides.json";

    /// <summary>
    /// Path to the verified-queries file — a domain-agnostic JSON store of
    /// hand-curated (question, SQL) pairs the matcher cosine-ranks against incoming questions.
    /// When a verified query's similarity ≥ <see cref="VerifiedQueryMinSimilarity"/>, the
    /// system uses its SQL directly and skips the LLM. Empty file = feature off (no matches,
    /// pipeline falls through to normal extraction).
    /// </summary>
    public string VerifiedQueriesPath { get; set; } = "Areas/AnalystAgent/Configuration/verified-queries.json";

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
    /// after each call lands in <see cref="AnalystAgent.Abstractions.LlmCallScope"/>;
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
    /// Master switch for the LLM-driven explainer. When false the orchestrator falls back to the
    /// templated explainer (faster, no LLM call on the success path).
    /// </summary>
    public bool UseLlmExplainer { get; set; } = false;

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

    /// <summary>When true (default), the LLM decomposer is skipped for questions the deterministic
    /// pre-gate (<see cref="Stages.IDecomposer.MightBeCompound"/>) judges confidently atomic — removing
    /// a full LLM round-trip from the large majority of single-part questions, the biggest cheap latency
    /// win on the data path. The pre-gate is RECALL-oriented: it errs toward "might be compound" (incl.
    /// different-dimension "top N X by A and top N Y by B" shapes) so genuine compounds still reach the
    /// LLM; the trade is a few extra LLM calls on ambiguous multi-grouping questions. Set false to always
    /// run the LLM decomposer if a deployment uses conjunction shapes the cues don't yet cover.</summary>
    public bool EnableDecomposerCompoundPreGate { get; set; } = true;

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

    /// <summary>Maximum accepted question length (characters). The API rejects anything longer with
    /// a 400 before any work runs — a cheap defensive bound (a real analyst question is well under
    /// this; only abuse/paste-bombs exceed it). Set to 0 to disable the cap.</summary>
    [Range(0, 1_000_000, ErrorMessage = "MaxQuestionLength must be 0..1000000.")]
    public int MaxQuestionLength { get; set; } = 2000;

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

    // ── Semantic external-tool selection (name + description embedding) ───────────────
    // Drives ToolHandler's tool-vs-data gate and which-tool pick from the tool's MEANING
    // (admin-editable CopilotToolDefinitions rows embedded by bge-m3), replacing the ad-hoc
    // "DB-shape veto" with a principled tool-vs-data-question margin. No hardcoded vocabulary:
    // every signal comes from the tool rows + these knobs. Fail-open: when the embedder is down
    // the handler reverts to the legacy lexical scorer + DB-shape veto path.

    /// <summary>Master switch for embedding-driven external-tool selection. When true (default), the
    /// ToolHandler routes by the tool's name+description cosine and gates tool-vs-data with
    /// <see cref="ToolVsSchemaMargin"/>. When false, it reverts to the legacy lexical keyword scorer +
    /// DB-shape veto — instant rollback with no behavior change on the disabled path. Fail-open
    /// regardless: if the embedder is unavailable the lexical path is used even when this is on.</summary>
    public bool EnableSemanticToolSelection { get; set; } = true;

    /// <summary>Absolute cosine floor a tool must beat to be eligible for dispatch in the semantic
    /// path. Below this the question is treated as "not a tool question" and falls through to the
    /// data/planner path. Default 0.55 — a real tool-shaped question against a relevant tool typically
    /// scores 0.6+; this rejects the long tail of weak coincidental matches.</summary>
    [Range(0.0, 1.0, ErrorMessage = "ToolSelectMinCosine must be 0.0..1.0.")]
    public double ToolSelectMinCosine { get; set; } = 0.55;

    /// <summary>Stage-1 tool-vs-data margin: the best tool cosine must beat the best schema-table
    /// cosine (from the schema-semantic retriever) by at least this much for the question to route to a
    /// TOOL. Otherwise it is a DATA question and falls through to the planner — so "how many tickets are
    /// open" can never be eaten by a tool whose embedding happens to score in the same neighborhood.
    /// Default 0.05.</summary>
    [Range(0.0, 1.0, ErrorMessage = "ToolVsSchemaMargin must be 0.0..1.0.")]
    public double ToolVsSchemaMargin { get; set; } = 0.05;

    /// <summary>Stage-2 disambiguation gap: when the top two tools are within this cosine delta the
    /// pick is ambiguous, so the handler spends at most ONE budget-gated LLM confirm over the ≤4 top
    /// candidates (the existing ResolveByLlmAsync). When the gap is wider, the top tool is dispatched
    /// directly with zero LLM. Default 0.04.</summary>
    [Range(0.0, 1.0, ErrorMessage = "ToolSelectGap must be 0.0..1.0.")]
    public double ToolSelectGap { get; set; } = 0.04;

    /// <summary>Tool-vs-data LEXICAL override (Fix B). A tiny lookup table has a weak schema EMBEDDING cosine,
    /// so a domain-overlapping tool can win the <see cref="ToolVsSchemaMargin"/> even when the question
    /// literally NAMES a schema entity ("list the currency codes"). When the question lexically/anchor-matches
    /// a schema entity (deterministic <see cref="Schema.ISchemaLinker.HasInScopeSignal"/>) AND the top tool's
    /// OWN cosine is below this threshold, the question is treated as DATA and the tool is suppressed — a named
    /// schema entity outranks a merely-similar tool. A genuinely strong tool match (cosine at/above this
    /// threshold) still dispatches. Default 0.66 (ENABLED): a real tool-shaped question against its tool
    /// typically scores well above this, so the override only fires on the weak-tool / named-entity collision.
    /// Set 0.0 to DISABLE the override entirely (byte-identical to the pre-fix floor+margin logic).</summary>
    [Range(0.0, 1.0, ErrorMessage = "SchemaLexicalLinkOverrideToolThreshold must be 0.0..1.0.")]
    public double SchemaLexicalLinkOverrideToolThreshold { get; set; } = 0.66;

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

    /// <summary>Defense-in-depth abstain in DirectAnalystPath (Layer 2 backstop): after SQL emit,
    /// abstain when grounding resolved ZERO evidence (no values/keys/temporal/derived-metrics) AND the
    /// SQL has no aggregate call — a generic label-only projection whose every literal is model-invented
    /// (the confident-hallucination signature, e.g. "who won the world cup" → SELECT TOP 1 NameEn …).
    /// Default OFF so behavior is unchanged until an operator opts in; the classifier OOS verdict
    /// (the decisive-OOS branch in AnalystOrchestrator) and the scope gate remain the primary guards.
    /// Aggregate presence (COUNT/SUM/…) exempts legitimate zero-value analytics like "count tickets by region".</summary>
    public bool EnableUngroundedProjectionAbstain { get; set; } = false;

    /// <summary>Defense-in-depth abstain (Layer 2 backstop) that makes the repair-provenance keystone
    /// load-bearing: when a LOSSY invalid-column strip dropped a predicate carrying a VALUE LITERAL
    /// (<c>WHERE BadCol = 'X'</c> — a load-bearing filter the question wanted) AND grounding resolved NO real
    /// value/key to replace it, the shipped answer is OVER-BROAD (it counts everything). Abstain instead of
    /// returning a confident wrong number. Narrow by design: a flag strip (<c>IsDeleted = 0</c>, no literal) or
    /// a grounded query (the injector enforced the right filter) is NOT affected. This is the asymmetry guard —
    /// an abstain costs a retry, a confident-wrong costs a bad decision — and the backstop that keeps the
    /// zero-confident-wrong posture on data the agent was not tuned on. Schema-agnostic (SQL-structure only).</summary>
    public bool AbstainOnLoadBearingLossyStrip { get; set; } = true;

    // ── Execution-guided self-consistency (Slice 1: abstain-fallback ONLY) ────────────────────
    // When the greedy DirectAnalystPath attempt loop would ABSTAIN (return null), draw k DIVERSE
    // candidates (candidate 0 = the already-computed greedy attempt; 1..k-1 sampled with a higher
    // temperature + distinct seed), execute each read-only, vote by result-set fingerprint, return
    // the majority, and abstain only on genuine disagreement. EVERY knob defaults OFF/neutral so the
    // flag-OFF path is byte-identical to today; on questions the system already answers NOTHING new runs.

    /// <summary>Master switch for execution-guided self-consistency. Default OFF — when false the
    /// DirectAnalystPath abstain exits return <c>null</c> exactly as before (byte-identical). Flip on to
    /// recover answers the single greedy sample missed, at the cost of up to
    /// <see cref="SelfConsistencyK"/>−1 extra LLM draws on questions that WOULD otherwise abstain.</summary>
    public bool EnableSelfConsistency { get; set; } = false;

    /// <summary>When true (default), self-consistency fires on the ABSTAIN fallback only — i.e. exactly
    /// where the greedy loop would have returned <c>null</c>. (The hard-shape proactive trigger is a later
    /// slice and out of scope here.) Has no effect unless <see cref="EnableSelfConsistency"/> is on.</summary>
    public bool SelfConsistencyOnAbstain { get; set; } = true;

    /// <summary>Number of candidates to consider, INCLUDING the reused greedy attempt (candidate 0). So
    /// k=3 draws 2 additional sampled candidates. Range [2,7] keeps the extra LLM cost bounded.</summary>
    [Range(2, 7, ErrorMessage = "SelfConsistencyK must be between 2 and 7.")]
    public int SelfConsistencyK { get; set; } = 3;

    /// <summary>Decoding temperature for the sampled candidates (1..k-1). Higher = more diverse draws.
    /// Candidate 0 is the greedy attempt and is NOT resampled. Default 0.4 — enough diversity to escape a
    /// single bad greedy sample without drifting off-question.</summary>
    [Range(0.0, 1.5, ErrorMessage = "SelfConsistencyTemperature must be 0.0..1.5.")]
    public double SelfConsistencyTemperature { get; set; } = 0.4;

    /// <summary>Base RNG seed for the sampled candidates: candidate i (i≥1) uses
    /// <c>SelfConsistencySeedBase + i</c>, so draws are distinct AND reproducible run-to-run.</summary>
    public int SelfConsistencySeedBase { get; set; } = 1000;

    /// <summary>Minimum number of candidates that must agree (share a result-set fingerprint bucket) for a
    /// winner to be returned. Below this the path ABSTAINS (genuine disagreement → return null). Range
    /// [1,7]; default 2 (a simple majority of the default k=3).</summary>
    [Range(1, 7, ErrorMessage = "SelfConsistencyMinAgreement must be between 1 and 7.")]
    public int SelfConsistencyMinAgreement { get; set; } = 2;

    /// <summary>Decimal places to round floating-point result cells to when fingerprinting, so two
    /// candidates that agree up to floating-point noise (4.0000 vs 4.00004) land in the SAME vote bucket.
    /// Range [0,10]; default 4.</summary>
    [Range(0, 10, ErrorMessage = "SelfConsistencyNumericTolerance must be 0..10.")]
    public int SelfConsistencyNumericTolerance { get; set; } = 4;

    /// <summary>SQL aggregate-function names whose presence proves analytic intent and EXEMPTS a query
    /// from <see cref="EnableUngroundedProjectionAbstain"/>. Config-driven, no per-table/phrase vocabulary;
    /// default = the ANSI set. Matched as a function CALL (<c>\bNAME\s*\(</c>) so a column named
    /// "AccountCount" never reads as COUNT(...).</summary>
    public System.Collections.Generic.List<string> AggregateSqlFunctions { get; set; }
        = new() { "COUNT", "SUM", "AVG", "MIN", "MAX" };

    /// <summary>Locale suffixes that mark bilingual label columns ({Field}En / {Field}Ar) per the
    /// EN/AR locale convention. Drives the deterministic projection-column repair in DirectAnalystPath:
    /// when the model writes a column the table lacks (e.g. <c>Name</c>), the repair looks for
    /// <c>Name</c>+suffix on that table and rewrites to it, locale-aware (an Arabic question prefers the
    /// Ar suffix). ONE knob — no per-table vocabulary, no scattered "En"/"Ar" literals in code. List
    /// order is the resolution order for the default (English) locale. Empty disables suffix rewriting
    /// (the LabelColumn fallback still applies).</summary>
    public System.Collections.Generic.List<string> BilingualLocaleSuffixes { get; set; }
        = new() { "En", "Ar" };

    /// <summary>When true, a 0-row result is TRUSTED (returned as the honest answer) instead of being
    /// resampled with a loosen-the-filters hint WHEN the question produced explicit grounding filters
    /// (linked lookup values or natural keys). Without this, an honestly-empty grounded query
    /// ("tickets from 2010", "ticket TKT-999") gets loosened and may answer a DIFFERENT question. The
    /// resample still fires for ungrounded empties (the over-restrictive-flag case it was built for).
    /// Default on.</summary>
    public bool TrustGroundedEmptyResult { get; set; } = true;

    /// <summary>
    /// STAGE-2 mechanism switch (default OFF). When false, the three highest-risk deterministic repairs —
    /// GROUP-BY grain, unrequested-status-predicate strip, and multi-value grounded-filter injection — run
    /// via their original regex-on-text implementations in <c>DirectAnalystPath</c> (today's behavior,
    /// byte-identical). When true, those same three repairs run as AST MUTATIONS on the ScriptDom tree
    /// (<c>SqlAstRepairs</c>): the tree is parsed once, mutated, and re-rendered by the grammar, so a clause
    /// can never be glued to the next keyword (the bug class that motivated this) and OR/parenthesis/subquery
    /// WHERE shapes the regex strip can't reach are handled. The POLICY is unchanged (same label test, same
    /// grounded values, same PK/owner lookup); only the MECHANISM differs. The other deterministic repairs are
    /// unaffected by this flag. Flip on once the AST path is proven equivalent on the gold corpus.
    /// </summary>
    public bool EnableAstRepairs { get; set; } = false;

    /// <summary>When true, SchemaLinker materializes the intermediate bridge tables on the shortest
    /// FK path between two anchored tables that the lookup/≤8-column closure heuristic leaves
    /// unconnected — including WIDE non-lookup tables (e.g. Customers) needed only for join-ability.
    /// Uses the already-built Dijkstra join-pather (ForeignKeyGraph.FindPath); bounded by a small hop
    /// cap so the slice stays tight for a small model. Default on; set false to revert to the
    /// heuristic-only closure.</summary>
    public bool EnableTransitiveJoinBridging { get; set; } = true;

    /// <summary>When true, ValueLinker falls back to the fuzzy (trigram) value index after exact
    /// whole-word matching, so a typo'd lookup value ("Dmascus" → "Damascus") still binds a filter.
    /// Fuzzy hits are scoped to the same FK-reachable lookup candidates as the exact pass and carry the
    /// match similarity as their confidence (so they're distinguishable from exact 1.0 binds). The fuzzy
    /// index (Ai:EntityResolution) is maintained by ValueIndexHostedService; if it's empty this is a
    /// no-op. Default on.</summary>
    public bool EnableFuzzyValueLinking { get; set; } = true;

    /// <summary>Upper word-count bound on a value the exact-match lookup pass will bind. A genuine lookup /
    /// enum value is a short label ("Open", "Rural Damascus", "In Progress"); a value longer than this is a
    /// free-text column (a Title/Name/Notes field that captured a whole sentence) masquerading as a label.
    /// Mirrors the inline-enum pass's cardinality guard for the lookup pass, closing the divergence that let
    /// a short-row table's free-text column self-bind a logged question. One scalar bound — no table/column
    /// list, fully portable. Default 4 (the longest legitimate value in-domain is 2 words, so this has
    /// margin); 1 is unsafe (would clip two-word statuses like "In Progress").</summary>
    [Range(1, 10, ErrorMessage = "MaxLookupValueWords must be between 1 and 10.")]
    public int MaxLookupValueWords { get; set; } = 4;

    /// <summary>When true (default), the inline-enum value pass treats an enum word as a past-tense VERB
    /// (and SKIPS binding it as a status filter) not only when it is immediately followed by a preposition
    /// ("issued IN", "paid BY") but also when followed by a temporal adverb / function word ("issued SO far",
    /// "closed LAST month", "paid YET") or a bare year ("issued 2024"). This fixes the status-verb over-filter
    /// where "how many bills were issued so far this year" wrongly forced <c>WHERE Status='Issued'</c>. The
    /// attributive override always protects real adjective bindings ("overdue BILLS", "open TICKETS") because
    /// the next token is then the entity noun, so this is safe to leave on. The closed-class adverb list is
    /// language grammar, not data/domain vocabulary, so it is portable to any schema. Set false to revert to
    /// the preposition-only test.</summary>
    public bool EnableVerbTimeCueGuard { get; set; } = true;

    /// <summary>When true, the schema linker consults a global VALUE→TABLE index so a question token that is a
    /// distinctive ENTITY-SUBTYPE value ("transformers" → Assets.AssetType) anchors its owning table — fixing
    /// the entity-type→wrong-table confident-wrong (today "how many transformers" counts the nearest
    /// name-matched table). Additive only: never evicts a lexical anchor, never calls the embedder. The index
    /// is restricted to columns named with an <see cref="EntitySubtypeColumnSuffixes"/> suffix (targets
    /// entity-subtype columns, NOT attribute columns like Status/Priority whose values are common words), keeps
    /// only DISTINCTIVE values (owned by exactly one table), and drops short/generic tokens. Default on; set
    /// false to revert instantly.</summary>
    public bool EnableEnumValueAnchoring { get; set; } = true;

    /// <summary>Column-name suffixes marking an ENTITY-SUBTYPE column — its values are entity nouns the user
    /// names ("transformer" is a kind of asset). ONLY these columns feed the value→table index, keeping it off
    /// attribute columns (Status/Priority/Outcome) whose values are common modifiers ("resolved"/"high"). A
    /// naming-convention rule (like the bilingual NameEn/NameAr suffixes) → schema-data-driven and portable.
    /// Default ["Type"] (AssetType, PointType, OrderType, …).</summary>
    public List<string> EntitySubtypeColumnSuffixes { get; set; } = new() { "Type" };

    /// <summary>Column-name FRAGMENTS that mark a column as free-text / identifier (NOT a bindable enum) — a
    /// column whose name CONTAINS any of these is excluded from the inline-enum value probe. Externalized so a
    /// schema with a status-like enum in a column named e.g. "MessagePriority" or "TransactionReferenceType"
    /// can clear the offending fragment, instead of being silently un-probed by a hardcoded deny-list. The
    /// cardinality gate (≤ maxDistinct) is the real free-text guard; this is a cheap name pre-filter. If left
    /// empty the catalog falls back to its built-in default (an empty list would over-probe wide text columns).</summary>
    public List<string> InlineEnumSkipColumnHints { get; set; } = new()
    {
        "Name", "Title", "Description", "Notes", "Summary", "Comment", "Address", "Reason",
        "Specification", "Json", "Content", "Message", "Email", "Phone", "Number", "Code",
        "Reference", "Url", "Path", "Hash", "Stamp", "Token", "Key", "Id"
    };

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

    /// <summary>
    /// Maximum characters of the FULL per-call prompt + response captured when an
    /// <c>LlmTraceCaptureScope.Full</c> is active (eval/assessment runs). This is the untruncated
    /// text used to inspect a trace end-to-end and diff prompts before/after a change; the
    /// ~24K-char schema prompt fits within the default. Preview mode (normal chat) ignores this and
    /// keeps only <see cref="LlmTracePreviewMaxChars"/>. Set to 0 to disable full capture even in eval.
    /// </summary>
    [Range(0, 200_000, ErrorMessage = "LlmTraceFullMaxChars must be 0..200000.")]
    public int LlmTraceFullMaxChars { get; set; } = 32000;

    // ── Magic-number replacements promoted to options (Phase 3) ─────────────────────────────
    // Previously hardcoded in pipeline code. Defaults match the prior literal values so the
    // behaviour change is purely "tuneable from one place" — no functional drift.

    /// <summary>Approximate character budget for the SQL-generation prompt (≈ 4 chars/token). When the
    /// query-scoped schema would exceed this, <c>LlmDirectSqlEmitter</c> re-renders every table compactly
    /// (keys + FK join-map + label + lookups) so the prompt fits the model's context window instead of
    /// being silently truncated by Ollama. Keep it below <c>num_ctx − MaxOutputTokens</c> in tokens
    /// (e.g. num_ctx 8192 − 2048 output ≈ 6000 tokens ≈ 24000 chars).</summary>
    [Range(2000, 200000, ErrorMessage = "PromptCharBudget must be 2000..200000.")]
    public int PromptCharBudget { get; set; } = 24000;

    /// <summary>Top-K passed to the schema-semantic retriever from the ScopeConfidenceGate.
    /// Previously the literal <c>1</c> in <c>ScopeConfidenceGate</c>. Higher values let the
    /// gate consider lower-ranked matches when deciding whether a question is in-scope.</summary>
    [Range(1, 20, ErrorMessage = "ScopeGateRetrieverTopK must be 1..20.")]
    public int ScopeGateRetrieverTopK { get; set; } = 1;

    /// <summary>
    /// Target SQL engine / dialect for the COMPILER. <c>SqlServer</c> (default) emits T-SQL via
    /// MssqlDialect — the current production target, unchanged. <c>Postgres</c> emits PostgreSQL via
    /// the unit-tested PostgresDialect. The engine-selection KEYSTONE: makes the dialect a config
    /// choice instead of a hardcoded DI line. Bound by name (case-insensitive) from copilot-options.json.
    /// <para>NOTE: this selects the dialect the COMPILER emits. Running end-to-end against a live
    /// non-SqlServer engine ALSO needs the Stage-2 plumbing (an IDbConnection-returning connection
    /// factory + per-dialect schema introspector + validator). Until that lands, keep this at
    /// <c>SqlServer</c> for execution; Postgres selection is for compiler / unit verification.</para>
    /// </summary>
    public DatabaseEngine Database { get; set; } = DatabaseEngine.SqlServer;
}

/// <summary>Target SQL engine for the copilot's compiler dialect. See <see cref="AnalystOptions.Database"/>.</summary>
public enum DatabaseEngine
{
    SqlServer = 0,
    Postgres = 1,
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
/// host overlay (<c>HostAnalystOptionsConfigurator</c>) calls this with the resolved Copilot model.</para>
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

