namespace SuperAdminCopilot.Pipeline;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using SuperAdminCopilot.Validation;

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
internal sealed class CopilotWarmupHostedService : BackgroundService
{
    private readonly IServiceProvider _root;
    private readonly ILogger<CopilotWarmupHostedService> _logger;

    public CopilotWarmupHostedService(IServiceProvider root, ILogger<CopilotWarmupHostedService> logger)
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

            // 3b) Schema-table summary embeddings — fires one EmbedAsync per allowed table so
            // the first real question doesn't pay the full retrieval-time priming burst
            // (50-500 sequential embedder calls). Bounded parallelism inside PrimeAsync.
            try
            {
                var retriever = sp.GetService<SuperAdminCopilot.Abstractions.IRetriever>()
                    as SuperAdminCopilot.Retrieval.VectorRetriever;
                if (retriever is not null)
                {
                    await retriever.PrimeAsync(maxParallelism: 4, cancellationToken: stoppingToken);
                    _logger.LogInformation("[Warmup] Table-summary embeddings primed at {Ms}ms.", sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Warmup] Table-embedding priming failed (non-fatal).");
            }

            // 3c) Verified-query matcher — pre-embed every entry's canonical question + variants
            // so the first real chat doesn't pay the ~50-90s priming burst for 87+ entries × 3-5
            // paraphrases. Without this, the first user question after a restart waits a full minute
            // before the orchestrator can even start matching. After warmup, every subsequent
            // question gets sub-100ms cosine lookups against the cached vectors.
            try
            {
                var vqMatcher = sp.GetRequiredService<SuperAdminCopilot.Retrieval.IVerifiedQueryMatcher>();
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
                var validator = sp.GetRequiredService<ICopilotConfigurationValidator>();
                var issues = await validator.ValidateAsync(stoppingToken);
                var errors = issues.Count(i => i.Severity == CopilotConfigurationIssueSeverity.Error);
                var warnings = issues.Count(i => i.Severity == CopilotConfigurationIssueSeverity.Warning);
                var info = issues.Count(i => i.Severity == CopilotConfigurationIssueSeverity.Info);
                if (issues.Count == 0)
                {
                    _logger.LogInformation("[Warmup/ConfigValidation] No schema/tool configuration issues found.");
                }
                else
                {
                    _logger.LogInformation(
                        "[Warmup/ConfigValidation] Completed with {Errors} error(s), {Warnings} warning(s), {Info} info item(s).",
                        errors, warnings, info);
                    foreach (var issue in issues.Where(i => i.Severity != CopilotConfigurationIssueSeverity.Info).Take(25))
                    {
                        var level = issue.Severity == CopilotConfigurationIssueSeverity.Error ? LogLevel.Warning : LogLevel.Information;
                        _logger.Log(level, "[Warmup/ConfigValidation] {Severity} {Code}: {Message}",
                            issue.Severity, issue.Code, issue.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[Warmup/ConfigValidation] check failed (non-fatal).");
            }

            sw.Stop();
            _logger.LogInformation("[Warmup] Complete in {Ms}ms.", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { /* host shutting down — fine */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Warmup] Failed (non-fatal).");
        }
    }
}
