namespace SuperAdminCopilot.Pipeline;

/// <summary>
/// Centralized stage-token constants used by the orchestrator (and consumed by the eval
/// rubric via <c>EvalCase.ExpectedFailedStage</c>). Keeping these in one place means a typo
/// in the orchestrator's trace string can never silently bypass an eval expectation — the
/// rubric and orchestrator agree by reference.
/// </summary>
public static class StageNames
{
    public const string Ok                  = "ok";
    public const string PreflightRefused    = "preflight-refused";
    public const string OutOfScope          = "out-of-scope";
    public const string MetadataUnsupported = "metadata-unsupported";
    public const string PlannerError        = "planner-error";
    public const string CompilerError       = "compiler-error";
    public const string ValidatorRejected   = "validator-rejected";
    public const string ExecutionError      = "execution-error";
    public const string PipelineException   = "pipeline-exception";
    public const string KillSwitchEngaged   = "kill-switch-engaged";
    public const string RateLimited         = "rate-limited";
    public const string RetryDegenerated    = "retry-degenerated";
    public const string RetryBudgetExhausted = "retry-budget-exhausted";

    // Tier markers for the three-tier cost hierarchy. Emitted as a one-line "Tier" step in
    // the trace so it's obvious from the investigation tree which tier won — and therefore
    // why the answer was fast or slow. Per the abstraction guide §2 cheap-path-first.
    public const string TierFastPathDeterministic = "tier-1-deterministic"; // Preflight, MetadataHandler, SemanticSearchHandler
    public const string TierShapeEngine          = "tier-2-shape-engine";   // Question Shape Engine
    public const string TierLlmPlanner           = "tier-3-llm";            // LlmPlanner (with retry)

    /// <summary>Tag emitted alongside Executor when the result shape doesn't match the spec.</summary>
    public const string ShapeMismatch            = "shape-mismatch";

    // Step-capture stage names (used by PipelineStep.Stage; stable for UI rendering).
    public const string StepPreflight        = "Preflight";
    public const string StepRetriever        = "Retriever";
    public const string StepPlanner          = "Planner";
    public const string StepCompiler         = "Compiler";
    public const string StepValidator        = "Validator";
    public const string StepExecutor         = "Executor";
    public const string StepExplainer        = "Explainer";
    public const string StepRetry            = "Retry";
    public const string StepIntentRoute      = "IntentRouter";
    public const string StepSqlIntentGuard   = "SqlIntentGuard";
    // Added for CopilotOrchestrator (Phase 1 redesigned pipeline).
    public const string StepConversational   = "Conversational";
    public const string StepKnowledgeMatch   = "KnowledgeMatch";
    public const string StepSemanticSearch   = "SemanticSearch";
    public const string StepToolDispatch     = "ToolDispatch";
    public const string StepAccessPolicy     = "AccessPolicy";
    public const string StepSpecExtractor    = "SpecExtractor";
    public const string StepSpecRefine       = "SpecExtractor (refine)";
    public const string StepRowShapeSanity   = "RowShapeSanity";
    public const string StepDecomposer       = "Decomposer";
    public const string StepSubQuestion      = "Sub-question";
    public const string StepOperationalGuard = "OperationalGuard";
    public const string StepVerifiedQuery    = "VerifiedQuery";
    public const string StepWriteIntentGuard = "WriteIntentGuard";
    public const string StepOutOfScopeGuard  = "OutOfScopeGuard";
    public const string StepCoverageCheck    = "CoverageCheck";
    public const string StepLlmDirectSql     = "LlmDirectSqlEmitter";

    public const string StatusOk      = "ok";
    public const string StatusSkipped = "skipped";
    public const string StatusFailed  = "failed";
    public const string StatusWarn    = "warn";    // non-fatal degradation (e.g. coverage gap)

    // Step "Kind" values consumed by the host trace UI to pick a renderer (LLM call payload,
    // SQL execution stats, function call, tool dispatch).
    public const string KindLlmCall      = "llm-call";
    public const string KindSqlExecution = "sql-execution";
    public const string KindFunctionCall = "function-call";
    public const string KindToolDispatch = "tool-dispatch";

