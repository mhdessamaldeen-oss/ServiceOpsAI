namespace SuperAdminCopilot.Configuration;

/// <summary>
/// Externalized text catalog (D.3 — port of legacy <c>CopilotTextCatalog</c>). Holds every
/// user-facing message the copilot emits — refusal phrases, error messages, fallback replies —
/// so admins can tweak tone or translate them without recompiling.
///
/// <para><b>Binding</b>: the DI registration uses <c>services.Configure&lt;CopilotTextCatalog&gt;()</c>
/// with reload-on-change against <c>copilot-text.json</c>. Consumers inject
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> and read
/// <c>.CurrentValue</c> on every access so edits take effect without a process restart.</para>
///
/// <para><b>Defaults</b>: every property has a sensible default value, so missing/empty JSON
/// doesn't break the build. The JSON only needs to contain the entries an admin wants to
/// override; everything else falls back to the in-code default.</para>
///
/// <para>Existing call sites currently use hardcoded strings; migration is incremental — each
/// call site can switch to <see cref="IOptionsMonitor{T}.CurrentValue"/> as the file rolls out.</para>
/// </summary>
public sealed class CopilotTextCatalog
{
    public const string SectionName = "SuperAdminCopilot:Text";

    // Phase 3 Step 12 — Question Shape Classifier complex-analytics hints. When a question
    // contains any of these phrases (case-insensitive), the orchestrator routes it straight
    // to the LlmDirectSqlEmitter escape valve instead of trying form-filling first.
    // Operators tune per deployment (industry-specific phrasing) by editing
    // copilot-text.json. Defaults below are the universal set; operators can override OR
    // append. Empty list = no classifier hints (deactivates the fast-route to escape valve).
    // NO hardcoded fallback in code — this catalog field is the single source of truth.
    public List<string> QuestionShapeComplexHints { get; set; } = new()
    {
        "running total", "running sum", "cumulative",
        "rank ", "ranked ", "ranking",
        "percentile", "median", "p50", "p95", "p99",
        "row_number", "over (",
        "year over year", "month over month", "quarter over quarter",
        "rolling average", "moving average",
        "lag ", "lead ",
        "recursive", "with cte"
    };

    // ── SpecExtractor prompt fragments ────────────────────────────────────
    // The planner's system prompt + the conditional hint/preamble/reminder blocks the
    // builder splices into the user prompt. Externalized so (a) operators can tune planner
    // behavior per deployment without recompiling, and (b) the same text feeds both
    // training-data assembly and inference — when fine-tuning the local model, the prompt
    // baked into the LoRA adapter is the one running in production. Format placeholders are
    // expanded by string.Format at the call site; literal `{` / `}` must be doubled.

    /// <summary>System prompt for the QuerySpec form-filling planner. Universal NL2SQL framing —
    /// no schema-specific table/column names. Override per deployment via copilot-text.json.</summary>
    public string SpecExtractorSystemPrompt { get; set; } =
        "You generate a structured QuerySpec JSON from a natural-language data question against an SQL Server database. " +
        "Return ONLY valid JSON matching the requested shape — no prose, no markdown, no commentary. " +
        "Use full Table.Column qualifiers everywhere. If you cannot answer without clarifying, set intent to \"clarification\".";

    /// <summary>Top-of-prompt warning spliced in when the question is detected as a period-comparison
    /// (e.g. "this month vs last month"). Tells the LLM to emit per-bucket SUM(CASE WHEN) aggregations
    /// rather than duplicate COUNT(*) under different aliases.</summary>
    public string SpecExtractorComparisonHint { get; set; } =
        "⚠ COMPARISON QUESTION DETECTED.\n" +
        "This question asks for SIDE-BY-SIDE numbers across two or more buckets (today vs yesterday, this month vs last month, year vs year, 2024 vs 2025, etc.).\n" +
        "MANDATORY pattern: emit ONE SUM(CASE WHEN <bucket-predicate> THEN 1 ELSE 0 END) aggregation PER BUCKET.\n" +
        "WRONG: two COUNT(*) aggregations with different aliases but identical filters — they produce the same number twice.\n" +
        "RIGHT: \"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN <bucketA-predicate> THEN 1 ELSE 0 END\",\"alias\":\"BucketA\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN <bucketB-predicate> THEN 1 ELSE 0 END\",\"alias\":\"BucketB\"}], filters:[], select:[].\n" +
        "Keep filters[] EMPTY — the per-bucket predicates live INSIDE each SUM(CASE WHEN).";

