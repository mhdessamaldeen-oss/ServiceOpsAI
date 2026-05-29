namespace SuperAdminCopilot.HostBridge;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Copilot.Trace;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Drains <see cref="ITraceWriteQueue"/> on a background thread, persisting each request via
/// <see cref="CopilotTraceHistoryStore"/> in its own DI scope. Decoupled from the pipeline
/// response path — the user's question returns as soon as the orchestrator produces SQL/answer;
/// the trace + embedding write happens after.
///
/// <para>Failure policy: the store already retries once for transient errors and increments
/// the 9001 EventId on persistent failure. The hosted service catches anything past that and
/// keeps draining — one bad row must not stall the queue.</para>
/// </summary>
internal sealed class TraceWriteHostedService : BackgroundService
{
    private readonly ITraceWriteQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TraceWriteHostedService> _logger;

    public TraceWriteHostedService(
        ITraceWriteQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<TraceWriteHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TraceWriteHostedService] Started — draining trace queue.");
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await PersistAsync(request, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Last-ditch swallow — the store already logs at error level for save failures.
                    _logger.LogError(ex, "[TraceWriteHostedService] Unhandled error draining one trace; continuing.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        _logger.LogInformation("[TraceWriteHostedService] Drain loop exited.");
    }

    private async Task PersistAsync(TraceWriteRequest req, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<CopilotTraceHistoryStore>();

        var traceId = await store.SaveAsync(
            req.Response,
            req.ElapsedMs,
            sessionId: req.SessionId,
            caseCode: req.CaseCode,
            pipelineTraceId: req.PipelineTraceId,
            generatedScript: req.GeneratedScript,
            errorMessage: req.ErrorMessage,
            sourceSuite: req.SourceSuite,
            expectedScript: req.ExpectedScript,
            llmCallCount: req.LlmCallCount,
            totalPromptTokens: req.TotalPromptTokens,
            totalCompletionTokens: req.TotalCompletionTokens,
            estimatedCostUsd: req.EstimatedCostUsd,
            stepModelsJson: req.StepModelsJson,
            cancellationToken: cancellationToken);

        // Embed-and-persist follow-up. The original code did Task.Run; here we just chain
        // the embedding work in the same worker. Failure is logged inside.
        if (req.ShouldEmbedQuestion && traceId is int id && !string.IsNullOrEmpty(req.QuestionForEmbedding))
        {
            try
            {
                var embedder = scope.ServiceProvider.GetRequiredService<ITextEmbedder>();
                var vec = await embedder.EmbedAsync(req.QuestionForEmbedding!, cancellationToken);
                if (vec.Length > 0)
                {
                    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                    await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);
                    var row = await ctx.Set<CopilotTraceHistory>().FindAsync(new object[] { id }, cancellationToken);
                    if (row is not null)
                    {
                        row.QuestionEmbeddingJson = System.Text.Json.JsonSerializer.Serialize(vec);
                        row.EmbeddingModelName = embedder.ModelName ?? "";
                        await ctx.SaveChangesAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TraceWriteHostedService] Embedding persistence failed for trace {TraceId}.", id);
            }
        }
    }
}
