namespace SuperAdminCopilot.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceOpsAI.Services.AI.Providers.Roles;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Compilation;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Eval;
using SuperAdminCopilot.Eval.Paraphrase;
using SuperAdminCopilot.Execution;
using SuperAdminCopilot.Explanation;
using SuperAdminCopilot.HostBridge;
using SuperAdminCopilot.Pipeline;
using SuperAdminCopilot.Pipeline.EntityResolution;
using SuperAdminCopilot.Pipeline.Stages;
using SuperAdminCopilot.Pipeline.Stages.Decomposed;
using SuperAdminCopilot.Retrieval;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using SuperAdminCopilot.Validation;

/// <summary>
/// Single DI entry point for the in-host SuperAdminCopilot. The host's Program.cs calls
/// <c>builder.Services.AddSuperAdminCopilot(builder.Configuration)</c> exactly once.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSuperAdminCopilot(this IServiceCollection services, IConfiguration configuration)
    {
        // P1 #59 — fail fast at startup when a new pipeline stage is missing its graph mapping.
        // Catches the dev mistake of adding a Step* constant without wiring it into
        // StageNames.StageToGraphNode + _InvestigationPipelineGraph.cshtml. Cheap reflection
        // scan that runs once per process boot.
        Pipeline.StageNames.AssertGraphCoverage();

        // Single-source settings — copilot-options.json under Areas/SuperAdminCopilot/Configuration/.
        // Replaces the previous appsettings.json "SuperAdminCopilot" section binding.
        // The file is the ONLY place runtime knobs live for this module.
        var copilotConfig = CopilotOptionsLoader.BuildConfiguration();
        services.AddOptions<CopilotOptions>()
            .Bind(copilotConfig.GetSection(CopilotOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Profile overlay (Phase 4) — applies the named preset from copilot-options.json's
        // Profiles{} block on top of the bound values. Runs BEFORE the SystemSettings overlay
        // so admin-edited operator settings always win over the engineering profile baseline.
        services.PostConfigure<CopilotOptions>(opts => CopilotOptionsLoader.ApplyProfile(copilotConfig, opts));
        services.AddSingleton<IPostConfigureOptions<CopilotOptions>, HostCopilotOptionsConfigurator>();
        // Declarative linguistic cues — Configuration/linguistic-cues.json. Loaded once at
        // startup, compiled to regex objects, exposed via ILinguisticCuesProvider. Replaces the
        // hardcoded regex sets previously embedded across SpecRepair phases. To add a new
        // language or synonym, edit the JSON; no recompilation required. Schema is documented
        // in Configuration/LinguisticCues.cs.
        services.AddSingleton<ILinguisticCuesProvider, LinguisticCuesProvider>();

        // SQL-dialect abstraction. Compiler talks to ISqlDialect instead of hardcoding T-SQL.
        // Default binding = MssqlDialect (current production target). Swap to PostgresDialect
        // (or any future implementation) by changing this single line — no compiler rewrite.
        // The dialect is unit-tested exhaustively in SqlDialectTests; each new dialect MUST
        // pass every assertion there before being eligible for production use.
        services.AddSingleton<Compilation.Dialects.ISqlDialect, Compilation.Dialects.MssqlDialect>();
        // D.3 — externalized text catalog. Reload-on-change is enabled by the host's default
        // appsettings.json registration; admins inject IOptionsMonitor<CopilotTextCatalog> and
        // read .CurrentValue on each access. A standalone copilot-text.json is shipped as a
        // reference template (documented in Configuration/copilot-text.json).
        services.Configure<Configuration.CopilotTextCatalog>(
            configuration.GetSection(Configuration.CopilotTextCatalog.SectionName));
        // Write-intent and FK-role rules — both hot-reloaded from their own JSON files,
        // registered as separate AddJsonFile sources in Program.cs. Operators add a verb / role
        // / pattern by editing the JSON; no recompile, no host restart.
        services.Configure<Configuration.WriteIntentOptions>(
            configuration.GetSection(Configuration.WriteIntentOptions.SectionName));
        services.Configure<Configuration.FkRoleOptions>(
            configuration.GetSection(Configuration.FkRoleOptions.SectionName));
        services.AddMemoryCache(); // backing store for the result cache (Tier 2 #13)

        // Bridge adapters — the ONLY classes that touch host types.
        services.AddSingleton<IConnectionStringProvider, HostConnectionStringProvider>();
        services.AddSingleton<ILlmClient, HostAiProviderLlmClient>();
        // Register the real embedder as a concrete dependency, decorate via CachingTextEmbedder
        // so every ITextEmbedder consumer transparently benefits from the per-question cache.
        // Same wire-up pattern as decoration without a third-party DecoratorExtensions package.
        services.AddSingleton<HostAiProviderEmbedder>();
        services.AddSingleton<ITextEmbedder>(sp => new CachingTextEmbedder(sp.GetRequiredService<HostAiProviderEmbedder>()));
        services.AddScoped<ISemanticSearch, HostSemanticSearchBridge>();
        services.AddScoped<ITraceSink, HostTraceSink>();
        // Async fire-and-forget trace persistence — the pipeline enqueues; the hosted
        // service drains and writes to its own DbContext. Removes ~100-500ms of synchronous
        // DB I/O from the user's response path. See TraceWriteQueue / TraceWriteHostedService.
        services.AddSingleton<HostBridge.ITraceWriteQueue, HostBridge.TraceWriteQueue>();
        services.AddHostedService<HostBridge.TraceWriteHostedService>();
        // Phase 6 portability hook — abstracts chat-message persistence so the API controller
        // no longer reaches into the host's DbContext directly. Replace HostConversationPersister
        // when porting the copilot to a host with a different conversation schema.
        services.AddScoped<IConversationPersister, HostConversationPersister>();
        services.AddScoped<ISuperAdminCopilotChatBridge, SuperAdminCopilotChatBridge>();
        // Live progress broadcaster — SignalR-backed in the production host so the chat
        // UI's Claude-style timeline updates in real time as each stage runs. Replace with
        // NullPipelineStepProgressSink.Instance in eval/test composition roots.
        services.AddSingleton<IPipelineStepProgressSink, SignalRPipelineStepProgressSink>();

        // Schema (Phase 1).
        services.AddSingleton<IDbConnectionFactory, SqlServerConnectionFactory>();
        services.AddSingleton<ISchemaIntrospector, SchemaIntrospector>();
        services.AddSingleton<IEntityCatalog, EntityCatalog>();
        services.AddSingleton<IForeignKeyGraph>(sp => sp.GetRequiredService<IEntityCatalog>().Graph);
        services.AddSingleton<ICopilotSchemaAccessPolicy, CopilotSchemaAccessPolicy>();
        services.AddSingleton<ISchemaMetadataMap, SchemaMetadataMap>();
        // Layer 2 schema knowledge — heuristics-only inference of soft-delete / PII / label /
        // date-role / bridge / lookup / person flags, written to a single JSON file. Consumed by
        // the new retriever + spec extractor (replaces hand-maintained SemanticLayer.json sections).
        // The runner is a singleton tracking the user-triggered generation job state for the UI.
        services.AddSingleton<ISchemaInferenceGenerator, SchemaInferenceGenerator>();
        services.AddSingleton<ISchemaInferenceJobRunner, SchemaInferenceJobRunner>();
        // Runtime schema knowledge — singleton snapshot loaded once at startup, reloaded after
        // every successful generation. Consumed by the new retriever, spec extractor, compiler.
        services.AddSingleton<ISchemaKnowledge, SchemaKnowledge>();
        // New embedding-based table retriever for the redesigned pipeline. Embeds every table
        // once (cached by schema hash) and cosine-ranks against the question on each request.
        services.AddSingleton<Retrieval.ISchemaSemanticRetriever, Retrieval.SchemaSemanticRetriever>();
        // New core LLM step: question + retrieved tables → QuerySpec JSON (form-filling).
        // Replaces the heavy LlmPlanner prompt with a tight, locally-runnable one.
        // Scoped because it consumes IPastQuestionStore (Scoped, DbContext-backed) for the
        // persistent-learning few-shot retrieval. Per-request allocation cost is microseconds.
        services.AddScoped<Pipeline.Stages.ISpecExtractor, Pipeline.Stages.SpecExtractor>();
        // ── SpecRepair: consolidated LLM-output mutation pipeline ──────────────────────
        // One owner for every mutation between SpecExtractor (raw LLM output) and SqlCompiler
        // (deterministic SQL synthesis). Phases run in the order registered here — order is
        // the contract; see Areas/SuperAdminCopilot/Pipeline/SpecRepair/README.md for the
        // full phase table and what bug class each covers.
        services.Configure<Pipeline.SpecRepair.SpecRepairOptions>(
            configuration.GetSection("SpecRepair"));
        // Phase 07.δ — Arabic question dispatcher. Fires FIRST. Detects "كم عدد X", "اظهر X",
        // "إجمالي X" and authoritatively sets spec.Root + clears the LLM's Customer-default
        // Select / Joins / Filters. Closes the 8-fail Arabic routing cluster from the heavy
        // 101-case suite. Schema-driven via semantic-layer entity synonyms — no entity names
        // hardcoded.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ArabicQuestionDispatchPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.HoistInlineAggregatesPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.NormalizeAggregationFunctionPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.NormalizeFilterOperatorPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.StripQuotedFilterValuesPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InferRootFromQuestionPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InferRootFromColumnRefsPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.AutoQualifyColumnsPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.DropAggregatedColumnsFromSelectPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.DropFilterContradictingGroupByPhase>();
        // DropSpuriousGroupBy: LLM emits MAX/MIN/SUM/AVG alongside GROUP BY of root.PK/NK,
        // producing per-row stats instead of a scalar. Drops the GROUP BY + identifier select items.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.DropSpuriousGroupByForScalarAggregationPhase>();
        // MapValueSynonyms: canonicalise filter values via SemanticLayer.ResolveSynonymValue
        // ("urgent" → "Critical", "pending payment" → "Issued"). Driven by semantic-layer.json synonyms section.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.MapValueSynonymsPhase>();
        // ConvertFkEqualsNameToJoin: WHERE FK_Id = 'Houri' → join target table + filter on target's
        // label column. Generic via InferredTable.ForeignKeysOut + semantic-layer LabelColumn —
        // works on any FK→entity pair in any schema. MUST run before ConvertNameFilterToLikePhase
        // so the redirected filter then gets the eq→like conversion.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ConvertFkEqualsNameToJoinPhase>();
        // ConvertNameFilterToLike: filter eq on a searchable column → like '%value%'. Covers
        // "customer Houri" / "Aleppo governorate" partial-name matching using semantic-layer's
        // searchableColumns declaration. Critical for admin name-search workflows.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ConvertNameFilterToLikePhase>();
        // Anti-join upgrade: when question says "X with no Y" but LLM emitted INNER JOIN, flip to "anti".
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.UpgradeInnerJoinToAntiJoinPhase>();
        // ConvertTopNAggregationToList: "top 10 highest bills" → LLM emits SUM(TotalAmount) LIMIT 10
        // instead of a row list. Detects the pattern and converts to bare select + ORDER BY DESC.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ConvertTopNAggregationToListPhase>();
        // EnforceCountOuterEntity: "how many customers have unpaid bills" — LLM picks Bills and
        // counts bills (325). Swap root to the outer entity from the question text and force
        // COUNT(DISTINCT outer.Id). Driven by semantic-layer entity synonym lookup.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.EnforceCountOuterEntityPhase>();
        // Phase 07.β — analyst-quality output enrichment. Auto-projects label columns when
        // SELECT references an FK (so "CategoryId" gets joined with "TicketCategories.Name").
        // Solves the "I don't want raw IDs" complaint: every FK reference brings in its
        // human-readable counterpart automatically.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.EnrichSelectWithLabelsPhase>();
        // NOTE: EnsureDisplayColumnsPhase moved later in the pipeline (after natural-key /
        // root-inference phases) so spec.Root is guaranteed set before the expansion runs.
        // Phase 07.γ — anti-join detection from question text. Closes the "without any X"
        // failure where the LLM emits no join and returns everything. Multilingual: handles
        // English "without any / with no / missing" + Arabic "بدون / ليس لديهم".
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectAntiJoinFromQuestionPhase>();
        // Phase 08 — universal text-search filter. Detects "X containing Y" / "X about Y" /
        // "X mentioning Y" (EN + AR triggers) and emits LIKE against every column in the
        // resolved entity's semantic-layer.json `searchableColumns`. No keyword list in code;
        // adding a new entity = one JSON edit. Weak-tier crutch — auto-skips on stronger models.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectTextSearchFilterPhase>();
        // ReplaceWrongAggregation: question says AVG/SUM/MAX/MIN but spec emitted COUNT(*).
        // Sibling to ForceAggregationOnCount — that one fires when aggregations is empty;
        // this one fires when it's non-empty but wrong. MUST run before ForceAggregationOnCount
        // so the latter sees the now-correct shape.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ReplaceWrongAggregationPhase>();
        // DedupAggregations: drops duplicate aggregation entries sharing the same function+column
        // pair. The LLM sometimes emits SUM(amount), SUM(amount) — both render and the compiler
        // suffixes _2 on the second alias. Runs AFTER aggregation-shape fixes so we de-dupe the
        // final list, not an intermediate.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.DedupAggregationsPhase>();
        // InjectNaturalKeyFilterFromQuestion: question text contains a token matching the entity's
        // naturalKeyFormat regex (e.g. TKT-00050) → add WHERE NaturalKeyCol = 'token'. Universal
        // and schema-driven via semantic-layer.json's naturalKeyColumn + naturalKeyFormat fields.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectNaturalKeyFilterFromQuestionPhase>();
        // ApplyConceptPatterns: question text matches a semantic-layer ConceptPattern trigger
        // (e.g. "overdue", "stale", "backlog") AND the pattern's filters target a referenced
        // table → inject those filters. Schema-driven; new concepts are added by editing
        // semantic-layer.json's conceptPatterns section, no code change.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ApplyConceptPatternsPhase>();
        // InjectTimeSeriesBucketing: TIMESERIES intent ("by month" / "weekly" / "trend over time")
        // and no date GROUP BY yet → inject FORMAT/DATEADD bucket + GROUP BY + COUNT(*). Bucket
        // expression rendered against the entity's semantic-layer "default" date role (CreatedAt
        // for Tickets, IssuedAt for Bills, etc.) so it works on any new schema.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectTimeSeriesBucketingPhase>();
        // Phase 07 — TimeIntent first. Single owner of temporal-intent injection. Fills the
        // date filter on spec.Filters / spec.PeriodComparisons from a structured TimeIntent.
        // The two legacy temporal phases below detect the populated filter and bow out via
        // their existing "skip if filter already on date column" early-return — no duplicates.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.TimeIntentSpecRepairPhase>();
        // InjectTemporalFilterFromQuestion: question text has a temporal keyword ("today",
        // "this week", "last 30 days", "Q1 of this year") AND the root entity has a default date
        // column AND the spec has NO filter on that column → inject the matching filter using the
        // compiler's existing @-token vocabulary. Catches the common silent-failure where the LLM
        // drops the time constraint entirely (e.g. "bills issued this week" → returns all
        // Issued-status bills regardless of date).
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectTemporalFilterFromQuestionPhase>();
        // RewriteEmptyToIsNull: question contains "no X" / "without X" / "missing X" AND the LLM
        // emitted a `column = ''` filter on a nullable column → rewrite to `column IS NULL`.
        // Also injects the IS NULL filter when no filter on the absence-noun column exists, AND
        // strips dangling hallucinated filters on unmentioned tables.
        // ORDER: MUST run BEFORE InjectLookupValueFilter — otherwise Pass-3 would strip a
        // freshly-injected lookup-name filter on a table the user didn't name (e.g. "open
        // tickets without email" — TicketStatuses.Name='Open' would be dropped because the
        // user said "open" not "TicketStatuses").
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.RewriteEmptyToIsNullPhase>();
        // InjectLookupValueFilterFromQuestion: scan question text for whole-word matches against
        // the actual values of FK-reachable lookup tables (Damascus / Aleppo / Water / Critical /
        // Open, etc.) → inject WHERE LookupTable.LabelCol = value when no such filter exists.
        // Catches dropped categorical constraints the LLM forgets to emit. Schema-driven via
        // EntityCatalog.GetAllLookupValues and the FK graph — no hardcoded entity names.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.InjectLookupValueFilterFromQuestionPhase>();
        // ForceCountDistinctOnDistinctQuestion: question contains "how many distinct/unique X"
        // → ensure aggregation is COUNT(DISTINCT col), not COUNT(*) or GROUP BY. Picks the column
        // from the existing aggregation, the entity's natural-key, or "Id" in that order.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ForceCountDistinctOnDistinctQuestionPhase>();
        // ForceTopNRowsOverMaxMin: question is TOPN ("top 5 X by Y" / "bottom 3" / "newest 10")
        // AND spec emitted a single MAX/MIN aggregation AND has a Limit → swap to TOP-N rows
        // ORDER BY metric. Catches the "MAX(BaseAmount) instead of TOP 10 ORDER BY IssuedAt DESC"
        // failure mode.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ForceTopNRowsOverMaxMinPhase>();
        // DedupContradictoryFilters: remove same-column overlapping/contradictory filter pairs
        // (Status='X' AND Status IN ('Y','Z'), duplicate filters, etc.). Without this the AND'd
        // filters silently degrade to 0 rows or an overly narrow result.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.DedupContradictoryFiltersPhase>();
        // StripFiltersOnAllTime: question contains "ever" / "all time" / "in history" → strip
        // the root entity's date filter AND any status-narrowing filter. The user's "any" intent
        // shouldn't be over-narrowed by the LLM's defaults.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.StripFiltersOnAllTimePhase>();
        // SwapDateColumnByVerb: question verb signals a specific lifecycle event (resolved /
        // closed / started / ended / issued / paid / signed up) → swap filters/orderby/groupby
        // from the LLM's chosen date column to the verb-implied date column from semantic-layer
        // dateRoles. Fixes "ticket volume" using ResolvedAt instead of CreatedAt, etc.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.SwapDateColumnByVerbPhase>();
        // EnsureOrderByForTopN: spec has Limit but no OrderBy → inject a stable default order
        // using the root entity's default date column DESC (or Id DESC). Prevents nondeterministic
        // TOP N results.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.EnsureOrderByForTopNPhase>();
        // ── Joins/ (file-organized by concern) ───────────────────────────────────────────────
        // DetectOverJoin: strip joined tables not referenced anywhere in SELECT/FILTER/GROUPBY.
        // Fixes the "A-COUNT-006 added irrelevant Customers join" silent over-count.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Joins.DetectOverJoinPhase>();
        // ForceCountDistinctOnFanOutJoin: COUNT(*) with a 1:N join → COUNT(DISTINCT root.Id) so
        // the result counts root rows, not Cartesian-product rows from the fan-out.
        // Aligned with Looker/Holistics/Honeydew BI-tool conventions for fan-out handling.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Joins.ForceCountDistinctOnFanOutJoinPhase>();
        // CrossEntityCountRootInference: 'how many customers who opened a ticket' → root=Tickets,
        // COUNT(DISTINCT Tickets.CustomerId). Catches LLM picking the dimension as root when the
        // user actually wants distinct fact-rows by dimension key.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Joins.CrossEntityCountRootInferencePhase>();
        // ── Filters/ (deeper concern split) ──────────────────────────────────────────────────
        // NegationFilter: 'not in Damascus' / 'except gas' / 'excluding X' → flip eq/in to neq/notin.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Filters.NegationFilterPhase>();
        // RangeFilterFromQuestion: 'between X and Y' / 'over X' / 'less than X' → numeric filter
        // on root entity's first money/amount/total column.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Filters.RangeFilterFromQuestionPhase>();
        // SpecificYearMonthFilter: 'in 2025' / 'in March 2025' / 'since 2024' / 'before 2025' →
        // half-open date range filter. Inspired by TIMEX3 / SUTime temporal-expression standards.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Filters.SpecificYearMonthFilterPhase>();
        // ── Aggregation/ ─────────────────────────────────────────────────────────────────────
        // CaseWhenComparison: diagnostic phase — detects COMPARE-shape with wrong aggregate
        // (single COUNT instead of SUM(CASE WHEN) per leg). Stage-1 grounding handles the
        // proper emission upstream; this phase logs uncovered cases for analysis.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Aggregation.CaseWhenComparisonPhase>();
        // ApplyDerivedMetricHint: override the LLM's wrong aggregation column when the question
        // implies a derived metric (ticket-age → DATEDIFF; revenue → Bills.TotalAmount; etc.).
        // Backstop for the SpecExtractor prompt hint — runs even when the LLM ignores the hint.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Aggregation.ApplyDerivedMetricHintPhase>();
        // StripUnsolicitedStatusOnSuperlative: when question is a superlative aggregate
        // (largest/smallest/peak X) without explicit status cue, strip the auto-added Status
        // filter. Catches "largest single bill" → MIN/MAX(BaseAmount) WHERE Status=Issued
        // — user asked for overall max, not max among Issued.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Aggregation.StripUnsolicitedStatusOnSuperlativePhase>();
        // RejectInvalidAggregationOnType: AVG/SUM on datetime → wrap in DATEDIFF; bit → CAST to
        // INT; nvarchar → drop and replace with COUNT(*). Prevents "Operand data type datetime2
        // is invalid for avg operator" SQL execution failures (B-WIN-lag-2 in session 109).
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Aggregation.RejectInvalidAggregationOnTypePhase>();
        // EnsureSelectInGroupBy: SELECT has a non-aggregated column that's missing from GROUP BY
        // → append it. Prevents "Column X is invalid in the select list because it is not
        // contained in either an aggregate function or the GROUP BY clause" (B-WIN-run-2).
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.Selection.EnsureSelectInGroupByPhase>();
        // ExpectationVerifier registration — used by the verifier runner script + future
        // assessment-summary plumbing to score traces by Expected* fields on each case.
        services.AddSingleton<Eval.ExpectationVerifier.IExpectationVerifier, Eval.ExpectationVerifier.ExpectationVerifier>();

        // ── Stage-1 grounding (principled redesign, DIN-SQL / CHESS / ValueNet style) ──────────
        // Pre-resolves schema-linked values, temporal slots, natural keys, intent shape, and
        // lifecycle verb date-role BEFORE the LLM call. The resolved context is rendered into the
        // SpecExtractor prompt as ground truth — the LLM is told to use it verbatim instead of
        // guessing. SpecRepair phases stay as a backstop; they become no-ops when the LLM uses
        // the grounded context correctly.
        services.AddSingleton<Grounding.IValueLinker, Grounding.ValueLinker>();
        services.AddSingleton<Grounding.IQuestionGrounder, Grounding.QuestionGrounder>();
        // Force-aggregation runs LAST — after root inference + column qualification, so it
        // can pick the right column to aggregate against. Covers the "paraphrased aggregation"
        // class: "largest bill we ever sent" → MAX, "peak meter reading" → MAX, etc.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ForceAggregationOnCountQuestionPhase>();
        // Phase 07.γ — sibling to the count-question coercion. Catches non-count aggregate
        // verbs (sum / total / average / max / min / highest / lowest, EN + AR) when the
        // LLM emits raw rows instead of aggregated. Solves: "total bill amount" → 1000 rows
        // becomes 1 row with SUM(TotalAmount). Universal, no entity hardcoding.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ForceNonCountAggregationPhase>();
        // Phase 07.γ — ensure top N / first N / bottom N translates to LIMIT N (EN + AR).
        // Closes the LIM-TOP family where local LLM forgets to emit a LIMIT clause and the
        // result returns 1000 rows (default cap) instead of N. Universal pattern detector.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.ForceTopNLimitPhase>();
        // Phase 07.ε — analyst-quality column completeness. Registered LAST so spec.Root is
        // guaranteed set (by InferRootFromQuestion / InjectNaturalKeyFilter / EnforceCountOuter
        // etc.) before column expansion runs. For LIST-shape queries with empty or narrow
        // projection, expand SELECT to the entity's displayColumns + auto-project FK label
        // names via LEFT JOINs. Universal, driven by config + FK naming patterns — no entity
        // names hardcoded.
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepairPhase, Pipeline.SpecRepair.Phases.EnsureDisplayColumnsPhase>();
        services.AddSingleton<Pipeline.SpecRepair.ISpecRepair, Pipeline.SpecRepair.SpecRepair>();
        // Escape valve — when form-filling QuerySpec can't express the shape (window funcs,
        // recursive CTEs, complex analytics), ask the LLM to write raw T-SQL. Output still
        // passes the SqlAstValidator + ReadOnlyExecutor read-only guard. Last-resort fallback.
        services.AddSingleton<Pipeline.Stages.ILlmDirectSqlEmitter, Pipeline.Stages.LlmDirectSqlEmitter>();
        // Phase 3 Step 12 — Question Shape Classifier. Deterministic keyword-based
        // pre-routing: window-function / running-total / rank questions bypass the
        // form-filling QuerySpec path and go straight to LlmDirectSqlEmitter.
        services.AddSingleton<Pipeline.Stages.IQuestionShapeClassifier, Pipeline.Stages.QuestionShapeClassifier>();
        // Phase 7a — Prompt Shape Classifier. Deterministic 8-shape classifier (COUNT, TOPN,
        // AGGREGATE, TIMESERIES, COMPARE, LOOKUP, JOIN, FILTER) that drives example-bank
        // selection in the prompt assembler. Patterns live in Configuration/Prompts/shape-classifier.json.
        services.AddSingleton<Pipeline.Prompts.IPromptShapeClassifier, Pipeline.Prompts.DeterministicPromptShapeClassifier>();
        // LLM-fallback decomposer for compound questions the regex HeuristicDecomposer misses.
        // The new orchestrator tries regex first (fast); falls back to this when regex returns null.
        services.AddSingleton<Pipeline.Stages.ILlmDecomposer, Pipeline.Stages.LlmDecomposer>();
        // Phase 2.3 multi-turn refinement memory — in-process per-conversation last-spec cache.
        services.AddSingleton<Pipeline.Stages.IConversationSpecMemory, Pipeline.Stages.ConversationSpecMemory>();
        // Heuristic refinement detector — decides if a new question is a refinement of the
        // previous turn's spec ("actually only the open ones") or an independent question.
        services.AddSingleton<Pipeline.Stages.IRefinementDetector, Pipeline.Stages.RefinementDetector>();
        // Chart-type suggester — heuristic, no LLM. Picks "kpi" / "bar" / "line" / "pie" / "table"
        // based on result-set shape. Host UI may use it to render a default view.
        services.AddSingleton<Pipeline.Stages.IChartTypeSuggester, Pipeline.Stages.ChartTypeSuggester>();
        // Deterministic spec enrichment — fills the LLM's gaps with schema-aware rules
        // (smart projection, FK label resolution, group-by-in-select). The "do hard-code
        // what's universal, use AI for intent" architectural fix.
        services.AddSingleton<Pipeline.Stages.ISpecEnricher, Pipeline.Stages.SpecEnricher>();
        // Verified queries — Snowflake-style hand-curated (question, SQL) catalog. When a new
        // question's embedding cosine-matches a verified entry above the configured threshold,
        // the orchestrator uses the SQL directly and skips the LLM. Empty file = inactive.
        services.AddSingleton<Retrieval.IVerifiedQueryStore, Retrieval.VerifiedQueryStore>();
        services.AddSingleton<Retrieval.IVerifiedQueryMatcher, Retrieval.VerifiedQueryMatcher>();
        // Writer service for the "Promote to Trusted" admin flow — appends to the JSON
        // file behind IVerifiedQueryStore and triggers a Reload so the matcher picks up
        // the new entry without a process restart.
        services.AddSingleton<Retrieval.IVerifiedQueryWriter, Retrieval.VerifiedQueryWriter>();
        // Phase 6 schema-drift linter — cross-checks JSON-config table/column references
        // against the live DB. Run on demand via ISchemaDriftLinter.Lint(); operators can
        // wire an admin endpoint or a startup hook (off by default).
        services.AddSingleton<Schema.ISchemaDriftLinter, Schema.SchemaDriftLinter>();
        // Phase 6 Step 21 — schema diff service for the admin UI. Computes added /
        // removed tables + columns between the live DB and the cached JSON snapshot.
        // Consumed by the SchemaDiffController which renders a clickable report.
        services.AddSingleton<Schema.ISchemaDiffService, Schema.SchemaDiffService>();
        // Investigation Workflow tab — backend-authoritative graph builder. Consumes the
        // canonical PipelineArchitecture + the run's PipelineStep[] and produces a single
        // WorkflowGraph the frontend renders verbatim. Replaces the hardcoded route
        // detection + STEP_DESCRIPTIONS that lived in Razor / investigation-pipeline.js.
        services.AddSingleton<Pipeline.IWorkflowGraphBuilder, Pipeline.WorkflowGraphBuilder>();

        // Semantic layer + few-shot example bank (§4 of the abstraction guide).
        services.AddSingleton<ISemanticLayer, SemanticLayer>();
        services.AddSingleton<IFewShotExampleStore, FewShotExampleStore>();

        // Phase 5 — Generic Resolver Layer.
        // Thin, interface-typed facades over ISemanticLayer + ISemanticCandidateResolver +
        // IForeignKeyGraph. Each resolver handles ONE concern: entity lookup, column lookup,
        // metric/dimension lookup, value-synonym rewrite, temporal column mapping, FK-graph
        // path traversal, or natural-key extraction. Registered Singleton — no mutable state.
        services.AddSingleton<Semantic.IEntityResolver, Semantic.EntityResolver>();
        services.AddSingleton<Semantic.IColumnResolver, Semantic.ColumnResolver>();
        services.AddSingleton<Semantic.IMetricResolver, Semantic.MetricResolver>();
        services.AddSingleton<Semantic.IDimensionResolver, Semantic.DimensionResolver>();
        services.AddSingleton<Semantic.IValueSynonymResolver, Semantic.ValueSynonymResolver>();
        services.AddSingleton<Semantic.ITemporalResolver, Semantic.TemporalResolver>();
        services.AddSingleton<Semantic.IRelationshipResolver, Semantic.RelationshipResolver>();
        services.AddSingleton<Semantic.INaturalKeyResolver, Semantic.NaturalKeyResolver>();
        // Temporal expression parser (D.1) — wraps Microsoft.Recognizers for richer expressions
        // the QuestionShapeEngine's regex doesn't cover. Optional consumer; registered so future
        // stages can inject it without re-wiring DI.
        services.AddSingleton<ITemporalParser, TemporalParser>();

        // Phase 07 — TimeIntent extractor. Consolidates all temporal parsing into a single
        // module that fills QuerySpec.TimeIntent BEFORE the LLM planner runs. Retires the
        // 5 competing temporal phases (SpecificYearMonth / InjectTemporalFilter / etc.).
        services.AddSingleton<ITimeIntentExtractor, TimeIntentExtractor>();

        // Department embedding matcher (D.2) — cosine-rank fallback when the JSON synonym dictionary
        // misses a word the user typed. Async API, opt-in for consumers (EntityRootGuard /
        // IntentNormalizer can fall back to this when their token-based match returns null).
        services.AddSingleton<IEntityEmbeddingMatcher, EntityEmbeddingMatcher>();
        services.AddSingleton<ISemanticCandidateResolver, SemanticCandidateResolver>();

        // Pipeline.
        // Retriever: register KeywordRetriever as the concrete fallback so VectorRetriever can
        // depend on it; then pick which one services IRetriever based on the UseVectorRetriever
        // option. This keeps both code paths warm — flipping the option requires no DI change.
        services.AddSingleton<KeywordRetriever>();
        services.AddSingleton<VectorRetriever>();
        services.AddSingleton<IRetriever>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CopilotOptions>>().Value;
            return opts.UseVectorRetriever
                ? sp.GetRequiredService<VectorRetriever>()
                : sp.GetRequiredService<KeywordRetriever>();
        });
        services.AddSingleton<JoinResolver>();
        services.AddSingleton<ICompiler, SqlCompiler>();
        services.AddSingleton<IValidator, SqlAstValidator>();
        services.AddScoped<ICopilotConfigurationValidator, CopilotConfigurationValidator>();
        // SQL Intent Guard — post-compile intent-vs-SQL check. Rejects shapes the syntactic
        // validator can't catch: COUNT for a list question, AVG missing on average questions,
        // GROUP BY root.Id (degenerate template), exact-date question missing the literal date.
        services.AddSingleton<Pipeline.Stages.ISqlIntentGuard, Pipeline.Stages.SqlIntentGuard>();
        services.AddSingleton<Pipeline.Stages.IQuerySpecAccessPolicyValidator, Pipeline.Stages.QuerySpecAccessPolicyValidator>();

        // Executor pipeline: Pii → CostGate → Cache → ReadOnly. Each wrapper short-circuits when
        // its CopilotOptions switch is off, so the configured chain is always all four layers
        // and overhead is paid only when explicitly enabled. PiiRedactor sits OUTERMOST so it
        // sees the final result row set regardless of cache hits / cost-gate refusals.
        services.AddSingleton<ReadOnlyExecutor>();
        services.AddSingleton<CachingExecutor>();
        services.AddSingleton<CostGateExecutor>();
        services.AddSingleton<IExecutor>(sp => new PiiRedactingExecutor(
            sp.GetRequiredService<CostGateExecutor>(),
            sp.GetRequiredService<ISemanticLayer>(),
            sp.GetRequiredService<ICopilotSchemaAccessPolicy>(),
            sp.GetRequiredService<ILogger<PiiRedactingExecutor>>()));

        // Operational guard (kill switch + rate limit, Tier 2 #16).
        services.AddSingleton<IOperationalGuard, OperationalGuard>();

        // Write-intent preflight — deterministic gate that refuses delete/insert/update/...
        // questions in milliseconds before any LLM call. Multilingual verb list, entity-agnostic.
        services.AddSingleton<Pipeline.Stages.IWriteIntentGuard, Pipeline.Stages.WriteIntentGuard>();

        // LLM-based intent router. Runs BEFORE the SQL-generation path and labels each question
        // as SQL / CHAT / TOOL / OUT_OF_SCOPE / REFINEMENT. Critical for protecting a narrow
        // fine-tuned SQL specialist from inputs it can't handle gracefully (catastrophic
        // forgetting on chat/tool/OOS). Uses the AiClassifierModel workload — initially the same
        // model as Copilot; can be swapped to a smaller faster classifier via the UI later.
        services.AddSingleton<Pipeline.Stages.IIntentClassifier, Pipeline.Stages.IntentClassifier>();

        // Positive scope-confidence gate. Replaces the prior regex-bank OutOfScopeHandler.
        // Defines OOS as the residual when all fast paths (Conversational/Knowledge/Tool/VQ)
        // miss AND both scope signals (verified-query cosine, schema-linker top score) are
        // below their floors. Schema-agnostic by construction — no hardcoded vocab anywhere,
        // every floor lives in CopilotOptions and every decision derives from config (the
        // schema-inferred map, the verified-query catalog, the tool registry).
        services.AddSingleton<Pipeline.Stages.IScopeConfidenceGate, Pipeline.Stages.ScopeConfidenceGate>();

        // Coverage Check — post-Explainer LLM verification that the reply fully addresses
        // the question. Scoped because it consumes IRetryBudget (per-request).
        services.AddScoped<Pipeline.Stages.ICoverageChecker, Pipeline.Stages.CoverageChecker>();

        // Explainer: LlmExplainer is the primary IExplainer; TemplatedExplainer is registered
        // as a concrete type so LlmExplainer can fall back to it on LLM failure or trivial
        // result sets without going through the IExplainer indirection.
        services.AddSingleton<TemplatedExplainer>();
        // LlmExplainer is Scoped because it consumes IRetryBudget (per-request). Same reasoning
        // as the planner: μs of allocation, dwarfed by the LLM call cost.
        services.AddScoped<IExplainer, LlmExplainer>();

        // F1.a + F1.d — conversational + knowledge-match handlers run before the planner so
        // greetings, "what can you do", "what is a ticket" etc. answer cheaply without an LLM call.
        // Each handler is registered TWICE: once under its specific interface (so the orchestrator
        // can dispatch to it after the router picks its branch) and once under IRoutingProbe (so
        // the HybridIntentRouter can ask "is this question yours?" without running the handler).
        services.AddSingleton<Pipeline.Stages.ConversationalHandler>();
        services.AddSingleton<Pipeline.Stages.IConversationalHandler>(sp => sp.GetRequiredService<Pipeline.Stages.ConversationalHandler>());

        services.AddSingleton<Pipeline.Stages.KnowledgeMatchHandler>();
        services.AddSingleton<Pipeline.Stages.IKnowledgeMatchHandler>(sp => sp.GetRequiredService<Pipeline.Stages.KnowledgeMatchHandler>());

        services.AddSingleton<Pipeline.Stages.DeterministicMetadataHandler>();
        services.AddSingleton<Pipeline.Stages.IMetadataHandler>(sp => sp.GetRequiredService<Pipeline.Stages.DeterministicMetadataHandler>());

        services.AddScoped<Pipeline.Stages.SemanticSearchHandler>();
        services.AddScoped<Pipeline.Stages.ISemanticSearchHandler>(sp => sp.GetRequiredService<Pipeline.Stages.SemanticSearchHandler>());

        // Decomposer (regex) + ResultShapeValidator + ConversationContext + SuggestedPromptProvider
        // remain useful. The shape-engine, IntentCoercer, LiteralDateGuard, EntityRootGuard,
        // SchemaAmbiguityGate, IntentNormalizer, SemanticUnderstanding, RequestedShapeValidator
        // and IQuestionShape implementations were deleted — their concerns now live inside
        // SpecExtractor (LLM-driven form-filling + deterministic normalizers).
        services.AddSingleton<Pipeline.Stages.IDecomposer, Pipeline.Stages.HeuristicDecomposer>();
        services.AddSingleton<Pipeline.Stages.IConversationContext, Pipeline.Stages.ConversationContext>();

        // Tool routing (C.1): host bridge over the existing CopilotToolDefinitions table +
        // ToolHandler stage that runs after SemanticSearch / before MetadataHandler. HttpClient
        // factory is required for the dispatch — the host has it registered already; we just
        // ensure it's available for any test/eval host that doesn't auto-add it.
        services.AddHttpClient();
        services.AddScoped<Tools.IToolRegistry, HostBridge.HostToolRegistryBridge>();
        // Scoped because it consumes the Scoped IToolRegistry (which holds a per-request DbContext).
        services.AddScoped<Pipeline.Stages.IToolHandler, Pipeline.Stages.ToolHandler>();

        // New slim orchestrator (Phase 1 refactor): replaces the 30-dep CopilotOrchestrator with
        // a focused conversational/knowledge → decompose → spec-extract → compile → validate →
        // execute → explain flow. The old CopilotOrchestrator file is kept temporarily and
        // will be deleted in Step 1.7 once smoke tests confirm parity.
        services.AddScoped<ISuperAdminCopilot, CopilotOrchestrator>();

        // Retry budget — Scoped so each HTTP request (i.e. each user question) gets a fresh
        // budget. Sub-questions emitted by the Decomposer share the parent's budget, which is
        // intentional: a question that splits into 5 sub-questions still has one cost ceiling.
        services.AddScoped<Pipeline.IRetryBudget, Pipeline.RetryBudget>();

        // Learning RAG store — Scoped so the cache shares scope with the embedder. Reads from
        // CopilotTraceHistories.QuestionEmbeddingJson populated by HostTraceSink on successful runs.
        services.AddScoped<Retrieval.IPastQuestionStore, Retrieval.PastQuestionStore>();

        // Eval — only the rigorous Execution-Accuracy checker remains. The legacy GoldenSetRunner
        // + its logs-only HTTP route + the orphaned QuestionSuiteLoader/EvalModels were retired
        // once the Assessment Lab UI absorbed the EX comparison directly. One UI, one engine.
        services.AddScoped<Eval.IExecutionAccuracyChecker, Eval.ExecutionAccuracyChecker>();

        // P1 #21 — warmup hosted service primes catalog + semantic layer + entity vectors at
        // host startup so the first real chat question doesn't pay full priming latency.
        services.AddHostedService<Pipeline.CopilotWarmupHostedService>();
        // P1 #80 — daily prune of old trace rows (configurable retention).
        services.AddHostedService<Pipeline.TraceRetentionHostedService>();

        // Role-bound LLM factory — per-role model override from SystemSettings; falls back to default.
        services.Configure<AiRoleBindings>(configuration.GetSection(AiRoleBindings.SectionName));
        services.AddSingleton<IRoleBoundLlmClientFactory, RoleBoundLlmClientFactory>();
        services.Configure<ValueIndexOptions>(configuration.GetSection(ValueIndexOptions.SectionName));

        // Paraphrase-robustness eval runner — used by ParaphraseEvalController.
        services.AddScoped<IParaphraseRobustnessRunner, ParaphraseRobustnessRunner>();
        services.AddScoped<IOfflineParaphraseGenerator, OfflineParaphraseGenerator>();

        // LLM-driven structural-cue parser — extracts column requests and grouping hints
        // from any bracket / dash / comma / SQL-style notation in the question text. Consumed
        // by the orchestrator's RunSingle path before SpecExtractor sees the question.
        services.AddSingleton<IDecomposedPromptProvider>(sp => new DecomposedPromptProvider(
            sp.GetRequiredService<ILogger<DecomposedPromptProvider>>()));
        services.AddScoped<IStructuralCueParser, StructuralCueParser>();

        // Fuzzy entity resolution — surfaces in the question (e.g. "Damascus") get resolved
        // to canonical DB values via trigram match against a sample-value index. Hosted service
        // primes the index on startup and refreshes periodically.
        services.AddSingleton<IValueIndex, ValueIndex>();
        services.AddSingleton<IFuzzyEntityResolver, FuzzyEntityResolver>();
        services.AddHostedService<ValueIndexHostedService>();

        return services;
    }
}
