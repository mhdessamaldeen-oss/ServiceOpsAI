namespace AnalystAgent.Pipeline;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;
using AnalystAgent.Validation;

/// <summary>
/// P1 #21 — Background hosted service that primes the cold-path caches the new copilot uses
/// so the first user request after a host restart doesn't pay the full priming cost. Specifically:
/// <list type="bullet">
///   <item>Catalog snapshot — forces schema introspection (sample-values, FK graph build).</item>
///   <item>Semantic-layer config — forces JSON load + lookup-dictionary build.</item>
///   <item>Department-embedding matcher — forces per-entity vector compute (one embedder call per entity).</item>
/// </list>
/// <para>Runs once 5 seconds after host startup so the rest of the application has time to wire
/// itself up. Failures are logged at Warning and never throw — a missing embedder or DB just means
/// the first real request takes the priming hit, same as before.</para>
/// </summary>
internal sealed class AnalystWarmupHostedService : BackgroundService
{
    private readonly IServiceProvider _root;
    private readonly ILogger<AnalystWarmupHostedService> _logger;

    public AnalystWarmupHostedService(IServiceProvider root, ILogger<AnalystWarmupHostedService> logger)
    {
        _root = root;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Brief delay so the host's request pipeline + EF DB context factory are wired before
            // we hit them. 5s is enough on a normal box; if the host is slow to boot this just
            // delays priming, never blocks startup.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            using var scope = _root.CreateScope();
            var sp = scope.ServiceProvider;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1) Schema catalog — Snapshot is Lazy; touching it forces introspection.
            var catalog = sp.GetRequiredService<IEntityCatalog>();
            _ = catalog.Snapshot;
            _logger.LogInformation("[Warmup] Catalog primed in {Ms}ms.", sw.ElapsedMilliseconds);

            // 1c) Eager-load schema-knowledge (inferred + overrides). Singleton ctor runs
            // TryLoad(); if the inferred file is missing we just log a warning and the
            // copilot still works (just without the rich semantic facts).
            var schemaKnowledge = sp.GetRequiredService<ISchemaKnowledge>();
            _logger.LogInformation(
                "[Warmup] SchemaKnowledge primed at {Ms}ms (available={Available}, tables={Tables}).",
                sw.ElapsedMilliseconds, schemaKnowledge.IsAvailable, schemaKnowledge.TableCount);