    /// <summary>Preamble spliced in when re-trying after a guard/executor rejection. Surfaces the
    /// previous error and instructs the model to generate a corrected spec from scratch rather
    /// than tweaking the rejected one. {0} = the previous error string.</summary>
    public string SpecExtractorRetryPreamble { get; set; } =
        "⚠ YOUR PREVIOUS ATTEMPT WAS REJECTED. The error you must fix:\n" +
        "   → {0}\n" +
        "Generate a CORRECTED spec from scratch using the question above. Do NOT repeat the previous structure.";

    /// <summary>Preamble spliced in for multi-turn refinement questions. Instructs the model to
    /// start from the previous spec and adjust ONLY what the new question changes. {0} = the
    /// JSON-serialized previous spec.</summary>
    public string SpecExtractorRefinementPreamble { get; set; } =
        "REFINEMENT MODE: the user is modifying their PREVIOUS QUERY. Start from the previous spec below and adjust ONLY what the new question changes (add/remove a filter, change limit, swap ordering, etc.). Keep the same root unless the user explicitly switches entity.\n" +
        "Previous spec: {0}";

    /// <summary>Bottom-of-prompt reminder bullets. The recency-weighted reminders models attend
    /// to most strongly. Kept schema-agnostic — references "lookup table" / "label column" rather
    /// than specific names.</summary>
    public string SpecExtractorReminders { get; set; } =
        "IMPORTANT REMINDERS:\n" +
        "- The \"Values:\" line under a lookup table is REFERENCE ONLY — a vocabulary of valid names. NEVER add a filter for those values unless the user's question explicitly mentions one or more of them. \"count tickets per status\" → groupBy only, NO filter. \"count tickets per status, open ones\" → groupBy + filter for \"Open\".\n" +
        "- Status / priority / category VALUES (lookup-row names) are NAMES, not IDs. Filter on the LOOKUP TABLE's label column, NOT on the FK Id column.\n" +
        "- For \"X and their Y\" through a bridge: just SELECT columns from both X and Y — the compiler resolves joins via FK graph.\n" +
        "- NEVER write SQL subqueries or SQL expressions (DATEADD, GETDATE, etc.) inside filter `value`. Use LITERAL values only.\n" +
        "- For \"in the last N days\" → op:\"gte\" with value:\"last_N_days\". For \"older than N days\" → op:\"lt\" with value:\"last_N_days\".\n" +
        "- NEVER invent column names. Only reference columns that appear in the schema slice above.\n" +
        "- \"with no\" / \"without\" / \"having no\" / \"who don't have\" → always joins:[{table:\"<other>\",kind:\"anti\"}], no filter on a synthetic column.\n" +
        "- Use intent:\"clarification\" SPARINGLY. Only when the question has TWO+ equally-valid interpretations that would produce DIFFERENT SQL. \"how many X\" / \"list X\" / \"open X\" are NOT ambiguous — answer them directly. Genuine ambiguity examples: \"top X\" (by what?), \"recent stuff\" (which entity?), \"the best ones\" (by which dimension?).";

    /// <summary>Final-check block spliced in at the END of the prompt for comparison questions.
    /// Models attend more to recent tokens — repeating the comparison rule here (after examples
    /// and reminders, right before the JSON trigger) significantly improves rule adherence on
    /// small local models.</summary>
    public string SpecExtractorComparisonFinalCheck { get; set; } =
        "══════════════════════════════════════════════════════════════════════\n" +
        "FINAL CHECK — THIS IS A COMPARISON QUESTION (contains 'vs' / 'compared' / 'versus' / 'X vs Y' / 'YoY' / 'MoM').\n" +
        "Your aggregations[] array MUST contain AT LEAST TWO entries.\n" +
        "Each entry MUST be { function:\"SUM\", column:\"CASE WHEN <bucket-predicate> THEN 1 ELSE 0 END\", alias:\"<bucket-name>\" }.\n" +
        "DIFFERENT predicates per bucket. The buckets distinguish the periods/values being compared.\n" +
        "filters[] MUST be empty (or only soft-delete / IS NOT NULL gates — NEVER the comparison predicates).\n" +
        "DO NOT emit a single COUNT(*) — that gives one number, not a comparison.\n" +
        "DO NOT emit two COUNT(*) with the same filter — that gives the same number twice.\n" +
        "══════════════════════════════════════════════════════════════════════";