    /// <summary>
    /// Canonical mapping from stage name (lower-cased) to the investigation graph's node ID.
    /// Returns null when the stage doesn't get its own node (informational markers like Tier).
    /// Single source of truth — the Razor view delegates to this method so the graph topology
    /// matches the constants in this class. Any new stage MUST add a case here OR be deliberately
    /// excluded (return null) so <see cref="AssertGraphCoverage"/> doesn't fail at startup.
    /// </summary>
    public static string? StageToGraphNode(string action)
    {
        var key = (action ?? "").Trim().ToLowerInvariant();
        // Strip retry-attempt suffix so "compiler (retry 1)" maps the same node as "compiler".
        // The orchestrator appends " (retry N)" to retried-step labels for trace clarity, but
        // without this normalization every retried step skipped the canonical map and fell
        // through to the legacy substring matchers — which lit the wrong graph node.
        var retryIdx = key.IndexOf(" (retry", StringComparison.Ordinal);
        if (retryIdx > 0) key = key.Substring(0, retryIdx);
        // Sub-question index ("sub-question 2") collapses to the canonical "sub-question".
        if (key.StartsWith("sub-question ", StringComparison.Ordinal)) key = "sub-question";

        return key switch
        {
        "operationalguard"       => "GUARD",
        "preflight"              => "INTAKE",
        "decomposer"             => "DECOMPOSE",
        "conversational"         => "CONV",
        "knowledgematch"         => "KNOWN",
        "semanticsearch"         => "SEMSEARCH",
        "metadatahandler"        => "METADATA",
        "tooldispatch"           => "TOOL",
        "shapeengine"            => "SHAPE",
        "retriever"              => "RETR",
        "planner"                => "PLAN",
        "intentrouter"           => "ROUTE",
        "clarificationgate"      => "CTRL",
        "compiler"               => "COMP",
        "validator"              => "VAL",
        "resultshapevalidator"   => "VAL",
        "sqlintentguard"         => "VAL",
        "executor"               => "EXEC",
        "explainer"              => "FMT",
        "retry"                  => "PLAN",
        "tier"                   => null,
        "entityrootguard"        => "GUARDS",
        "literaldateguard"       => "GUARDS",
        "intentcoercer"          => "GUARDS",
        "intentcoercer-antijoin" => "GUARDS",
        "intentnormalizer"       => "GUARDS",
        "questionrewriter"       => "GUARDS",
        "semanticunderstanding"  => "GUARDS",
        "conversationcontext"    => "GUARDS",
        "retrydegenerationguard" => "DEGEN",
        // ── New-pipeline steps introduced by CopilotOrchestrator ────────────
        "accesspolicy"           => "VAL",     // safety gate — rendered alongside other validators
        "specextractor"          => "PLAN",    // the redesigned LLM step replaces the old planner
        "specextractor (refine)" => "PLAN",    // retry path with previous spec + error
        "rowshapesanity"         => "VAL",     // empty-result detector — validator-tier check
        "sub-question"           => "DECOMPOSE", // each fanned-out sub-question lives under the decomposer node
        "verifiedquery"          => "PLAN",    // verified-query short-circuit lives in the planner tier conceptually
        "writeintentguard"       => "GUARD",   // deterministic preflight against write attempts
        "outofscopeguard"        => "GUARD",   // positive scope-confidence gate (B1 — 2026-05-19); refuses when VQ-cosine + schema-linker both below floors
        "coveragecheck"          => "FMT",     // post-Explainer verification — sits in formatter tier
        "llmdirectsqlemitter"    => "PLAN",    // escape valve — raw T-SQL emitter, sits in planner tier
        _ => null,   // unknown stage — caller falls through to legacy substring matchers
        };
    }

    /// <summary>
    /// Startup assertion: every <c>Step*</c> constant declared in <see cref="StageNames"/> must
    /// map to a graph node via <see cref="StageToGraphNode"/>. Catches the case where a new
    /// pipeline stage is added but the graph mapping is forgotten — the node would silently
    /// not light up in the investigation tree. Throws at host startup so the developer notices
    /// before the trace ships to a real user.
    /// </summary>
    public static void AssertGraphCoverage()
    {
        var fields = typeof(StageNames).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var missing = new List<string>();
        foreach (var f in fields)
        {
            if (!f.Name.StartsWith("Step", StringComparison.Ordinal)) continue;
            var name = f.GetValue(null) as string;
            if (string.IsNullOrEmpty(name)) continue;
            if (StageToGraphNode(name) is null)
                missing.Add($"{f.Name}=\"{name}\"");
        }
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "[StageNames] graph-coverage check FAILED: the following Step* constants have no " +
                "node mapping in StageToGraphNode. Add a switch case in Stages.cs and the matching " +
                $"node in _InvestigationPipelineGraph.cshtml. Missing: {string.Join(", ", missing)}");
        }
    }
}

