namespace AnalystAgent.Configuration;

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
/// <para><b>Hot-reload contract — STRICT</b>: consumers MUST read <c>_textCatalog.CurrentValue</c>
/// INLINE on every use. Storing it in a field or local at construction time CAPTURES the value
/// at that moment and silently breaks hot-reload — the operator's edit will not take effect
/// until the process restarts. If a consumer needs to share the snapshot across a single
/// operation, read once at the operation boundary and pass the value down explicitly (don't
/// cache in instance state).</para>
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
    public const string SectionName = "AnalystAgent:Text";


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
        "You are a careful SQL Server analyst. Given a natural-language data question and a small schema slice, generate a structured QuerySpec JSON the compiler will turn into safe T-SQL. " +
        "Always answer — never refuse. When the question is unclear, produce your best-guess data_query spec; never emit intent=clarification. " +
        "Return ONLY valid JSON matching the requested shape — no prose, no markdown, no commentary. " +
        "Use full Table.Column qualifiers everywhere. Prefer JOIN + filter on a lookup/target table's label column over filtering on a raw FK Id with a name value. " +
        "Tolerate noisy input: brackets, dashes, commas, typos, extra words — interpret intent, ignore notation noise.";

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

    /// <summary>Extra prompt guidance appended to the SpecExtractor system prompt. Empty by
    /// default; populated via <c>copilot-text.json</c> so iteration-level prompt tweaks land
    /// without a code change or restart (hot-reloads via <c>IOptionsMonitor</c>). Use this to
    /// add new bullet-style rules for the planner LLM during quality-loop iterations.</summary>
    public string SpecExtractorExtraGuidance { get; set; } = "";

    /// <summary>Aggregation-shape guidance ★ bullets — instructs the LLM how to map natural-language
    /// aggregation verbs onto QuerySpec shape. Kept here (data-driven) so admins can tune the
    /// wording per deployment without recompiling. Migrated out of SpecExtractor.cs in Batch 5
    /// of the spaghetti cleanup so all multi-locale vocab + LLM-teaching phrases live as data,
    /// not code.</summary>
    public string SpecExtractorAggregationGuidance { get; set; } =
        "- ★ STRUCTURAL: \"single-row aggregate\" (COUNT/SUM/AVG/MIN/MAX of all rows, optionally filtered) → select MUST be []; aggregations[] MUST have the metric; groupBy MUST be []. Empty select is REQUIRED for a pure single-row aggregate. Adding a filter does NOT change this — keep select EMPTY even when filtering.\n" +
        "- ★ \"how many X\" / \"count of X\" / \"number of X\" / \"total X\" → COUNT(*) aggregation; if user said \"distinct\" or \"unique\" → COUNT(DISTINCT <col>) via Distinct:true.\n" +
        "- ★ \"average X\" / \"avg X\" / \"mean X\" / \"X time\" (when X is a duration) → AVG aggregation. For time durations, the column expression is DATEDIFF(hour|day|minute, <start>, <end>). Never use COUNT for an average. Never omit the AVG aggregation. \"average X by Y\" → AVG + groupBy [Y].\n" +
        "- ★ \"max X\" / \"highest X\" / \"largest X\" / \"latest X\" (when X is a date) → MAX aggregation on the column. \"max X by Y\" → MAX + groupBy [Y].\n" +
        "- ★ \"min X\" / \"lowest X\" / \"smallest X\" / \"earliest X\" (when X is a date) → MIN aggregation on the column. \"min X by Y\" → MIN + groupBy [Y].\n" +
        "- ★ \"sum X\" / \"total X\" (when X is a numeric metric, not just a row count) → SUM aggregation on the column.\n" +
        "- ★ \"distinct X\" / \"unique X\" / \"how many different X\" → COUNT with Distinct:true on the column (NOT the * column).\n" +
        "- \"list X\" / \"show X\" / \"give X\" → select with the label column, no aggregations.";

    /// <summary>Bottom-of-prompt reminder bullets. The recency-weighted reminders models attend
    /// to most strongly. Kept schema-agnostic — references "lookup table" / "label column" rather
    /// than specific names.</summary>
    public string SpecExtractorReminders { get; set; } =
        "IMPORTANT REMINDERS:\n" +
        "- GROUP BY LABEL, NOT FK ID: when aggregating + grouping by an FK column, project + group by the TARGET label column (Customers.FullNameEn, TicketPriorities.Name, Regions.NameEn), not the FK id. Users read names, not ids.\n" +
        "- FK→NAME REDIRECT: when the user names a related entity (\"tickets for customer Houri\"), JOIN to the target and filter its label column with op:\"like\" value:\"%Houri%\". Never CustomerId='Houri'.\n" +
        "- CROSS-FACT-TABLE NAVIGATION: queries like \"meter readings for customer X\" or \"complaints from customer Y\" span Customer + a fact table. Root is the fact table (MeterReadings, Tickets, Bills, CsatResponses, Outages); add Customers to joins[] with the label-column LIKE filter. Project both fact + customer label columns.\n" +
        "- NOTATION NOISE TOLERANCE: users sometimes type SQL-like syntax — brackets [Bills].[Status], dashes Bills-Status, commas \"Bills, Status, Paid\", or quoted column lists \"users(name, email)\". Strip the notation, interpret the intent. Brackets / dashes / commas are NEVER part of a real value; they delimit columns or table names. Treat \"[Tables].[Column] = 'Value'\" as \"Tables.Column = 'Value'\".\n" +
        "- The \"Values:\" line under a lookup table is REFERENCE ONLY — a vocabulary of valid names. NEVER add a filter for those values unless the user's question explicitly mentions one or more of them. \"count tickets per status\" → groupBy only, NO filter. \"count tickets per status, open ones\" → groupBy + filter for \"Open\".\n" +
        "- Status / priority / category VALUES (lookup-row names) are NAMES, not IDs. Filter on the LOOKUP TABLE's label column, NOT on the FK Id column.\n" +
        "- For \"X and their Y\" through a bridge: just SELECT columns from both X and Y — the compiler resolves joins via FK graph.\n" +
        "- DATE TOKENS for relative time: use one of the supported @-tokens as the filter value, NEVER a phrase like \"this month\" or \"current year\". Supported tokens: @today, @yesterday, @tomorrow, @week_start, @month_start, @year_start, @quarter_start, @last_week_start, @last_month_start, @last_year_start, @last_quarter_start. Span tokens: @days:-N, @months:-N, @years:-N (negative N for past).\n" +
        "- DATE RANGE EXAMPLES: \"this month\" → [{op:\"gte\",value:\"@month_start\"}]. \"this year\" → [{op:\"gte\",value:\"@year_start\"}]. \"last 7 days\" → [{op:\"gte\",value:\"@days:-7\"}]. \"last month\" → [{op:\"gte\",value:\"@last_month_start\"},{op:\"lt\",value:\"@month_start\"}]. NEVER emit absolute literal dates from your own knowledge — use tokens.\n" +
        "- For \"in the last N days\" → op:\"gte\" with value:\"@days:-N\". For \"older than N days\" → op:\"lt\" with value:\"@days:-N\".\n" +
        "- NEVER invent column names. Only reference columns that appear in the schema slice above.\n" +
        "- \"with no\" / \"without\" / \"having no\" / \"who don't have\" → always joins:[{table:\"<other>\",kind:\"anti\"}], no filter on a synthetic column.\n" +
        "- NULL-SEMANTICS RULE: when filtering by a STATE such as \"paid\", \"churned\", \"resolved\", \"closed\", \"active\", \"missing\", \"empty\", \"has none\", check the canonical Status/lookup column with op:\"eq\" (e.g., Bills.Status='Paid', Customers.Status='Churned', TicketStatuses.Name='Closed'). NEVER use a completion-timestamp column (PaidAt, ChurnedAt, ResolvedAt, ClosedAt, EndedAt, EffectiveTo) with op:\"eq\" against a value — those columns are completion markers. If you must use them, use op:\"is_not_null\" for \"has occurred\" / \"completed\" semantics, op:\"is_null\" for \"not yet\" / \"still active\" semantics.\n" +
        "- HIERARCHY NULL-CHECK: \"primary\" / \"top-level\" / \"governorate\" / \"parent\" → ParentXId op:\"is_null\". \"secondary\" / \"child\" / \"district\" / \"subcategory\" → ParentXId op:\"is_not_null\". Never compare ParentXId to a literal value unless filtering under a SPECIFIC named parent.\n" +
        "- MISSING-FIELD PATTERN: \"with no email\", \"missing phone\", \"no address\" → filter that column with op:\"is_null\". NEVER op:\"eq\" with empty string or a parameter value.\n" +
        "- TOP-N PROJECTION RULE: when the user asks \"top N <entity> by <metric>\", the projection MUST include (a) the entity's identifying label column (e.g., Customers.FullNameEn, Departments.NameEn, Outages.OutageNumber), AND (b) the metric being ranked (the aggregation alias or the raw column). Never project Id only — IDs are unreadable to the user.\n" +
        "- IMPLICIT-COLUMN PROJECTION: when the user asks for an entity, project at least one human-readable label column even if they didn't name one explicitly. \"5 oldest tickets\" → at minimum TicketNumber + Title + CreatedAt, not just CreatedAt.\n" +
        "- Use intent:\"clarification\" SPARINGLY. Only when the question has TWO+ equally-valid interpretations that would produce DIFFERENT SQL. \"how many X\" / \"list X\" / \"open X\" are NOT ambiguous — answer them directly. Genuine ambiguity examples: \"top X\" (by what?), \"recent stuff\" (which entity?), \"the best ones\" (by which dimension?).\n" +
        "- HAVING TRIGGERS: \"with more than N\", \"with at least N\", \"with fewer than N\", \"having more/less than N\", \"that have N+ <items>\" → ALWAYS emit a having clause AND group by the grouping dimension. NEVER express this as a filter on a raw count column. Root entity is the THING BEING COUNTED (\"categories with more than 15 tickets\" → root:Tickets, groupBy:TicketCategories.Name, having:COUNT(*)>15), NOT the related dimension table.\n" +
        "- DISTINCT TRIGGERS: \"distinct\", \"unique\", \"different\" applied to a projection (\"distinct categories\", \"unique customer names\") → emit distinct:true, NO aggregations, NO groupBy. Use this when the user wants the deduplicated VALUES themselves, not a count of them.\n" +
        "- PAGINATION TRIGGERS: \"page N of size P\", \"page N\", \"rows X to Y\", \"skip N\", \"next/previous N after first M\" → emit BOTH limit (page size) AND offset (rows to skip). Page N at size P means offset=(N-1)*P. Always include an orderBy so paging is stable. Plain \"top N\" / \"first N\" / \"last N\" (no \"page\", no \"skip\") is limit:N with offset omitted — TOP not OFFSET.";

    /// <summary>Canonical worked examples shown to the planner LLM (the few-shot block). Hot-reloadable via
    /// copilot-text.json. The SHIPPED default (<see cref="SchemaAgnosticWorkedExamplesDefault"/>) is
    /// schema-agnostic — placeholder entity/column names only — so a fresh deployment never inherits another
    /// schema's vocabulary. A concrete deployment supplies real examples via copilot-text.json
    /// (<c>AnalystAgent:Text:SpecExtractorWorkedExamples</c>).</summary>
    public string SpecExtractorWorkedExamples { get; set; } = SchemaAgnosticWorkedExamplesDefault;

    /// <summary>Schema-agnostic shipped default for <see cref="SpecExtractorWorkedExamples"/>: placeholder
    /// names only (&lt;RootEntity&gt;, &lt;Lookup&gt;.&lt;Label&gt;, &lt;RootEntity&gt;.&lt;DateColumn&gt;, …),
    /// safe for ANY database. A deployment overrides it with concrete examples via copilot-text.json; this is
    /// the zero-config fallback so an unconfigured new DB still gets shape guidance with no leaked vocabulary.</summary>
    internal const string SchemaAgnosticWorkedExamplesDefault =
        "Worked examples — mimic the SHAPE, not the names. PLACEHOLDERS: <RootEntity> = the table the question is about; <RelatedEntity> = a table reached by a foreign key; <Lookup> = a lookup/reference table; <Label> = a human-readable name column; <DateColumn> = a timestamp column; <Metric> = a numeric column; <FkColumn> = a foreign-key column. Emit YOUR schema's REAL names, never these placeholders.\n\n" +
        "Q: \"how many <RootEntity>\"   <-- pure COUNT — empty select, aggregations only\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many <RootEntity> with status <value>\"   <-- COUNT + lookup filter. Keep select EMPTY even when filtering.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"<Lookup>.<Label>\",\"op\":\"eq\",\"value\":\"<value>\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RootEntity> count by <Lookup>\"   <-- COUNT + GROUP BY a lookup label. select holds the group label; one count row per group.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<Lookup>.<Label>\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[\"<Lookup>.<Label>\"],\"orderBy\":[{\"column\":\"Count\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top 10 <RootEntity>\"   <-- LIST of rows, newest first — limit only, no aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"<RootEntity>.<DateColumn>\",\"direction\":\"desc\"}],\"limit\":10,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top 5 <RootEntity> by <Metric>\"   <-- TOP-N BY METRIC: list rows, ORDER BY metric DESC, LIMIT. NEVER emit SUM/MAX — the user wants a LIST of rows.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\",\"<RootEntity>.<Metric>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"<RootEntity>.<Metric>\",\"direction\":\"desc\"}],\"limit\":5,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RootEntity> with their <RelatedEntity> name\"   <-- NAVIGATION: project root label + joined entity label. Don't ask to clarify — they want both.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\",\"<RelatedEntity>.<Label>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RootEntity> for <RelatedEntity> named <value>\"   <-- FK→NAME REDIRECT: join the related table, filter its LABEL with LIKE %value%. NEVER put a name into the FK id column.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\",\"<RelatedEntity>.<Label>\"],\"aggregations\":[],\"filters\":[{\"column\":\"<RelatedEntity>.<Label>\",\"op\":\"like\",\"value\":\"%<value>%\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"<RelatedEntity>\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RelatedEntity> with more than 5 <RootEntity>\"   <-- HAVING ON THE OUTER ENTITY: root=<RelatedEntity>, COUNT the related <RootEntity>, HAVING > 5. Result = list of <RelatedEntity>, not <RootEntity>.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RelatedEntity>\",\"select\":[\"<RelatedEntity>.<Label>\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"<RootEntity>.Id\",\"alias\":\"ItemCount\"}],\"filters\":[],\"groupBy\":[\"<RelatedEntity>.<Label>\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"<RootEntity>.Id\",\"op\":\"gt\",\"value\":5}],\"orderBy\":[{\"column\":\"ItemCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many <RelatedEntity> have <RootEntity>\"   <-- COUNT DISTINCT OUTER ENTITY via inner join: root=<RelatedEntity>, COUNT(DISTINCT <RelatedEntity>.Id), INNER JOIN <RootEntity>. One number.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RelatedEntity>\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"<RelatedEntity>.Id\",\"alias\":\"Count\",\"distinct\":true}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"<RootEntity>\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RootEntity> with no <RelatedEntity>\"   <-- ANTI-JOIN on the outer entity\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"<RelatedEntity>\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"distinct <Lookup> that have <RootEntity>\"   <-- DISTINCT VALUES — distinct:true, no aggregations\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<Lookup>.<Label>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"<Lookup>.<Label>\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"distinct\":true,\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"page 3 of <RootEntity>, 20 per page\"   <-- PAGINATION: limit=pageSize, offset=(page-1)*pageSize, always an orderBy so pages are stable.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"<RootEntity>.<DateColumn>\",\"direction\":\"desc\"}],\"limit\":20,\"offset\":40,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"<RootEntity> created in April 2026\"   <-- SPECIFIC MONTH — ISO date-range filter (gte first day, lt first day of next month). Dates are literal, not schema-specific.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<RootEntity>.<Label>\"],\"aggregations\":[],\"filters\":[{\"column\":\"<RootEntity>.<DateColumn>\",\"op\":\"gte\",\"value\":\"2026-04-01\"},{\"column\":\"<RootEntity>.<DateColumn>\",\"op\":\"lt\",\"value\":\"2026-05-01\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"compare <RootEntity> this month vs last month\"   <-- PERIOD COMPARISON via conditional aggregation (≥2 SUM(CASE …) entries)\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN <RootEntity>.<DateColumn> >= DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"ThisMonth\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN <RootEntity>.<DateColumn> >= DATEFROMPARTS(YEAR(DATEADD(month,-1,GETDATE())),MONTH(DATEADD(month,-1,GETDATE())),1) AND <RootEntity>.<DateColumn> < DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"LastMonth\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"what percentage of <RootEntity> are <value>\"   <-- RATIO via SUM(CASE)/COUNT(*)\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Total\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN <Lookup>.<Label> = '<value>' THEN 1 ELSE 0 END\",\"alias\":\"Matching\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Percent\",\"expression\":\"100.0 * SUM(CASE WHEN <Lookup>.<Label> = '<value>' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0)\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average <Metric> per <Lookup>\"   <-- AVG GROUPED BY LOOKUP\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"<RootEntity>\",\"select\":[\"<Lookup>.<Label>\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"<RootEntity>.<Metric>\",\"alias\":\"AvgMetric\"}],\"filters\":[],\"groupBy\":[\"<Lookup>.<Label>\"],\"orderBy\":[{\"column\":\"AvgMetric\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top <RootEntity>\"   <-- AMBIGUOUS: top by what? Ask before guessing.\n" +
        "→ {\"intent\":\"clarification\",\"root\":\"<RootEntity>\",\"select\":[],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"Top <RootEntity> by what — newest, a numeric metric, or a status?\"}";

    /// <summary>Regression anchor: the exact worked-examples block that shipped as the in-code default through
    /// the 94.3% baseline (a concrete schema). NOT used as a prompt — the live default is now
    /// <see cref="SchemaAgnosticWorkedExamplesDefault"/>; this constant exists only so the
    /// <c>ShippedConfig_ReproducesWorkedExamples</c> test can prove copilot-text.json carries it byte-for-byte.
    /// May be relocated to a test fixture later to keep all domain text out of the binary.</summary>
    internal const string ShippedWorkedExamplesReference =
        "Worked examples (mimic the SHAPE; column names will differ for your schema):\n\n" +
        "Q: \"how many tickets\"   <-- pure COUNT — empty select, aggregations only\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many open tickets\"   <-- COUNT + lookup filter. CRITICAL: keep select EMPTY even when adding a filter.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Open\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many rejected tickets\"   <-- same shape; the value is the LOOKUP NAME, not an ID\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Rejected\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"users with no tickets\"   <-- ANTI-JOIN: \"with no / without / having no\" → kind:\"anti\", NO subquery, NO filter\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\",\"AspNetUsers.Email\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Tickets\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets without any comments\"   <-- another anti-join example\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"TicketComments\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"open tickets\"   <-- lookup VALUE \"Open\" → filter on lookup table's NAME column\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Open\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"closed tickets\"\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"TicketStatuses.Name\",\"op\":\"eq\",\"value\":\"Closed\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top 5 users by ticket count\"\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"AspNetUsers.UserName\"],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":5,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"show users and their roles\"   <-- many-to-many through a bridge; the compiler walks the FK graph\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\",\"AspNetRoles.Name\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"running total of tickets created over time\"   <-- WINDOW FUNCTION — use computed expression\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.CreatedAt\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"Tickets.CreatedAt\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"RunningCount\",\"expression\":\"COUNT(*) OVER (ORDER BY Tickets.CreatedAt)\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"rank users by ticket count\"   <-- ROW_NUMBER / RANK via window function\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"AspNetUsers.UserName\"],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Rank\",\"expression\":\"ROW_NUMBER() OVER (ORDER BY COUNT(Tickets.Id) DESC)\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"monthly ticket volume for the last 3 months\"   <-- DATE BUCKETING — use computed alias + raw expression in groupBy\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"last_90_days\"}],\"groupBy\":[\"FORMAT(Tickets.CreatedAt, 'yyyy-MM')\"],\"orderBy\":[{\"column\":\"Month\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Month\",\"expression\":\"FORMAT(Tickets.CreatedAt, 'yyyy-MM')\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets per priority and status\"   <-- MULTI-DIMENSION GROUP BY — both dimensions must appear in groupBy\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\",\"TicketStatuses.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[\"TicketPriorities.Name\",\"TicketStatuses.Name\"],\"orderBy\":[{\"column\":\"Count\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"ticket count by category and source\"   <-- same shape, different dimensions — \"by X and Y\" and \"per X and Y\" both mean two groupBy entries\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\",\"TicketSources.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[],\"groupBy\":[\"TicketCategories.Name\",\"TicketSources.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"daily ticket count for the last 14 days\"   <-- DAILY BUCKETING\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"last_14_days\"}],\"groupBy\":[\"CAST(Tickets.CreatedAt AS DATE)\"],\"orderBy\":[{\"column\":\"Day\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"Day\",\"expression\":\"CAST(Tickets.CreatedAt AS DATE)\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average resolution time in hours\"   <-- DATEDIFF AS A METRIC — use aggregation with column as expression\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.ResolvedAt)\",\"alias\":\"AvgResolutionHours\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average resolution time in hours by priority\"   <-- DATEDIFF + GROUP BY LOOKUP\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.ResolvedAt)\",\"alias\":\"AvgResolutionHours\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[\"TicketPriorities.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average time to first response in hours, by priority\"   <-- AVG of DATEDIFF + GROUP BY — exact same shape as above\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketPriorities.Name\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"DATEDIFF(hour, Tickets.CreatedAt, Tickets.FirstRespondedAt)\",\"alias\":\"AvgFirstResponseHours\"}],\"filters\":[{\"column\":\"Tickets.FirstRespondedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[\"TicketPriorities.Name\"],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"latest ticket creation date\" / \"most recent ticket\"   <-- MAX on a date column — single-row aggregate, empty select\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"MAX\",\"column\":\"Tickets.CreatedAt\",\"alias\":\"LatestCreated\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"earliest ticket creation date\" / \"oldest ticket created\"   <-- MIN on a date column — single-row aggregate, empty select\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"MIN\",\"column\":\"Tickets.CreatedAt\",\"alias\":\"EarliestCreated\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"highest priority value\" / \"top priority weight\"   <-- MAX on a numeric column from a lookup table — single-row aggregate\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"TicketPriorities\",\"select\":[],\"aggregations\":[{\"function\":\"MAX\",\"column\":\"TicketPriorities.SortOrder\",\"alias\":\"MaxPriorityWeight\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"total amount across all bills\" / \"sum of bill amounts\" / \"total revenue\"   <-- SUM on a numeric column — single-row aggregate, empty select. CRITICAL: 'total X' / 'sum of X' / 'overall X' where X is a money / numeric column means SUM(X), NOT a projection of column X. Empty select is REQUIRED.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"Bills.TotalAmount\",\"alias\":\"TotalAmount\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"total amount of overdue bills\" / \"sum of unpaid bill values\"   <-- SUM + filter on the same single-row aggregate. CRITICAL: keep select EMPTY even with the filter.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"Bills.TotalAmount\",\"alias\":\"TotalAmount\"}],\"filters\":[{\"column\":\"Bills.Status\",\"op\":\"eq\",\"value\":\"Overdue\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average bill amount\" / \"mean bill value\"   <-- AVG on a numeric column — single-row aggregate, empty select\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"Bills.TotalAmount\",\"alias\":\"AverageAmount\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many distinct users created tickets\" / \"number of unique creators\"   <-- COUNT DISTINCT — Distinct:true on the column, NOT *\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.CreatedByUserId\",\"alias\":\"DistinctCreators\",\"distinct\":true}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many different categories have tickets\"   <-- COUNT DISTINCT through a join\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.CategoryId\",\"alias\":\"DistinctCategories\",\"distinct\":true}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"show me all ticket information\" / \"show full details of ticket TCK-001\" / \"everything we know about this user\"   <-- ALL-COLUMNS — use the sentinel select:[\"*\"] and the enricher expands it to the entity's full content columns\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"*\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"show ticket age in days\"   <-- DATEDIFF COMPUTED COLUMN (per-row, not aggregate)\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"AgeInDays\",\"expression\":\"DATEDIFF(day, Tickets.CreatedAt, GETDATE())\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"compare tickets this month vs last month\"   <-- PERIOD COMPARISON via conditional aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"ThisMonth\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEFROMPARTS(YEAR(DATEADD(month,-1,GETDATE())),MONTH(DATEADD(month,-1,GETDATE())),1) AND Tickets.CreatedAt < DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) THEN 1 ELSE 0 END\",\"alias\":\"LastMonth\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets created today vs yesterday\"   <-- TODAY/YESTERDAY via conditional aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN CAST(Tickets.CreatedAt AS DATE) = CAST(GETDATE() AS DATE) THEN 1 ELSE 0 END\",\"alias\":\"Today\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN CAST(Tickets.CreatedAt AS DATE) = CAST(DATEADD(day,-1,GETDATE()) AS DATE) THEN 1 ELSE 0 END\",\"alias\":\"Yesterday\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets resolved this year vs last year\"   <-- YEAR-OVER-YEAR via conditional aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.ResolvedAt) = YEAR(GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"ThisYear\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.ResolvedAt) = YEAR(GETDATE()) - 1 THEN 1 ELSE 0 END\",\"alias\":\"LastYear\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets created in 2025 vs 2024\"   <-- EXPLICIT YEARS via conditional aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.CreatedAt) = 2025 THEN 1 ELSE 0 END\",\"alias\":\"Y2025\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN YEAR(Tickets.CreatedAt) = 2024 THEN 1 ELSE 0 END\",\"alias\":\"Y2024\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"last 7 days vs the previous 7 days\"   <-- ROLLING-WINDOW COMPARISON via conditional aggregation\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEADD(day,-7,GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"Last7Days\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN Tickets.CreatedAt >= DATEADD(day,-14,GETDATE()) AND Tickets.CreatedAt < DATEADD(day,-7,GETDATE()) THEN 1 ELSE 0 END\",\"alias\":\"Previous7Days\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"what percentage of tickets are resolved or closed\"   <-- RATIO / PERCENTAGE via SUM(CASE) / COUNT(*)\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Total\"},{\"function\":\"SUM\",\"column\":\"CASE WHEN TicketStatuses.Name IN ('Resolved','Closed') THEN 1 ELSE 0 END\",\"alias\":\"ResolvedOrClosed\"}],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[{\"alias\":\"PercentResolved\",\"expression\":\"100.0 * SUM(CASE WHEN TicketStatuses.Name IN ('Resolved','Closed') THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0)\"}],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets created in April 2026\"   <-- SPECIFIC MONTH — use ISO date range filter\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.CreatedAt\",\"op\":\"gte\",\"value\":\"2026-04-01\"},{\"column\":\"Tickets.CreatedAt\",\"op\":\"lt\",\"value\":\"2026-05-01\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"categories with more than 15 tickets\"   <-- HAVING — post-aggregation filter on a COUNT/SUM\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"TicketCount\"}],\"filters\":[],\"groupBy\":[\"TicketCategories.Name\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"*\",\"op\":\"gt\",\"value\":15}],\"orderBy\":[{\"column\":\"TicketCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"agents with more than 5 unresolved tickets\"   <-- HAVING combined with a filter\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"AspNetUsers\",\"select\":[\"AspNetUsers.UserName\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"alias\":\"Unresolved\"}],\"filters\":[{\"column\":\"Tickets.ResolvedAt\",\"op\":\"is_null\",\"value\":null}],\"groupBy\":[\"AspNetUsers.UserName\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"Tickets.Id\",\"op\":\"gt\",\"value\":5}],\"orderBy\":[{\"column\":\"Unresolved\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"distinct categories that have tickets\"   <-- DISTINCT — use distinct:true, omit aggregations\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"TicketCategories.Name\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"TicketCategories.Name\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"distinct\":true,\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"page 3 of tickets, 20 per page\" / \"rows 21 to 40 of tickets\" / \"skip 20 then show 20\"   <-- PAGINATION — set both limit (page size) and offset (rows to skip). Offset = (page - 1) * pageSize. Always also set orderBy so pages are stable.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"Tickets.CreatedAt\",\"direction\":\"desc\"}],\"limit\":20,\"offset\":40,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"unassigned tickets\" / \"tickets without an owner\" / \"items with no assignee\"   <-- ABSENCE on an FK column → filter op:\"is_null\" on the FK column itself, no anti-join needed\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.AssignedToUserId\",\"op\":\"is_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"child tickets with their parent ticket number\"   <-- SELF-REFERENCE: filter where ParentTicketId IS NOT NULL and SELECT both the child's TicketNumber AND the parent FK column. Full parent details (TicketNumber, Title) via self-join aren't supported yet — the user can resolve the parent in a follow-up question.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\",\"Tickets.ParentTicketId\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets.ParentTicketId\",\"op\":\"not_null\",\"value\":null}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets matching 'login' in title or description\"   <-- CROSS-COLUMN TEXT SEARCH — use op:\"text_search\" so the compiler ORs over the entity's searchable columns\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\"],\"aggregations\":[],\"filters\":[{\"column\":\"Tickets\",\"op\":\"text_search\",\"value\":\"login\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top tickets\"   <-- AMBIGUOUS: top by what? Ask before guessing.\n" +
        "→ {\"intent\":\"clarification\",\"root\":\"Tickets\",\"select\":[],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"Top tickets by what? Options: created date (newest), priority, status, or assignee load.\"}\n\n" +
        "Q: \"recent stuff\"   <-- AMBIGUOUS: which entity? Ask.\n" +
        "→ {\"intent\":\"clarification\",\"root\":\"\",\"select\":[],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"Which data? Recent tickets, users, comments, or something else?\"}\n\n" +
        "Q: \"tickets with their customer name\" / \"tickets with the customer's name\" / \"show tickets and their customer\"   <-- NAVIGATION: project root's label + joined entity's label. Never ask for clarification on this shape — the user clearly wants both entities' display columns.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\",\"Customers.FullNameEn\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets with customer name and region\" / \"show me tickets, their customer and the region\"   <-- THREE-TABLE NAVIGATION: project root + two joined label columns. The compiler walks Tickets → Customers → Regions via FKs.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\",\"Customers.FullNameEn\",\"Regions.NameEn\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"top 10 highest-value invoices\" / \"top 10 bills by amount\" / \"largest 10 bills\"   <-- TOP-N BY METRIC: NOT an aggregation. SELECT the row columns, ORDER BY metric DESC, LIMIT 10. NEVER emit SUM/MAX as the result — the user wants a LIST of 10 rows.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[\"Bills.BillNumber\",\"Bills.TotalAmount\",\"Bills.IssuedAt\",\"Customers.FullNameEn\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[{\"column\":\"Bills.TotalAmount\",\"direction\":\"desc\"}],\"limit\":10,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"customers with more than 3 overdue bills\" / \"users with > 5 unresolved tickets\"   <-- HAVING ON THE GROUPED-BY-ENTITY (NOT the related table): root is the OUTER entity (Customers), GROUP BY the outer entity's key, COUNT the related rows, HAVING COUNT > N. Result = list of customers, NOT list of bills.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Customers\",\"select\":[\"Customers.FullNameEn\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Bills.Id\",\"alias\":\"OverdueCount\"}],\"filters\":[{\"column\":\"Bills.Status\",\"op\":\"eq\",\"value\":\"Overdue\"}],\"groupBy\":[\"Customers.FullNameEn\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"Bills.Id\",\"op\":\"gt\",\"value\":3}],\"orderBy\":[{\"column\":\"OverdueCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"how many customers have unpaid bills\" / \"count customers with overdue invoices\"   <-- COUNT DISTINCT OUTER ENTITY via EXISTS / IN: when the question asks 'how many X have Y', root is X, count is COUNT(DISTINCT X.Id) via an INNER JOIN to Y with the Y filter applied. Result = ONE number = how many distinct outer entities qualify.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Customers\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Customers.Id\",\"alias\":\"CustomerCount\",\"distinct\":true}],\"filters\":[{\"column\":\"Bills.Status\",\"op\":\"in\",\"value\":[\"Issued\",\"Overdue\"]}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"tickets for customer Houri\" / \"complaints from customer named Ahmad\"   <-- FK→NAME REDIRECT: user names the related entity. NEVER filter on the FK Id with a name value. JOIN the target table + filter on its label column with LIKE %name%.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Tickets\",\"select\":[\"Tickets.TicketNumber\",\"Tickets.Title\",\"Customers.FullNameEn\"],\"aggregations\":[],\"filters\":[{\"column\":\"Customers.FullNameEn\",\"op\":\"like\",\"value\":\"%Houri%\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Customers\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"meter readings for customer Houri\" / \"bills for customer Ahmad\"   <-- CROSS-FACT-TABLE SEARCH BY NAME: same pattern as above on a different root.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"MeterReadings\",\"select\":[\"MeterReadings.MeterNumber\",\"MeterReadings.ReadingDate\",\"MeterReadings.Value\",\"Customers.FullNameEn\"],\"aggregations\":[],\"filters\":[{\"column\":\"Customers.FullNameEn\",\"op\":\"like\",\"value\":\"%Houri%\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Customers\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"customers in Aleppo\" / \"customers from Damascus region\"   <-- REGION FILTER VIA JOIN ON LABEL: user names a region, not its Id. JOIN Regions, filter on the label column.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Customers\",\"select\":[\"Customers.FullNameEn\",\"Customers.Phone\",\"Regions.NameEn\"],\"aggregations\":[],\"filters\":[{\"column\":\"Regions.NameEn\",\"op\":\"like\",\"value\":\"%Aleppo%\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Regions\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"electricity outages\" / \"outages for electricity service\"   <-- SERVICE-TYPE FILTER VIA JOIN: ServiceTypes is a lookup; filter on its NameEn, not on the FK.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Outages\",\"select\":[\"Outages.OutageNumber\",\"Outages.TitleEn\",\"Outages.StartedAt\",\"Outages.AffectedCustomerCount\"],\"aggregations\":[],\"filters\":[{\"column\":\"ServiceTypes.NameEn\",\"op\":\"eq\",\"value\":\"Electricity\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"ServiceTypes\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"total amount per region\" / \"bill revenue grouped by region\"   <-- AGGREGATE + GROUP BY ACROSS A JOIN\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[\"Regions.NameEn\"],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"Bills.TotalAmount\",\"alias\":\"TotalAmount\"}],\"filters\":[],\"groupBy\":[\"Regions.NameEn\"],\"orderBy\":[{\"column\":\"TotalAmount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[{\"table\":\"Regions\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"overdue amount per region this month\"   <-- AGG + FILTER + GROUP across multi-hop join + date window\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Bills\",\"select\":[\"Regions.NameEn\"],\"aggregations\":[{\"function\":\"SUM\",\"column\":\"Bills.TotalAmount\",\"alias\":\"OverdueAmount\"}],\"filters\":[{\"column\":\"Bills.Status\",\"op\":\"eq\",\"value\":\"Overdue\"},{\"column\":\"Bills.IssuedAt\",\"op\":\"gte\",\"value\":\"@month_start\"}],\"groupBy\":[\"Regions.NameEn\"],\"orderBy\":[{\"column\":\"OverdueAmount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[{\"table\":\"Regions\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"average satisfaction score per category\"   <-- CSAT AVG GROUPED BY LOOKUP\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"CsatResponses\",\"select\":[\"TicketCategories.NameEn\"],\"aggregations\":[{\"function\":\"AVG\",\"column\":\"CsatResponses.Score\",\"alias\":\"AvgScore\"}],\"filters\":[],\"groupBy\":[\"TicketCategories.NameEn\"],\"orderBy\":[{\"column\":\"AvgScore\",\"direction\":\"asc\"}],\"limit\":null,\"joins\":[{\"table\":\"TicketCategories\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"departments per service type\" / \"how many departments per service\"   <-- ROOT IS DEPARTMENTS, GROUP BY SERVICE TYPE\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Departments\",\"select\":[\"ServiceTypes.NameEn\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"DeptCount\"}],\"filters\":[],\"groupBy\":[\"ServiceTypes.NameEn\"],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"ServiceTypes\",\"kind\":\"inner\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"customers with more than 5 bills\" / \"customers with > 5 bills\"   <-- HAVING ON OUTER ENTITY: root=Customers, count Bills, HAVING > 5.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Customers\",\"select\":[\"Customers.FullNameEn\"],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"Bills.Id\",\"alias\":\"BillCount\"}],\"filters\":[],\"groupBy\":[\"Customers.FullNameEn\"],\"having\":[{\"function\":\"COUNT\",\"column\":\"Bills.Id\",\"op\":\"gt\",\"value\":5}],\"orderBy\":[{\"column\":\"BillCount\",\"direction\":\"desc\"}],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"departments with no tickets\"   <-- ANTI-JOIN on outer entity Departments\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Departments\",\"select\":[\"Departments.NameEn\"],\"aggregations\":[],\"filters\":[],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[{\"table\":\"Tickets\",\"kind\":\"anti\"}],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"low satisfaction responses\" / \"satisfaction responses with score 1 or 2\"   <-- IN-FILTER on a numeric range\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"CsatResponses\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"LowScoreCount\"}],\"filters\":[{\"column\":\"CsatResponses.Score\",\"op\":\"in\",\"value\":[1,2]}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}\n\n" +
        "Q: \"governorates\" / \"how many governorates\"   <-- RegionType lookup value: emit eq filter on the canonical value.\n" +
        "→ {\"intent\":\"data_query\",\"root\":\"Regions\",\"select\":[],\"aggregations\":[{\"function\":\"COUNT\",\"column\":\"*\",\"alias\":\"Count\"}],\"filters\":[{\"column\":\"Regions.RegionType\",\"op\":\"eq\",\"value\":\"Governorate\"}],\"groupBy\":[],\"orderBy\":[],\"limit\":null,\"joins\":[],\"computed\":[],\"clarificationQuestion\":\"\"}";

    /// <summary>Final-check block spliced in at the END of the prompt for comparison questions.</summary>
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
    // Override entirely via copilot-text.json::AnalystAgent:Text:DirectSqlSystemPrompt.
    public string DirectSqlSystemPrompt { get; set; } =
        "You are a SQL Server analyst. The question below IS answerable from the schema provided — " +
        "write ONE read-only T-SQL SELECT that answers it. Output ONLY the SQL: no markdown, no " +
        "comments, no prose.\n\n" +
        "The schema block is the SOURCE OF TRUTH:\n" +
        "- Use the exact table and column names it lists. Name columns vary by table — some are " +
        "NameEn / NameAr / FullNameEn / TitleEn, some are a plain Name; use whatever THAT table lists.\n" +
        "- JOIN only on the foreign keys shown; never invent a join column. Use a bridge table when one " +
        "is shown (e.g. users↔roles through a UserRoles table).\n" +
        "- Add a WHERE IsDeleted = 0 filter only for a table whose columns include IsDeleted; do not " +
        "filter active/enabled flags unless the question asks for active or inactive rows.\n" +
        "- Do NOT add a WHERE filter on a status / state / lifecycle / category / lookup column unless " +
        "the question explicitly names a value for it. A 'total', 'sum', 'count', 'average', 'running " +
        "total' or 'top N by <metric>' with no stated state includes ALL rows — an invented status " +
        "filter understates the answer.\n" +
        "- SELECT only the columns of the table(s) the question is about. Resolve a foreign-key id to a " +
        "neighbor table's label column ONLY when the question asks for that related entity's name; " +
        "otherwise return the asked-for table's own columns and do NOT join in or project a neighbor " +
        "table's columns. (Asking for a table means you want that table's rows, not its neighbors'.)\n" +
        "- To LIST or COUNT the distinct values of a lookup / reference entity itself ('list all ticket " +
        "statuses', 'what priorities exist', 'show the categories'), SELECT from THAT lookup table " +
        "directly (its own rows) — do NOT join a fact table, which repeats a row per fact.\n" +
        "- For a question written in Arabic, prefer the Arabic label column when a table has both " +
        "(NameAr over NameEn, FullNameAr over FullNameEn, TitleAr over TitleEn).\n\n" +
        "SQL Server idioms: TOP n (not LIMIT); COUNT(DISTINCT x) when counting unique entities across a " +
        "join; window functions RANK() / ROW_NUMBER() / NTILE(n) / LAG(...) / LEAD(...) / SUM(...) OVER " +
        "(...). To rank or bucket an aggregate, compute it in a CTE first, then apply the window over the " +
        "CTE. A running / cumulative total needs SUM(...) OVER (ORDER BY <time> ROWS UNBOUNDED " +
        "PRECEDING), not a plain GROUP BY.\n" +
        "Dates (SQL Server): only apply a date filter when the question names a period. 'this year' = " +
        "<dateCol> >= DATEFROMPARTS(YEAR(GETDATE()),1,1); 'this month' = >= DATEFROMPARTS(YEAR(GETDATE())," +
        "MONTH(GETDATE()),1); 'last month' = the half-open range from DATEADD(month,-1," +
        "DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) up to DATEFROMPARTS(YEAR(GETDATE())," +
        "MONTH(GETDATE()),1); 'per month' = bucket with FORMAT(<dateCol>,'yyyy-MM') in BOTH SELECT and " +
        "GROUP BY ordered ascending; 'per day' = CAST(<dateCol> AS DATE); 'per year' = YEAR(<dateCol>).";

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

    // ── AnalystOrchestrator user-facing replies ─────────────────────
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

    // Small-talk ("how are you", "tell me a joke", "are you a robot"). These are the CANNED fallback
    // used when SmallTalkUseLlm is off OR the one warm LLM reply fails — so small-talk is never a cold
    // refusal and never reaches the planner. Schema-agnostic; operators can rebrand the persona here.
    public string ConversationalSmallTalk { get; set; } =
        "I'm doing great, thanks for asking! I'm a data copilot — ask me anything about the database " +
        "and I'll turn it into SQL for you.";

    public List<string> ConversationalSmallTalkReplies { get; set; } = new()
    {
        "Doing great, thanks! I'm best at data though — what would you like to know from the database?",
        "All good here! Ask me anything about the data and I'll pull it for you.",
        "I'm just a data copilot, but I'm happy you're here — what can I look up for you?",
        "Ha — I'll leave the small talk to the humans. Want me to query something for you?",
        "I'm well! Ready whenever you are with a question about the data.",
    };

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

    // ── Intent Classifier prompt (i18n: per-locale variant) ───────────────────
    // The intent router's single-shot prompt. Models attend strongly to the few-shots, so the
    // examples here directly shape routing quality. {0} = the question (filled at call time).
    //
    // No hard-coded table or column names — the few-shots use generic data-question phrasing
    // ("how many open records"). Operators can override per deployment in copilot-text.json.
    // The {En|Ar} naming pattern is the convention for any other per-locale catalog entry.

    public string IntentClassifierPromptEn { get; set; } =