    // Phase 3 Step 14 — system prompt for the LlmDirectSqlEmitter escape valve. Per-database
    // operator can customize the dialect rules, the safety reminders, the formatting hint.
    // Override entirely via copilot-text.json::SuperAdminCopilot:Text:DirectSqlSystemPrompt.
    public string DirectSqlSystemPrompt { get; set; } =
        "You write SQL Server T-SQL for read-only data questions. Output ONLY the SQL — no " +
        "explanation, no markdown, no comments. The SQL must be a SINGLE SELECT statement " +
        "(no INSERT / UPDATE / DELETE / DROP / ALTER / TRUNCATE — the executor rejects those). " +
        "Wrap nothing. End with a single semicolon optional.\n\n" +
        "Use SQL Server syntax: TOP N (not LIMIT), GETDATE() / DATEADD / DATEDIFF / YEAR() / " +
        "MONTH() / FORMAT() for dates, ISNULL / COALESCE / NULLIF for nulls, PERCENTILE_CONT(0.5) " +
        "WITHIN GROUP (ORDER BY x) OVER () for medians, ROW_NUMBER() OVER / RANK() OVER / " +
        "SUM(...) OVER (ORDER BY ... ROWS BETWEEN ...) for window functions. Always include " +
        "WHERE IsDeleted = 0 when the root table has that column.";

    // ── Preflight + intent-route refusals ─────────────────────────────────
    public string PreflightRefusalPrefix { get; set; } = "I can't run that — ";
    public string PreflightWriteIntent { get; set; } = "this looks like a write/action request; the copilot is read-only.";
    public string PreflightOutOfScope { get; set; } = "this looks like a prediction or opinion question; the copilot only answers from the database.";
    public string PreflightSecretDisclosure { get; set; } = "this looks like a secrets-disclosure request; the copilot will not surface credentials or keys.";

    public string IntentOutOfScope { get; set; } = "This question is out of scope for the data copilot.";
    public string IntentMetadataUnsupported { get; set; } = "Metadata questions (about table/column structure) aren't supported in this build yet.";
    public string IntentGreeting { get; set; } = "Hi — please ask a question about the database.";

    // ── Router hard-fail messages (Phase 2) ───────────────────────────────
    // Emitted when the IntentRouter picks a branch but that branch can't fulfil the question.
    // Honest refusals beat silently dispatching to a different branch (which is what the old
    // cascade did when a probe got it wrong).
    public string RouterToolBranchNoMatch { get; set; } =
        "I thought your question was for an external tool, but none of the configured tools matched. Try rephrasing, or ask a question about the database instead.";
    public string RouterMetadataBranchNoMatch { get; set; } =
        "I thought your question was about table or column structure, but I couldn't resolve it. Try \"what tables do we have\" or \"what columns are in <table>\".";
    public string RouterSemanticBranchNoMatch { get; set; } =
        "I thought your question was a free-text search, but I couldn't anchor it to a configured entity. Try \"tickets about <topic>\" or \"similar to <code>\".";
    public string RouterClarify { get; set; } =
        "I'm not sure exactly what you're asking. Could you rephrase with a specific table or metric, like \"how many open tickets\" or \"top 5 users by activity\"?";
    public string RouterRefuse { get; set; } =
        "I can't answer that — it looks like a write request, an opinion, or a secrets request. The copilot is read-only and stays grounded in the database.";

    // ── Pipeline-stage error replies ──────────────────────────────────────
    public string PlannerFailed { get; set; } = "Could not plan a query for that question.";
    public string CompilerFailed { get; set; } = "Could not compile the plan into SQL.";
    public string ValidatorRejected { get; set; } = "Generated SQL did not pass validation.";
    public string ExecutorFailed { get; set; } = "Query failed at execution.";
    public string ShapeEngineCompilerFailed { get; set; } = "Could not compile the shape-engine plan into SQL.";
    public string ShapeEngineValidatorRejected { get; set; } = "Shape-engine SQL did not pass validation.";
    public string ShapeEngineExecutorFailed { get; set; } = "Shape-engine query failed at execution.";