            // 1d) Value→table index — probes the entity-subtype columns ONCE so the first question that names
            // an entity-via-its-type ("transformers" → Assets.AssetType) doesn't pay the build. Lazy otherwise.
            try
            {
                if (sp.GetRequiredService<ISchemaLinker>() is SchemaLinker schemaLinker)
                    _logger.LogInformation("[Warmup] Enum-value index primed at {Ms}ms ({N} distinctive values).",
                        sw.ElapsedMilliseconds, schemaLinker.PrimeEnumValueIndex());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup] Enum-value index priming failed (non-fatal).");
            }

            // 2) Semantic layer — Config is Lazy too.
            var semantic = sp.GetRequiredService<ISemanticLayer>();
            _ = semantic.Config;
            _logger.LogInformation("[Warmup] Semantic layer primed at {Ms}ms.", sw.ElapsedMilliseconds);

            // 3) Department-embedding vectors — fires one EmbedAsync per declared entity.
            try
            {
                var matcher = sp.GetRequiredService<IEntityEmbeddingMatcher>();
                if (matcher.IsAvailable)
                    await matcher.FindAsync("warmup", minConfidence: 1.0f, cancellationToken: stoppingToken);
                _logger.LogInformation("[Warmup] Department vectors primed at {Ms}ms.", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup] Department-vector priming failed (non-fatal).");
            }

            // 3b) Schema-table embeddings — prime the LIVE similarity retriever (the one the
            // SchemaLinker uses) so the first real question doesn't pay the priming burst. With
            // persisted vectors this just loads them from disk; on a cold cache it embeds once and
            // saves. A throwaway probe forces GetTableVectorsAsync.
            try
            {
                var retriever = sp.GetService<AnalystAgent.Retrieval.ISchemaSemanticRetriever>();
                if (retriever is not null && retriever.IsAvailable)
                {
                    _ = await retriever.RetrieveAsync("warmup probe", 1, stoppingToken);
                    _logger.LogInformation("[Warmup] Schema-table embeddings primed at {Ms}ms.", sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup] Schema-embedding priming failed (non-fatal).");
            }

            // 3c) Verified-query matcher — pre-embed every entry's canonical question + variants
            // so the first real chat doesn't pay the ~50-90s priming burst for 87+ entries × 3-5
            // paraphrases. Without this, the first user question after a restart waits a full minute
            // before the orchestrator can even start matching. After warmup, every subsequent
            // question gets sub-100ms cosine lookups against the cached vectors.
            try
            {
                var vqMatcher = sp.GetRequiredService<AnalystAgent.Retrieval.IVerifiedQueryMatcher>();
                if (vqMatcher.IsAvailable)
                {
                    // MatchAsync triggers GetVerifiedVectorsAsync internally, which primes the
                    // cache as a side effect. The probe text doesn't matter — we throw away the
                    // result; we just need to force the priming pass.
                    _ = await vqMatcher.MatchAsync("warmup probe", stoppingToken);
                    _logger.LogInformation("[Warmup] Verified-query vectors primed at {Ms}ms.", sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup] Verified-query priming failed (non-fatal).");
            }

            // 4) Package-quality config validation. This checks semantic-layer table/column
            // references, metric/dimension column references, value synonym contexts, and
            // dynamic tool registry readiness against the live database/catalog.
            try
            {
                var validator = sp.GetRequiredService<IAnalystConfigurationValidator>();
                var issues = await validator.ValidateAsync(stoppingToken);
                var errors = issues.Count(i => i.Severity == AnalystConfigurationIssueSeverity.Error);
                var warnings = issues.Count(i => i.Severity == AnalystConfigurationIssueSeverity.Warning);
                var info = issues.Count(i => i.Severity == AnalystConfigurationIssueSeverity.Info);
                if (issues.Count == 0)
                {
                    _logger.LogInformation("[Warmup/ConfigValidation] No schema/tool configuration issues found.");
                }
                else
                {
                    _logger.LogInformation(
                        "[Warmup/ConfigValidation] Completed with {Errors} error(s), {Warnings} warning(s), {Info} info item(s).",
                        errors, warnings, info);
                    foreach (var issue in issues.Where(i => i.Severity != AnalystConfigurationIssueSeverity.Info).Take(25))
                    {
                        var level = issue.Severity == AnalystConfigurationIssueSeverity.Error ? LogLevel.Warning : LogLevel.Information;
                        _logger.Log(level, "[Warmup/ConfigValidation] {Severity} {Code}: {Message}",
                            issue.Severity, issue.Code, issue.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[Warmup/ConfigValidation] check failed (non-fatal).");
            }

            // 4b) Schema-drift lint — verify every configured table/column reference (schema-inferred +
            // overrides + verified-queries) still exists in the live DB. This is the ONLY invocation of
            // the linter; without it the safety net never runs. A SchemaDriftException (thrown only when
            // the operator set FailFastOnSchemaDrift) is intentionally NOT caught here so it propagates
            // and stops the host; ordinary lint failures are non-fatal.
            try
            {
                var linter = sp.GetService<ISchemaDriftLinter>();
                linter?.Lint();
                _logger.LogInformation("[Warmup] Schema-drift lint completed at {Ms}ms.", sw.ElapsedMilliseconds);
            }
            catch (SchemaDriftException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup/SchemaDrift] lint failed (non-fatal).");
            }

            sw.Stop();
            _logger.LogInformation("[Warmup] Complete in {Ms}ms.", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { /* host shutting down — fine */ }
        catch (SchemaDriftException) { throw; }   // operator opted into fail-fast — stop the host
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Warmup] Failed (non-fatal).");
        }
    }
}