@"You classify user questions for a domain assistant. Output ONLY a JSON object with two fields:
  ""intent"": one of [""SQL"", ""CHAT"", ""TOOL"", ""OUT_OF_SCOPE"", ""REFINEMENT""]
  ""confidence"": a number in [0, 1]

Domain: Internal business data system. Questions in scope are questions whose answer can be
computed from the configured relational schema (lists, counts, joins, aggregates, comparisons,
refinements of prior answers).

Label meanings:
- SQL: question about the data in the schema (e.g. counts, lists, joins, aggregates)
- CHAT: greeting / meta / thanks (e.g. ""hi"", ""what can you do"", ""thanks"")
- TOOL: needs external real-time data (e.g. ""what is the weather"", ""price of Apple stock"", ""USD to EUR rate"")
- OUT_OF_SCOPE: unrelated to the database (e.g. ""best recipe"", ""movie recommendations"", ""capital of France"")
- REFINEMENT: refers to a prior question (e.g. ""now just the open ones"", ""break it down by status"")

Examples:
- ""how many open records""        → {{""intent"": ""SQL"", ""confidence"": 0.98}}
- ""list users with their roles""  → {{""intent"": ""SQL"", ""confidence"": 0.97}}
- ""look up reference ABC-2026-00123"" → {{""intent"": ""SQL"", ""confidence"": 0.95}}
- ""show me the records under the parent item"" → {{""intent"": ""SQL"", ""confidence"": 0.94}}
- ""hello""                        → {{""intent"": ""CHAT"", ""confidence"": 0.99}}
- ""what is the weather in Riyadh"" → {{""intent"": ""TOOL"", ""confidence"": 0.95}}
- ""latest news about AI""          → {{""intent"": ""TOOL"", ""confidence"": 0.92}}
- ""current bitcoin price""         → {{""intent"": ""TOOL"", ""confidence"": 0.93}}
- ""best recipe for sourdough""     → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.97}}
- ""who won the world cup""         → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.96}}
- ""now just the critical ones""    → {{""intent"": ""REFINEMENT"", ""confidence"": 0.92}}