    public string RetryBudgetExhausted { get; set; } = "I tried twice but couldn't form a clean plan within the budget. Try rephrasing or simplifying.";
    public string RetryDegenerated { get; set; } = "I tried twice but couldn't get a query that matches the question's intent (the retry dropped the aggregation). Try rephrasing.";
    public string PipelineExhausted { get; set; } = "Pipeline exhausted retry budget.";
    public string PipelineUnhandled { get; set; } = "Unhandled error in the pipeline.";

    // ── Operational guard refusals ────────────────────────────────────────
    public string OperationalGuardPrefix { get; set; } = "I can't run that — ";
    public string KillSwitchEngaged { get; set; } = "the copilot is currently disabled by an admin.";
    public string RateLimited { get; set; } = "you're sending requests too quickly; please wait a few seconds and try again.";

    // ── Misc ──────────────────────────────────────────────────────────────
    public string EmptyQuestion { get; set; } = "Please send a question in the body.";

    // ── SimpleCopilotOrchestrator user-facing replies ─────────────────────
    // Used when stages fail in the new pipeline. Override in copilot-text.json / appsettings.json
    // to localize or rebrand. {0}/{1} placeholders are passed through string.Format at use site.
    public string SpecExtractorFailed { get; set; } =
        "I couldn't understand the question. Try rephrasing — e.g. 'show me open tickets' or 'how many users'.";
    public string NoCandidateTablesTemplate { get; set; } =
        "I couldn't find tables matching your question. Did you mean: {0}? " +
        "Try rephrasing with one of those, or check that schema knowledge has been generated.";
    public string CompilerFailedTemplate { get; set; } = "I couldn't build SQL for that question. {0}";
    public string ValidatorRejectedTemplate { get; set; } = "The generated SQL didn't pass safety checks: {0}";
    public string SqlIntentMismatchTemplate { get; set; } = "The SQL doesn't match what you asked. {0}";
    public string ExecutorFailedTemplate { get; set; } = "The SQL failed to execute: {0}";
    public string AccessPolicyRefusalTemplate { get; set; } = "I can't run that — {0}";
    public string OperationalGuardRefusalTemplate { get; set; } = "I can't run that — {0}";
    public string DecomposedFailedSummary { get; set; } = "one or more sub-questions failed";

    // ── SpecExtractor refine retry hint (sent to the LLM, not the user) ───
    public string EmptyResultRetryHint { get; set; } =
        "The previous spec returned 0 rows but the question expected results. " +
        "You likely added a spurious filter — drop unnecessary filters and retry.";

    // ── F1.a/F1.d — Conversational + knowledge-match replies (Tier 2 i18n) ────────
    // Override these in copilot-text.json or appsettings.json to translate or rebrand.
    // The handlers fall back to these defaults when the property is not configured.

    // Neutral default — the JSON catalog is the place to inject domain-specific phrasing
    // (e.g. "ask me anything about tickets" for a support deployment, "ask me about orders"
    // for a commerce deployment). Keeps the in-code default usable by any consumer.
    public string ConversationalGreeting { get; set; } =
        "Hi! I'm the data copilot. Ask me anything about the database in plain English and " +
        "I'll translate your question into SQL and run it for you.";

    public string ConversationalThanks { get; set; } =
        "You're welcome — happy to help. Anything else you'd like to ask?";

    public string ConversationalFarewell { get; set; } =
        "Goodbye! Come back anytime you need a quick data answer.";

    public string ConversationalCapabilities { get; set; } =
        "Here's what I can do:\n\n" +
        "**📊 Ask data questions in plain English** — counts, lists, breakdowns, comparisons, " +
        "per-group aggregates.\n\n" +
        "**🔍 Find records by similarity or keyword** — semantic search over the indexed entities, " +
        "free-text matching across configured searchable columns.\n\n" +
        "**🗂️ Schema metadata** — \"what tables do we have\", \"what columns are in X\", " +
        "\"how X and Y relate\" (subject to the configured introspection policy).\n\n" +
        "**🧮 Aggregations + grouping** — count, sum, average, min, max, with per-X breakdowns.\n\n" +
        "**🔗 Multi-step questions** — split into sub-questions, chain results from one step into the next.\n\n" +
        "**🛡️ Read-only & safe** — I never modify the database. Write/delete/update requests are refused.\n\n" +
        "Just ask in your own words — I'll figure out the SQL.";