/// <summary>
/// One node in the Workflow tab's architectural template. The view renders the pipeline as
/// a fixed map of these descriptors — fired stages light up using the recorded
/// <see cref="PipelineStep"/>; un-fired stages render dimmed but stay clickable so the user
/// always sees the full architecture, not just what happened to execute this turn.
/// </summary>
/// <param name="CanonicalName">Lower-case key used to match against recorded step names
/// (with retry-suffix and sub-question-index stripped). Stable across renames of
/// <c>StageNames.Step*</c>.</param>
/// <param name="Label">User-facing label rendered in the workflow node.</param>
/// <param name="Description">"What this stage does" — used as the row-detail when the stage
/// didn't fire, so a click on a dimmed node is never empty.</param>
/// <param name="Mandatory">True for stages that ALWAYS run in their branch; false for
/// conditional/retry-triggered stages (RowShapeSanity, SpecRefine retry). Drives the
/// `MANDATORY` vs `OPTIONAL` pill badge.</param>
/// <param name="Section">Which section of the diagram this node belongs to. Drives layout —
/// rail-pre / router-probe / decomposer / branch-* / rail-post / terminal.</param>
/// <param name="BranchColumn">For section=branch-*, which of the 5 router outcomes this
/// stage belongs to (GeneralChat / SemanticSearch / ExternalTool / VerifiedQuery / DataQuery).
/// Null for non-branch sections.</param>
public sealed record PipelineStageDescriptor(
    string CanonicalName,
    string Label,
    string Description,
    bool Mandatory,
    string Section,
    string? BranchColumn = null);

/// <summary>
/// The Workflow tab's source of truth. Top-to-bottom order matches the orchestrator flow in
/// <see cref="Pipeline.CopilotOrchestrator"/>. Adding a new stage means: (a) add a
/// descriptor here, (b) record it via OrchestratorStepRecorder. The view picks it up
/// automatically — no view changes needed.
/// </summary>
public static class PipelineArchitecture
{
    public const string SectionRailPre      = "rail-pre";
    public const string SectionRouterProbe  = "router-probe";
    public const string SectionDecomposer   = "decomposer";
    public const string SectionBranch       = "branch";
    public const string SectionRailPost     = "rail-post";
    public const string SectionTerminal     = "terminal";

    public const string BranchGeneralChat   = "GeneralChat";
    public const string BranchSemanticSearch = "SemanticSearch";
    public const string BranchExternalTool  = "ExternalTool";
    public const string BranchVerifiedQuery = "VerifiedQuery";
    public const string BranchDataQuery     = "DataQuery";