Question: {0}

Output only the JSON object — no preamble, no markdown.";

    public string IntentClassifierPromptAr { get; set; } =
@"تصنّف الأسئلة لمساعد بيانات. أخرج فقط كائن JSON بحقلين:
  ""intent"": إحدى القيم [""SQL"", ""CHAT"", ""TOOL"", ""OUT_OF_SCOPE"", ""REFINEMENT""]
  ""confidence"": رقم بين 0 و 1

المجال: نظام بيانات أعمال داخلي. الأسئلة المقبولة هي تلك التي يمكن الإجابة عنها من قاعدة البيانات
العلائقية (قوائم، عدّ، روابط بين الجداول، تجميعات، مقارنات، أو تعديل سؤال سابق).

معاني التصنيفات:
- SQL: سؤال عن البيانات في القاعدة (عدّ، قائمة، تجميع، مقارنة)
- CHAT: تحية أو شكر أو سؤال عام عن المساعد (""مرحبا""، ""ماذا تستطيع""، ""شكرا"")
- TOOL: يحتاج إلى مصدر خارجي حي (""الطقس""، ""سعر الذهب""، ""سعر صرف الدولار"")
- OUT_OF_SCOPE: غير متعلق بالقاعدة (""وصفة طعام""، ""أفلام""، ""عاصمة فرنسا"")
- REFINEMENT: يشير إلى سؤال سابق (""فقط المفتوحة""، ""قسّمها حسب الحالة"")