    // ── Conversational humanization — multi-variant reply pools (Bundle 4) ────
    // The handler picks one variant at random from each list. When a list is empty (legacy JSON
    // didn't supply it), the handler falls back to the matching singular field above — fully
    // backward-compatible.
    //
    // Operators can override per deployment to match brand tone, locale, or persona. All variants
    // are schema-agnostic (no entity references); add domain-specific lines by editing
    // copilot-text.json. Keep variants short — chat replies, not paragraphs.

    public List<string> ConversationalGreetingReplies { get; set; } = new()
    {
        "Hi there — ready when you are. What would you like to ask the database?",
        "Hey! I can run queries, find records, or summarize data. What's on your mind?",
        "Hi — ask me anything about the data, in plain English. I'll handle the SQL.",
        "Hello! What data question can I help you with today?",
        "Hi! Tell me what you're trying to find out, and I'll go pull it from the database.",
    };

    public List<string> ConversationalThanksReplies { get; set; } = new()
    {
        "Happy to help — what's next?",
        "Anytime. Anything else I can pull up?",
        "Glad it helped! Let me know if you'd like to dig deeper.",
        "You're welcome — ready for the next question whenever you are.",
        "No problem. Want me to slice it differently or look at something else?",
    };

    public List<string> ConversationalFarewellReplies { get; set; } = new()
    {
        "See you next time! I'll be here when you need data.",
        "Bye — come back whenever you have a question.",
        "Goodbye. Anytime you need a quick data answer, I'm here.",
        "Take care! Drop in whenever you need to query something.",
        "Until next time — happy data hunting!",
    };

    // Capabilities replies stay rich (the markdown structure is the value-add for first-time
    // discovery). The list lets operators rotate phrasings across sessions; the primary reply
    // (above, ConversationalCapabilities) is the default when this list is empty.
    public List<string> ConversationalCapabilitiesReplies { get; set; } = new();

    // ── F3 — Tool-routing stopwords (Tier 2 i18n) ────────────────────────────
    // Comma-separated stopwords filtered out before scoring tool keyword overlap. Default
    // covers English question/auxiliary words; ops can override or extend with locale-specific
    // tokens (Arabic, Spanish, etc.) via copilot-text.json.
    public List<string> ToolRoutingStopwords { get; set; } = new()
    {
        "what", "which", "when", "where", "tell", "show", "give", "find", "get", "latest", "current",
        "today", "please", "need", "want", "with", "from", "into", "about", "this", "that", "have",
        "the", "a", "an", "is", "are", "of", "in", "on", "for", "to", "me", "you", "do", "does",
    };

    // ── Projection-join phrase hints ───────────────────────────────────────────
    // Phrases that signal "I want LEFT-join semantics — preserve root rows even when the related
    // row is missing". When any of these phrases appears in a question, the SpecExtractor
    // post-processes the LLM's spec to force existing non-anti joins[] entries to kind:left.
    // Schema-agnostic — no entity names. Operators can extend with locale-specific synonyms
    // ("avec leurs ...", "مع ...") via copilot-text.json.
    public List<string> LeftJoinPhrases { get; set; } = new()
    {
        "with their",
        "and their",
        "alongside",
        "showing each",
        "along with",
        "including any",
        "and any related",
    };

    // ── Temporal parser culture (B3 — 2026-05-19) ──────────────────────────────
    // Microsoft.Recognizers.Text.DateTime is multilingual. The TemporalParser reads this
    // setting and passes it to RecognizeDateTime() so deployments can switch between
    // English / Chinese / French / Spanish / German / Portuguese / Italian / Japanese /
    // Korean / Turkish / Bulgarian / Hindi / Dutch / Swedish without recompiling.
    // Value should match Microsoft.Recognizers.Text.Culture.* (e.g. "English", "French").
    public string TemporalParserCulture { get; set; } = "English";

    // ── Relative-date keyword vocabulary (planner-prompt hint) ─────────────────
    // The LLM-facing list of recognised relative-date tokens it can put in a filter value.
    // The deterministic TemporalParser uses Microsoft.Recognizers.Text.DateTime (multilingual)
    // for natural language; this list is just the controlled vocabulary the LLM should prefer
    // over inventing SQL expressions in filter values. Operators can extend with locale-specific
    // tokens, fiscal-period tokens ("last_fiscal_quarter"), or domain-specific ones.
    public List<string> RelativeDateKeywords { get; set; } = new()
    {
        "today",
        "yesterday",
        "last_7_days",
        "last_14_days",
        "last_30_days",
        "last_90_days",
        "this_week",
        "this_month",
        "this_quarter",
        "this_year",
        "last_week",
        "last_month",
        "last_quarter",
        "last_year",
        "year_to_date",
        "month_to_date",
    };