    public static readonly IReadOnlyList<PipelineStageDescriptor> Stages = new[]
    {
        // ── Pre-rail ──────────────────────────────────────────────────
        new PipelineStageDescriptor(
            "operationalguard", "Operational Guard",
            "Per-session retry-budget and cost-gate check. Refuses when the current chat has been hammering the LLM (cost runaway) or when the question would breach a configured cap.",
            Mandatory: true, Section: SectionRailPre),
        new PipelineStageDescriptor(
            "writeintentguard", "Write-Intent Guard",
            "Deterministic preflight against write attempts (delete / drop / insert / update / alter / create-table / truncate, multilingual). The pipeline is read-only by design; this gate refuses write-shaped questions in milliseconds instead of letting them burn a full LLM round-trip before being rejected later.",
            Mandatory: true, Section: SectionRailPre),

        // ── Router · probe cascade ────────────────────────────────────
        new PipelineStageDescriptor(
            "conversational", "Conversational",
            "Tries to handle the question without SQL or LLM — greeting (\"hi\"), meta-question (\"what can you do\"), or thanks. Terminal on match: pipeline short-circuits with a templated reply.",
            Mandatory: false, Section: SectionRouterProbe),
        new PipelineStageDescriptor(
            "knowledgematch", "Knowledge Match",
            "Looks the question up in the FAQ dictionary (\"what is a ticket\", \"what is a priority\"). Returns a curated definition. Terminal on match — no LLM call.",
            Mandatory: false, Section: SectionRouterProbe),
        new PipelineStageDescriptor(
            "semanticsearch", "Semantic Search",
            "Vector-similarity search over indexed entities. Used when the question names a specific record by id or topic (\"ticket TCK-2026\", \"tickets about login\"). Terminal: result flows through Explainer.",
            Mandatory: false, Section: SectionRouterProbe),
        new PipelineStageDescriptor(
            "tooldispatch", "Tool Dispatch",
            "Routes to a registered external tool (weather, FX, country lookup, Jira, Slack). Delegates instead of writing SQL. Terminal: tool response flows through Explainer.",
            Mandatory: false, Section: SectionRouterProbe),
        new PipelineStageDescriptor(
            "verifiedquery", "Trusted Query",
            "Hand-curated (question, SQL) catalog. When a new question's embedding cosine-matches a trusted entry above the threshold, the SQL is used directly — skipping the LLM. Terminal: SQL flows through Validator + Executor + Explainer.",
            Mandatory: false, Section: SectionRouterProbe),
        new PipelineStageDescriptor(
            "outofscopeguard", "Scope Confidence Gate",
            "Positive scope-confidence gate (B1 — 2026-05-19). Refuses the question only when all fast paths have missed AND both lightweight scope signals are below floor: (a) max verified-query cosine, (b) top schema-linker score. Replaces the prior regex-bank OutOfScopeHandler; defines OOS as the residual of what's in scope rather than enumerating bad inputs. Schema-agnostic — works against any deployment's schema/tools/catalog.",
            Mandatory: false, Section: SectionRouterProbe),

        // ── Decomposer (mandatory when DataQuery default fires) ──────
        new PipelineStageDescriptor(
            "decomposer", "Decomposer",
            "Decides whether the question is atomic or compound. If compound (\"open AND closed tickets by category\"), splits into sub-questions that each run their own DataQuery pipeline in parallel.",
            Mandatory: true, Section: SectionDecomposer),

        // ── Data query branch ────────────────────────────────────────
        new PipelineStageDescriptor(
            "specextractor", "Spec Extractor",
            "Form-filling LLM call. Reads the question + retrieved schema slice, emits a structured QuerySpec JSON (root, filters, aggregations, group-by). Replaces free-form SQL generation.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "specrefine", "Spec Refine (retry)",
            "Retry of the spec extractor with the previous error fed back to the LLM. Runs only when Compiler / Validator / IntentGuard / Executor / RowShapeSanity reported a problem.",
            Mandatory: false, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "accesspolicy", "Access Policy",
            "Blocks tables and columns marked sensitive (PasswordHash, ApiKey, Secret, …) — even if the LLM tries to project them. Runs BEFORE compile so a forbidden column never even appears in a generated query.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "compiler", "Compiler",
            "QuerySpec → parameterised T-SQL. Applies join graph resolution, soft-delete filter, MaxRows cap, FK label projection. Fully deterministic.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "validator", "Validator",
            "AST-walks the generated SQL via ScriptDom. Rejects DML, multi-statement scripts, dangerous functions. Last syntactic gate before execution.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "sqlintentguard", "SQL Intent Guard",
            "Spec-shape sanity check (TOP-N without ORDER BY, anti-join with redundant filters, COUNT for a list question, etc.). Catches what the syntactic validator misses.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "executor", "Executor",
            "Runs the compiled SQL against the read-only DB connection. Stops reading at MaxRows; surfaces IsTruncated when capped.",
            Mandatory: true, Section: SectionBranch, BranchColumn: BranchDataQuery),
        new PipelineStageDescriptor(
            "rowshapesanity", "Row-Shape Sanity",
            "Detects suspicious empty results (e.g. TOP-N or aggregation returning 0 rows) and triggers a refinement retry. Optional — only fires when the executor succeeded but the result shape looks wrong.",
            Mandatory: false, Section: SectionBranch, BranchColumn: BranchDataQuery),

        // ── Post-rail ─────────────────────────────────────────────────
        new PipelineStageDescriptor(
            "explainer", "Explainer",
            "LLM call that summarises the result rows as a natural-language paragraph. Falls back to a templated reply for trivial / small / budget-exhausted result sets. Last LLM call in the pipeline.",
            Mandatory: true, Section: SectionRailPost),
        new PipelineStageDescriptor(
            "coveragecheck", "Coverage Check",
            "Post-Explainer verification pass. An LLM reads (question, generated SQL, result columns + first row, reply) and emits either 'COMPLETE' or a list of question aspects the answer fails to address. Catches partial answers — e.g. compound questions where only the first half was answered, or cases where the wrong column was joined and the result data misses the intent.",
            Mandatory: false, Section: SectionRailPost),
    };

    /// <summary>Normalise a recorded step's Action to the canonical key used in the architecture.
    /// Strips " (retry N)" suffix from retried steps and "<index>" suffix from sub-questions so
    /// retried + indexed variants all match the base descriptor.</summary>
    public static string CanonicalNameOf(string? action)
    {
        if (string.IsNullOrEmpty(action)) return string.Empty;
        var s = action.ToLowerInvariant();
        var retryIdx = s.IndexOf(" (retry", StringComparison.Ordinal);
        if (retryIdx > 0) s = s.Substring(0, retryIdx);
        if (s.StartsWith("sub-question ", StringComparison.Ordinal)) s = "sub-question";
        // " " in labels like "spec extractor" → "specextractor"
        s = s.Replace(" ", "").Replace("(refine)", "refine");
        return s;
    }
}
