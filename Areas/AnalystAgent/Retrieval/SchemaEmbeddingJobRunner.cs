namespace AnalystAgent.Retrieval;

using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Schema;

public enum SchemaEmbeddingJobStatus { Idle, Running, Completed, Failed }

public sealed class SchemaEmbeddingJobState
{
    public SchemaEmbeddingJobStatus Status { get; init; } = SchemaEmbeddingJobStatus.Idle;
    public int VectorCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Coordinates the user-triggered "Generate Schema Embeddings" job. Generation embeds every visible
/// table with the configured embedding model — slow on a constrained GPU — so it MUST run detached
/// from the HTTP request (a client disconnect would otherwise cancel it mid-loop and persist nothing,
/// exactly the trap the schema-inference job avoids). The UI starts it, then polls <see cref="State"/>.
/// </summary>
public interface ISchemaEmbeddingJobRunner
{
    SchemaEmbeddingJobState State { get; }
    /// <summary>Start a background regenerate (clear the store, then re-embed + persist). Returns
    /// false if a job is already running.</summary>
    bool Start();
}

internal sealed class SchemaEmbeddingJobRunner : ISchemaEmbeddingJobRunner
{
    private readonly ISchemaEmbeddingStore _store;
    private readonly ISchemaSemanticRetriever _retriever;
    private readonly ITextEmbedder _embedder;
    private readonly ILogger<SchemaEmbeddingJobRunner> _logger;
    private readonly object _gate = new();
    private SchemaEmbeddingJobState _state = new();
    private Task? _runningTask;

    public SchemaEmbeddingJobRunner(
        ISchemaEmbeddingStore store,
        ISchemaSemanticRetriever retriever,
        ITextEmbedder embedder,
        ILogger<SchemaEmbeddingJobRunner> logger)
    {
        _store = store;
        _retriever = retriever;
        _embedder = embedder;
        _logger = logger;
    }

    public SchemaEmbeddingJobState State { get { lock (_gate) return _state; } }

    public bool Start()
    {
        lock (_gate)
        {
            if (_state.Status == SchemaEmbeddingJobStatus.Running) return false;
            _state = new SchemaEmbeddingJobState
            {
                Status = SchemaEmbeddingJobStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            };
            // CancellationToken.None — the job must outlive the HTTP request that started it.
            _runningTask = Task.Run(() => RunAsync(), CancellationToken.None);
            return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            _store.Clear();
            var count = await _retriever.RefreshAsync(CancellationToken.None);
            _logger.LogInformation("[SchemaEmbeddingJob] Generated {Count} table vectors (model={Model}).", count, _embedder.ModelName);
            lock (_gate)
            {
                _state = new SchemaEmbeddingJobState
                {
                    Status = SchemaEmbeddingJobStatus.Completed,
                    VectorCount = count,
                    StartedAt = _state.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SchemaEmbeddingJob] Generation failed.");
            lock (_gate)
            {
                _state = new SchemaEmbeddingJobState
                {
                    Status = SchemaEmbeddingJobStatus.Failed,
                    StartedAt = _state.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message,
                };
            }
        }
    }
}