    // ── Comparison-question vocabulary ────────────────────────────────────────
    // When any of these phrases appears in the question, the SpecExtractor surfaces a
    // "COMPARISON QUESTION DETECTED" hint at the top of the user prompt so the LLM
    // selects the conditional-aggregation pattern (multiple SUM(CASE WHEN ...) aggregations
    // with per-bucket predicates) instead of duplicating the same COUNT(*) under different
    // aliases (which yields the same number for every bucket — useless for analysts).
    //
    // Schema-agnostic: phrases describe the QUESTION SHAPE, not the entity. Operators can
    // extend with locale-specific comparison vocabulary (e.g. Arabic "مقارنة") via
    // copilot-text.json without touching code.
    public List<string> ComparisonPhrases { get; set; } = new()
    {
        " vs ",
        " vs. ",
        "versus",
        "compare ",
        "compared to",
        "compared with",
        "compared against",
        " vs another ",
        "this month vs last month",
        "this year vs last year",
        "today vs yesterday",
        "year over year",
        "month over month",
        "week over week",
        "year-over-year",
        "month-over-month",
        "week-over-week",
        "yoy",
        "mom",
        "wow",
        "qoq",
        "side by side",
        "side-by-side",
        // Arabic comparison vocabulary
        "مقارنة",
        "مقارنه",
        "مقابل",
    };

    // ── Suggested-prompt fallback pool ────────────────────────────────────────
    // SuggestedPromptProvider falls back to this list when it can't generate spec-shape-aware
    // suggestions (the question failed at preflight / refusal / no spec available). Override
    // per deployment in copilot-text.json — the default is generic enough for any schema but
    // can be customised with domain-specific starter questions ("how many orders are open?").
    public List<string> SuggestedPromptPool { get; set; } = new()
    {
        "Show me a count of the most common entity.",
        "Break down by category or status.",
        "Show me the most recent records.",
        "Find rows matching a keyword.",
        "Show top 5 grouped by a dimension.",
    };

    // ── Tool-routing DB-shape markers ─────────────────────────────────────────
    // ToolHandler uses these markers to decide whether a question looks more like a database
    // query than a tool call. When any marker is present in the question, the tool-resolver
    // raises the matching threshold (so weak keyword overlap with a tool definition can't
    // hijack a database-shaped question) and skips LLM-fallback tool resolution entirely.
    // The default list combines generic SQL verbs with placeholders the host can override.
    // For richer routing, configure via copilot-text.json — the host typically adds its
    // entity synonyms here (the ToolHandler also reads SemanticLayer.Entities[].Synonyms
    // at runtime so this list is a *supplement*, not the only source).
    public List<string> DbShapeMarkers { get; set; } = new()
    {
        "count", "how many", "how much", "show", "list", "find", "what", "which", "between",
        "top", "average", "avg", "sum", "minimum", "maximum", "min", "max", "group by", "per",
    };

    // ── Coverage Checker prompts ──────────────────────────────────────────────
    // System + example prompts for the post-Explainer Coverage Checker. Kept schema-agnostic
    // (no real table or person names) so the same prompt works for any database — ticketing,
    // e-commerce, HR, finance. Operators can rewrite per deployment by overriding in JSON;
    // domain-specific examples can be added here when desired.
    public string CoverageCheckerSystemPrompt { get; set; } =
@"You are a coverage auditor. You receive a user QUESTION, the SQL that ran, the COLUMN NAMES of the result, the FIRST ROW (sample), and the natural-language REPLY.

Your only job: decide whether the REPLY + result COMPLETELY addresses every aspect of the QUESTION.

Output rules (STRICT):
- If complete, output exactly: COMPLETE
- If incomplete, output exactly: MISSING: <short comma-separated list of the question aspects the answer fails to address>
- No other prose. No JSON. No code fences. Just one line.

Examples:
- Q='how many <X>'. Reply contains a count. → COMPLETE
- Q='top N <X> by <metric> and how many <Y> exist'. Reply has the <X> list but no <Y> count. → MISSING: how many <Y> exist
- Q='show me <X> created today'. Reply lists rows but the date filter isn't confirmed. → MISSING: confirmation that the date filter is today
- Q='compare <X> this month vs last month'. Reply has both month counts. → COMPLETE";
}
