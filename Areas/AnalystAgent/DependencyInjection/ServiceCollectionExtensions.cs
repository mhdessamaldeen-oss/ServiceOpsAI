namespace AnalystAgent.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceOpsAI.Services.AI.Providers.Roles;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Eval;
using AnalystAgent.Eval.Paraphrase;
using AnalystAgent.Execution;
using AnalystAgent.Explanation;
using AnalystAgent.HostBridge;
using AnalystAgent.Pipeline;
using AnalystAgent.Pipeline.EntityResolution;
using AnalystAgent.Pipeline.Stages;
using AnalystAgent.Retrieval;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;
using AnalystAgent.Validation;

/// <summary>
/// Single DI entry point for the in-host AnalystAgent. The host's Program.cs calls
/// <c>builder.Services.AddAnalystAgent(builder.Configuration)</c> exactly once.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalystAgent(this IServiceCollection services, IConfiguration configuration)
    {
        // P1 #59 — fail fast at startup when a new pipeline stage is missing its graph mapping.
        // Catches the dev mistake of adding a Step* constant without wiring it into
        // StageNames.StageToGraphNode + _InvestigationPipelineGraph.cshtml. Cheap reflection
        // scan that runs once per process boot.
        Pipeline.StageNames.AssertGraphCoverage();

        // Single-source settings — copilot-options.json under Areas/AnalystAgent/Configuration/.
        // Replaces the previous appsettings.json "AnalystAgent" section binding.
        // The file is the ONLY place runtime knobs live for this module.
        var copilotConfig = AnalystOptionsLoader.BuildConfiguration();
        services.AddOptions<AnalystOptions>()
            .Bind(copilotConfig.GetSection(AnalystOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Profile overlay (Phase 4) — applies the named preset from copilot-options.json's
        // Profiles{} block on top of the bound values. Runs BEFORE the SystemSettings overlay
        // so admin-edited operator settings always win over the engineering profile baseline.
        services.PostConfigure<AnalystOptions>(opts => AnalystOptionsLoader.ApplyProfile(copilotConfig, opts));
        services.AddSingleton<IPostConfigureOptions<AnalystOptions>, HostAnalystOptionsConfigurator>();
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

        // SQL-dialect selection is CONFIG-DRIVEN via AnalystOptions.Database (default SqlServer →
        // MssqlDialect, the current production target — unchanged). Database=Postgres binds the
        // unit-tested PostgresDialect so the compiler emits PostgreSQL. The engine-selection keystone:
        // no recompile to switch dialect. (End-to-end against a live non-SqlServer engine ALSO needs
        // the Stage-2 plumbing: an IDbConnection connection factory + per-dialect introspector + validator.)
        services.AddSingleton<Sql.Dialects.ISqlDialect>(sp =>
            Sql.Dialects.SqlDialectFactory.Create(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.AnalystOptions>>().Value.Database));
        // [DELETED] The QuerySpec→SQL compiler and its collaborators (temporal tokenizer, semantic
        // expander, filter-value rewriter, sensitive-column policy, type coercer, filter-clause
        // emitter, expression qualifier, alias policy, label resolver). The lean path lets the model
        // emit SQL directly — no IR, no hand-built engine. ISqlDialect (registered above) stays:
        // LlmDirectSqlEmitter uses it to normalise raw model SQL.
        // D.3 — externalized text catalog. The per-deployment override file
        // Configuration/copilot-text.json is registered as a hot-reloading AddJsonFile source in
        // Program.cs (AnalystAgent:Text section); consumers inject
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
        services.AddScoped<IAnalystAgentChatBridge, AnalystAgentChatBridge>();
        // Live progress broadcaster — SignalR-backed in the production host so the chat
        // UI's Claude-style timeline updates in real time as each stage runs. Replace with
        // NullPipelineStepProgressSink.Instance in eval/test composition roots.
        services.AddSingleton<IPipelineStepProgressSink, SignalRPipelineStepProgressSink>();

        // Schema (Phase 1).
        services.AddSingleton<IDbConnectionFactory, SqlServerConnectionFactory>();
        services.AddSingleton<ISchemaIntrospector, SchemaIntrospector>();
        services.AddSingleton<IEntityCatalog, EntityCatalog>();
        services.AddSingleton<IForeignKeyGraph>(sp => sp.GetRequiredService<IEntityCatalog>().Graph);
        services.AddSingleton<IAnalystSchemaAccessPolicy, AnalystSchemaAccessPolicy>();
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
        // Persisted schema-embedding store — table/column/entity vectors saved to disk (keyed by model +
        // schema hash) so a restart loads them instead of re-embedding, and bge-m3 stays out of VRAM.
        services.AddSingleton<Retrieval.ISchemaEmbeddingStore, Retrieval.SchemaEmbeddingStore>();
        services.AddSingleton<Retrieval.ISchemaSemanticRetriever, Retrieval.SchemaSemanticRetriever>();
        // Background runner for the "Generate Schema Embeddings" admin action — detached from the
        // HTTP request so a client disconnect can't cancel a slow re-embed mid-loop.
        services.AddSingleton<Retrieval.ISchemaEmbeddingJobRunner, Retrieval.SchemaEmbeddingJobRunner>();
        // THE schema linker — combines name/synonym + trigram + embedding similarity → matched-set FK
        // closure into one tight slice. Replaces the keyword/camelCase/retrieval-fallback patchwork.
        services.AddSingleton<Schema.ISchemaLinker, Schema.SchemaLinker>();
        // Linguistic registry — declarative cues (granularity, lifecycle verbs, plural rules)
        // surfaced to the grounder. Backed by linguistic-cues.json.
        services.AddSingleton<Grounding.ILinguisticRegistry,
                              Linguistic.LinguisticRegistry>();
        // 2026-05-30 data-driven addition. The deep-dive analyzer flagged 93/294 baseline
        // failures (31.6%) where the LLM emitted `SELECT FK_id, SUM(...) GROUP BY FK_id`
        // instead of `SELECT target.Label, SUM(...) GROUP BY target.Label`. This rule rewrites
        // that pattern. DisplayColumnsMissingRule (above) handles the LIST shape; this one
        // handles the AGGREGATE-WITH-GROUPBY shape.
        // 2026-05-30 — Iteration 2 fix for the self-join hierarchical pattern. When the LLM
        // projects ParentRegion.NameEn or similar without a matching join, this rule drops the
        // unresolved entry from SELECT so the SQL executes (partial answer) instead of throwing
        // "multi-part identifier could not be bound". Three cases in baseline 190 hit this.
        // 2026-05-30 Phase 2.1 — InferSelfJoinFromUnresolvedAliasRule runs BEFORE the drop rule
        // so a self-join intent (ParentRegion.NameEn → JOIN Regions AS ParentRegion) becomes a
        // real join instead of getting dropped. If this rule doesn't fire, the drop rule still
        // cleans up genuinely unresolved references.
        // 2026-05-30 evening — DropUnsafeComputedExpressionsRule. When the LLM emits a raw
        // SELECT/FROM inside Computed.Expression (SUB-shape failure pattern from trace ID 3),
        // the access-policy gate would otherwise refuse-hard and crash the whole pipeline.
        // This rule drops the offending Computed + scrubs SELECT/GROUP BY/HAVING references to
        // the dropped alias, so the validator passes and the user gets a partial answer.
        // Single-source-of-truth: shares the unsafe-keyword check with QuerySpecAccessPolicyValidator.
        // 2026-05-30 evening — SwapPureCountForListWhenQuestionIsListShapeRule. Pre-empts the
        // SqlIntentGuard's CheckCountShapeOnNonCountQuestion refusal for the "complaints in
        // Damascus this month" pattern: LLM emits pure-COUNT spec for a list question, gate
        // would refuse-hard after retries fail. Mirrors the gate's exact predicate (same source
        // of truth via ctx.Linguistics.LooksLikeAggregateQuery), drops the COUNT, projects the
        // root's display columns + FK labels. User gets the list they asked for.
        // 2026-05-30 evening — DropDistinctWhenAggregatedRule. Pre-empts SqlIntentGuard
        // CheckDistinctWithAggregations refusal: when LLM sets BOTH distinct=true and emits
        // aggregations, clear distinct (groupBy already produces one row per group). Trivial
        // single-flag flip — aggregations win.
        // 2026-06-01 — WarnUnknownFilterOpsRule. Rewrites typo'd FilterSpec.Op values
        // (e.g. "equ" instead of "eq") to "eq" so the filter isn't silently dropped by the
        // WHERE-builder. Surfaces the typo in the rule_fired log line so operators can spot
        // which ops the LLM hallucinates most often.
        // 2026-05-30 Iter 4 — AVG/SUM on nvarchar columns throws "Operand data type nvarchar is
        // invalid for avg operator". Rule rewrites those to COUNT(*) using the new
        // ISchemaView.IsNumericColumn type-aware surface (column metadata source).
        // Phase B.5 — 10 rules covering the 21 v2 phases that had no replacement in the
        // original 12-rule mapping (everyday phrasings: synonyms, NK tokens, lifecycle verbs,
        // negation, ranges, top-N order, FK eq → join, time-series buckets, cross-entity counts,
        // concept patterns, dedupe). DerivedMetricRule is wired but no-ops until the
        // semantic-layer.json derivedMetrics block is added in a follow-up.

        // Stage-1 grounding (DIN-SQL / CHESS / ValueNet style). Pre-resolves schema-linked
        // values, temporal slots, natural keys, intent shape, and lifecycle verb date-role
        // BEFORE the LLM call. Consumed by SpecExtractor.
        services.AddSingleton<Grounding.IValueLinker, Grounding.ValueLinker>();
        services.AddSingleton<Grounding.IQuestionGrounder, Grounding.QuestionGrounder>();

        services.AddSingleton<Eval.ExpectationVerifier.IExpectationVerifier, Eval.ExpectationVerifier.ExpectationVerifier>();

        // The analyst SQL generator. Writes T-SQL directly from the grounded, query-scoped schema
        // (no hardcoded worked-examples — they misled the model on Arabic/hard questions). Output
        // passes the SqlAstValidator + ReadOnlyExecutor read-only guard.
        services.AddSingleton<Pipeline.Stages.ILlmDirectSqlEmitter, Pipeline.Stages.LlmDirectSqlEmitter>();
        // 2026-06-01 — REMOVED the phrase-matching IQuestionShapeClassifier. Its brittle
        // English keyword routing was replaced by the coverage-gap → escape-valve retry in
        // SingleQuestionExecutor (AnalystOptions.EnableCoverageEscapeRetry). Detection over
        // prediction; multilingual; no hardcoded vocab.
        // Phase 7a — Prompt Shape Classifier. Deterministic 8-shape classifier (COUNT, TOPN,
        // AGGREGATE, TIMESERIES, COMPARE, LOOKUP, JOIN, FILTER) that drives example-bank
        // selection in the prompt assembler. Patterns live in Configuration/Prompts/shape-classifier.json.
        services.AddSingleton<Pipeline.Prompts.IPromptShapeClassifier, Pipeline.Prompts.DeterministicPromptShapeClassifier>();
        // LLM-fallback decomposer for compound questions the regex HeuristicDecomposer misses.
        // The new orchestrator tries regex first (fast); falls back to this when regex returns null.
        services.AddSingleton<Pipeline.Stages.ILlmDecomposer, Pipeline.Stages.LlmDecomposer>();
        // Chart-type suggester — heuristic, no LLM. Picks "kpi" / "bar" / "line" / "pie" / "table"
        // based on result-set shape. Host UI may use it to render a default view.
        // Deterministic spec enrichment — fills the LLM's gaps with schema-aware rules
        // (smart projection, FK label resolution, group-by-in-select). The "do hard-code
        // what's universal, use AI for intent" architectural fix.
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
        // against the live DB. Invoked at startup by AnalystWarmupHostedService; throws a
        // SchemaDriftException (stops the host) only when AnalystOptions.FailFastOnSchemaDrift is set.
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

        // Semantic layer — domain vocabulary + entity identity over the inferred schema.
        services.AddSingleton<ISemanticLayer, SemanticLayer>();

        // Department embedding matcher (D.2) — cosine-rank fallback when the JSON synonym dictionary
        // misses a word the user typed. Async API, consumed at warmup priming + entity resolution.
        services.AddSingleton<IEntityEmbeddingMatcher, EntityEmbeddingMatcher>();

        // Pipeline.
        services.AddSingleton<IValidator, SqlAstValidator>();
        services.AddScoped<IAnalystConfigurationValidator, AnalystConfigurationValidator>();
        // SQL Intent Guard — post-compile intent-vs-SQL check. Rejects shapes the syntactic
        // validator can't catch: COUNT for a list question, AVG missing on average questions,
        // GROUP BY root.Id (degenerate template), exact-date question missing the literal date.

        // Executor pipeline: Pii → CostGate → Cache → ReadOnly. Each wrapper short-circuits when
        // its AnalystOptions switch is off, so the configured chain is always all four layers
        // and overhead is paid only when explicitly enabled. PiiRedactor sits OUTERMOST so it
        // sees the final result row set regardless of cache hits / cost-gate refusals.
        services.AddSingleton<ReadOnlyExecutor>();
        services.AddSingleton<CachingExecutor>();
        services.AddSingleton<CostGateExecutor>();
        services.AddSingleton<IExecutor>(sp => new PiiRedactingExecutor(
            sp.GetRequiredService<CostGateExecutor>(),
            sp.GetRequiredService<ISemanticLayer>(),
            sp.GetRequiredService<IAnalystSchemaAccessPolicy>(),
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
        // every floor lives in AnalystOptions and every decision derives from config (the
        // schema-inferred map, the verified-query catalog, the tool registry).
        services.AddSingleton<Pipeline.Stages.IScopeConfidenceGate, Pipeline.Stages.ScopeConfidenceGate>();

        // Coverage Check — post-Explainer LLM verification that the reply fully addresses
        // the question. Scoped because it consumes IRetryBudget (per-request).

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
        // Minimal direct-analyst front-path (flag-gated via AnalystOptions.EnableDirectSqlPath).
        // Scoped — it consumes Scoped collaborators (IExplainer, IResponsePersister).
        services.AddScoped<Pipeline.IDirectAnalystPath, Pipeline.DirectAnalystPath>();
        services.AddScoped<Pipeline.ISingleQuestionExecutor, Pipeline.SingleQuestionExecutor>();
        // Orchestrator: thin dispatcher — preflight, fast-paths, decomposer, then delegates
        // to ISingleQuestionExecutor or RunDecomposedAsync.
        services.AddScoped<IAnalystAgent, AnalystOrchestrator>();

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
        services.AddHostedService<Pipeline.AnalystWarmupHostedService>();
        // P1 #80 — daily prune of old trace rows (configurable retention).
        services.AddHostedService<Pipeline.TraceRetentionHostedService>();

        // Role-bound LLM factory — per-role model override from SystemSettings; falls back to default.
        services.Configure<AiRoleBindings>(configuration.GetSection(AiRoleBindings.SectionName));
        services.AddSingleton<IRoleBoundLlmClientFactory, RoleBoundLlmClientFactory>();
        services.Configure<ValueIndexOptions>(configuration.GetSection(ValueIndexOptions.SectionName));

        // Paraphrase-robustness eval runner — used by ParaphraseEvalController.
        services.AddScoped<IParaphraseRobustnessRunner, ParaphraseRobustnessRunner>();
        services.AddScoped<IOfflineParaphraseGenerator, OfflineParaphraseGenerator>();

        // Fuzzy entity resolution — surfaces in the question (e.g. "Damascus") get resolved
        // to canonical DB values via trigram match against a sample-value index. Hosted service
        // primes the index on startup and refreshes periodically.
        services.AddSingleton<IValueIndex, ValueIndex>();
        services.AddSingleton<IFuzzyEntityResolver, FuzzyEntityResolver>();
        services.AddHostedService<ValueIndexHostedService>();

        return services;
    }
}