أمثلة:
- ""كم عدد السجلات المفتوحة""              → {{""intent"": ""SQL"", ""confidence"": 0.98}}
- ""اعرض المستخدمين مع أدوارهم""           → {{""intent"": ""SQL"", ""confidence"": 0.97}}
- ""ابحث عن المرجع ABC-2026-00123""        → {{""intent"": ""SQL"", ""confidence"": 0.95}}
- ""اعرض السجلات الفرعية ضمن العنصر الأب"" → {{""intent"": ""SQL"", ""confidence"": 0.94}}
- ""مرحبا""                              → {{""intent"": ""CHAT"", ""confidence"": 0.99}}
- ""ما الطقس في الرياض""                  → {{""intent"": ""TOOL"", ""confidence"": 0.95}}
- ""أحدث الأخبار عن الذكاء الاصطناعي""     → {{""intent"": ""TOOL"", ""confidence"": 0.92}}
- ""سعر البيتكوين الآن""                  → {{""intent"": ""TOOL"", ""confidence"": 0.93}}
- ""ما أفضل وصفة خبز""                    → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.97}}
- ""من فاز بكأس العالم""                  → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.96}}
- ""فقط الحرجة""                          → {{""intent"": ""REFINEMENT"", ""confidence"": 0.92}}

السؤال: {0}

