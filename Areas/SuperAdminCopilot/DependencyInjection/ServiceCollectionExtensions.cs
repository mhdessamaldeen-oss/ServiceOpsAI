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
        // Role-keyword embedding boosts — Configuration/embedding-keywords.json. Lookup/bridge/
        // person/fact phrases that SchemaSemanticRetriever appends to a table's embedding text.
        // Externalized so operators can tune (or translate) without recompiling.
        services.AddOptions<EmbeddingKeywordsOptions>()
            .Bind(copilotConfig.GetSection(EmbeddingKeywordsOptions.SectionName));
        // Declarative linguistic cues — Configuration/linguistic-cues.json. Loaded once at
        // startup, compiled to regex objects, exposed via ILinguisticCuesProvider. Replaces the
        // hardcoded regex sets previously embedded across SpecRepair phases. To add a new
        // language or synonym, edit the JSON; no recompilation required. Schema is documented
        // in Configuration/LinguisticCues.cs.
        services.AddSingleton<ILinguisticCuesProvider, LinguisticCuesProvider>();

        // SQL-dialect selection is CONFIG-DRIVEN via CopilotOptions.Database (default SqlServer →
        // MssqlDialect, the current production target — unchanged). Database=Postgres binds the
        // unit-tested PostgresDialect so the compiler emits PostgreSQL. The engine-selection keystone:
        // no recompile to switch dialect. (End-to-end against a live non-SqlServer engine ALSO needs
        // the Stage-2 plumbing: an IDbConnection connection factory + per-dialect introspector + validator.)
        services.AddSingleton<Compilation.Dialects.ISqlDialect>(sp =>
            Compilation.Dialects.SqlDialectFactory.Create(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.CopilotOptions>>().Value.Database));
        // Temporal-token grammar collaborator (2026-06-01 de-couple). Both the compiler's
        // WHERE-builder and QualifyColumnsInExpression delegate here so the grammar lives in
        // one place — and externalisation to JSON can happen without touching the compiler.
        services.AddSingleton<Compilation.ITemporalTokenizer, Compilation.TemporalTokenizer>();
        // Semantic expansion collaborator — turns metric:<name> / dimension:<name> spec tokens
        // into concrete SQL fragments via ISemanticLayer. Lives in its own collaborator so the
        // compiler reads as "expand semantic refs → enrich → emit SQL" instead of weaving the
        // translation through filter / group-by code.
        services.AddSingleton<Compilation.ISemanticExpander, Compilation.SemanticExpander>();
        // Filter-value rewriting collaborator — owns synonym + lookup-name + column-ref drop
        // logic so the WHERE-builder stays focused on operator emission. Keeps future filter
        // features (currency-symbol stripping, regex normalisation, etc.) out of WHERE.
        services.AddSingleton<Compilation.IFilterValueRewriter, Compilation.FilterValueRewriter>();
        // D.3 — externalized text catalog. The per-deployment override file
        // Configuration/copilot-text.json is registered as a hot-reloading AddJsonFile source in
        // Program.cs (SuperAdminCopilot:Text section); consumers inject
        // IOptionsMonitor<CopilotTextCatalog> and read .CurrentValue on each access, so edits to that
        // file (refusal text, planner prompts, worked examples) land without a restart or recompile.
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
        // Slice 1 — column-level semantic retriever. Embeds every (table, column) once
        // (cached by schema hash). Sub-50ms cosine sweep per question; primes lazily on first
        // call. Injects top-K relevant columns into the planner prompt so the LLM gets
        // semantic-anchored column hints instead of guessing from column names alone.
        services.AddSingleton<Retrieval.IColumnSemanticRetriever, Retrieval.ColumnSemanticRetriever>();
        // Slice 2 (2026-05-30 close-out) — entity-level semantic retriever. Embeds the DOMAIN
        // descriptions + synonyms from semantic-layer.json (not raw table shape). Surfaces
        // domain-vocab → DB-entity mappings (Arabic "تذاكر" → Tickets, "complaints" → Tickets)
        // that the table-shape retriever misses. Wired into SpecExtractor alongside the schema
        // and column retrievers — three complementary signals, no overlap.
        services.AddSingleton<Retrieval.IEntitySemanticRetriever, Retrieval.EntitySemanticRetriever>();
        // New core LLM step: question + retrieved tables → QuerySpec JSON (form-filling).
        // Replaces the heavy LlmPlanner prompt with a tight, locally-runnable one.
        // Scoped because it consumes IPastQuestionStore (Scoped, DbContext-backed) for the
        // persistent-learning few-shot retrieval. Per-request allocation cost is microseconds.
        services.AddScoped<Pipeline.Stages.ISpecExtractor, Pipeline.Stages.SpecExtractor>();
        // ── SpecRepair: consolidated LLM-output mutation pipeline ──────────────────────
        // SpecRepair — 12 typed rules behind a single coordinator. Replaces the prior 50-phase
        // pipeline. See docs/architecture/v3/ for the design.
        services.AddSingleton<Application.Repair.ILinguisticRegistry,
                              Infrastructure.Linguistic.LinguisticRegistry>();
        services.AddSingleton<Application.Repair.Schema.ISchemaView,
                              Infrastructure.Schema.SchemaView>();
        services.AddSingleton<Application.Repair.Semantic.ISemanticView,
                              Infrastructure.Schema.SemanticView>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingRootRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DanglingColumnReferenceRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.WrongAggregationShapeRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingTemporalScopeRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingLookupFilterRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingTextSearchRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingAntiJoinRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.UnsolicitedFilterRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.AmbiguousLimitRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.OverJoinRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.InvalidSelectGroupByRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DisplayColumnsMissingRule>();
        // 2026-05-30 data-driven addition. The deep-dive analyzer flagged 93/294 baseline
        // failures (31.6%) where the LLM emitted `SELECT FK_id, SUM(...) GROUP BY FK_id`
        // instead of `SELECT target.Label, SUM(...) GROUP BY target.Label`. This rule rewrites
        // that pattern. DisplayColumnsMissingRule (above) handles the LIST shape; this one
        // handles the AGGREGATE-WITH-GROUPBY shape.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.ProjectLabelForFkGroupByRule>();
        // 2026-05-30 — Iteration 2 fix for the self-join hierarchical pattern. When the LLM
        // projects ParentRegion.NameEn or similar without a matching join, this rule drops the
        // unresolved entry from SELECT so the SQL executes (partial answer) instead of throwing
        // "multi-part identifier could not be bound". Three cases in baseline 190 hit this.
        // 2026-05-30 Phase 2.1 — InferSelfJoinFromUnresolvedAliasRule runs BEFORE the drop rule
        // so a self-join intent (ParentRegion.NameEn → JOIN Regions AS ParentRegion) becomes a
        // real join instead of getting dropped. If this rule doesn't fire, the drop rule still
        // cleans up genuinely unresolved references.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.InferSelfJoinFromUnresolvedAliasRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DropUnresolvedSelectColumnsRule>();
        // 2026-05-30 evening — DropUnsafeComputedExpressionsRule. When the LLM emits a raw
        // SELECT/FROM inside Computed.Expression (SUB-shape failure pattern from trace ID 3),
        // the access-policy gate would otherwise refuse-hard and crash the whole pipeline.
        // This rule drops the offending Computed + scrubs SELECT/GROUP BY/HAVING references to
        // the dropped alias, so the validator passes and the user gets a partial answer.
        // Single-source-of-truth: shares the unsafe-keyword check with QuerySpecAccessPolicyValidator.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DropUnsafeComputedExpressionsRule>();
        // 2026-05-30 evening — SwapPureCountForListWhenQuestionIsListShapeRule. Pre-empts the
        // SqlIntentGuard's CheckCountShapeOnNonCountQuestion refusal for the "complaints in
        // Damascus this month" pattern: LLM emits pure-COUNT spec for a list question, gate
        // would refuse-hard after retries fail. Mirrors the gate's exact predicate (same source
        // of truth via ctx.Linguistics.LooksLikeAggregateQuery), drops the COUNT, projects the
        // root's display columns + FK labels. User gets the list they asked for.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.SwapPureCountForListWhenQuestionIsListShapeRule>();
        // 2026-05-30 evening — DropDistinctWhenAggregatedRule. Pre-empts SqlIntentGuard
        // CheckDistinctWithAggregations refusal: when LLM sets BOTH distinct=true and emits
        // aggregations, clear distinct (groupBy already produces one row per group). Trivial
        // single-flag flip — aggregations win.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DropDistinctWhenAggregatedRule>();
        // 2026-06-01 — WarnUnknownFilterOpsRule. Rewrites typo'd FilterSpec.Op values
        // (e.g. "equ" instead of "eq") to "eq" so the filter isn't silently dropped by the
        // WHERE-builder. Surfaces the typo in the rule_fired log line so operators can spot
        // which ops the LLM hallucinates most often.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.WarnUnknownFilterOpsRule>();
        // 2026-05-30 Iter 4 — AVG/SUM on nvarchar columns throws "Operand data type nvarchar is
        // invalid for avg operator". Rule rewrites those to COUNT(*) using the new
        // ISchemaView.IsNumericColumn type-aware surface (column metadata source).
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.NumericAggregationOnNonNumericRule>();
        // Phase B.5 — 10 rules covering the 21 v2 phases that had no replacement in the
        // original 12-rule mapping (everyday phrasings: synonyms, NK tokens, lifecycle verbs,
        // negation, ranges, top-N order, FK eq → join, time-series buckets, cross-entity counts,
        // concept patterns, dedupe). DerivedMetricRule is wired but no-ops until the
        // semantic-layer.json derivedMetrics block is added in a follow-up.
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.ValueSynonymRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.NaturalKeyTokenRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.LifecycleVerbDateRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.NegationFilterRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.NumericRangeRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.MissingOrderByForLimitRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.FkNameToJoinRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.TimeSeriesBucketingRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.OuterEntityCountRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.DerivedMetricRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.ConceptPatternRule>();
        services.AddSingleton<Application.Repair.IRepairRule, Application.Repair.Rules.FilterDedupeRule>();
        services.AddSingleton<Application.Repair.RepairBus>();

        // Stage-1 grounding (DIN-SQL / CHESS / ValueNet style). Pre-resolves schema-linked
        // values, temporal slots, natural keys, intent shape, and lifecycle verb date-role
        // BEFORE the LLM call. Consumed by SpecExtractor.
        services.AddSingleton<Grounding.IValueLinker, Grounding.ValueLinker>();
        services.AddSingleton<Grounding.IQuestionGrounder, Grounding.QuestionGrounder>();

        services.AddSingleton<Eval.ExpectationVerifier.IExpectationVerifier, Eval.ExpectationVerifier.ExpectationVerifier>();

        services.AddSingleton<Pipeline.SpecRepair.ISpecRepair, Pipeline.SpecRepair.SpecRepair>();
        // Escape valve — when form-filling QuerySpec can't express the shape (window funcs,
        // recursive CTEs, complex analytics), ask the LLM to write raw T-SQL. Output still
        // passes the SqlAstValidator + ReadOnlyExecutor read-only guard. Last-resort fallback.
        // Advanced-shape knowledge for the escape valve (gold-SQL examples + keyword grammar),
        // externalized to Configuration/shape-examples.json + advanced-shape-keywords.json so a new
        // DB/schema is targeted by editing config, not C#. Absent files → byte-identical in-code fallback.
        services.AddSingleton<IAdvancedShapeCatalog, AdvancedShapeCatalog>();
        services.AddSingleton<Pipeline.Stages.ILlmDirectSqlEmitter, Pipeline.Stages.LlmDirectSqlEmitter>();
        // 2026-06-01 — REMOVED the phrase-matching IQuestionShapeClassifier. Its brittle
        // English keyword routing was replaced by the coverage-gap → escape-valve retry in
        // SingleQuestionExecutor (CopilotOptions.EnableCoverageEscapeRetry). Detection over
        // prediction; multilingual; no hardcoded vocab.
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

        // 2026-06-01 — Phase 4 refactor: ResponsePersister owns trace-row stamping; extracted
        // from the orchestrator so it can be shared by both the orchestrator's fast-path exits
        // and the single-question executor without duplication.
        services.AddScoped<Pipeline.IResponsePersister, Pipeline.ResponsePersister>();
        // 2026-06-01 — SingleQuestionExecutor: owns the entity-resolution → spec-extraction →
        // compile/validate/execute loop → explain → coverage-check path. The orchestrator
        // is now a thin dispatcher (~160 LOC) that routes preflight/fast-paths/decomposer and
        // delegates single questions here.
        services.AddScoped<Pipeline.ISingleQuestionExecutor, Pipeline.SingleQuestionExecutor>();
        // Orchestrator: thin dispatcher — preflight, fast-paths, decomposer, then delegates
        // to ISingleQuestionExecutor or RunDecomposedAsync.
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