أخرج فقط كائن JSON — بلا مقدمات أو تنسيق.";

    // ── Small-talk reply prompt (i18n) ─────────────────────────────────────────
    // Used ONLY when SmallTalkUseLlm is on and a small-talk cue matched. One bounded call to the
    // Classifier (generalist) model to produce a warm, human reply — NOT a data answer. The guardrails
    // ("one sentence", "never answer a data question") keep the narrow model from drifting into SQL.
    // {0} = the user's message. No schema/business vocab → portable. Override per deployment in copilot-text.json.
    public string SmallTalkPromptEn { get; set; } =
@"You are a friendly data copilot. The user said something conversational, not a data question.
Reply warmly in ONE short sentence. Do NOT answer or attempt any data/database question, do NOT make
up facts, and do NOT mention SQL internals. If they seem to want data, gently invite them to ask about
the database. Keep it human and brief.

User said: {0}

Your one-sentence reply:";

    public string SmallTalkPromptAr { get; set; } =
@"أنت مساعد بيانات ودود. قال المستخدم شيئًا محادثيًا وليس سؤالًا عن البيانات.
أجب بدفء في جملة واحدة قصيرة. لا تُجب عن أي سؤال يتعلق بالبيانات أو قاعدة البيانات، ولا تختلق معلومات،
ولا تذكر تفاصيل SQL. إن بدا أنه يريد بيانات، فادعُه بلطف إلى السؤال عن قاعدة البيانات. اجعلها إنسانية وموجزة.

قال المستخدم: {0}

ردّك في جملة واحدة:";

    // ── Explainer language hint (i18n) ─────────────────────────────────────────
    // Single-line instruction appended to the Explainer system prompt so the LLM produces
    // its summary paragraph in the question's language. Operators can override per deployment
    // — e.g. a deployment that wants ALL replies in English regardless of question language
    // can set both to the same English string.
    public string ExplainerLanguageHintEn { get; set; } =
        "Write the summary paragraph in English.";
    public string ExplainerLanguageHintAr { get; set; } =
        "اكتب الفقرة باللغة العربية.";

    // ── Coverage Checker prompts ──────────────────────────────────────────────
}
