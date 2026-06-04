namespace AnalystAgent.HostBridge;

using System.Threading.Channels;
using ServiceOpsAI.Models.AI;

/// <summary>
/// Background queue for CopilotTraceHistory writes. The pipeline enqueues; a hosted
/// service drains and persists. The pipeline never awaits DB I/O — the user's response
/// returns as soon as the orchestrator produces it, and the trace lands asynchronously
/// in its own DbContext (no transaction overlap with the user's response path).
///
/// <para>Channel is unbounded but trace writes are tiny rows; under sustained pressure
/// a bound could be added with policy DropOldest. Default policy: never drop, retry
/// in worker, log persistent failures via existing 9001 EventId.</para>
/// </summary>
public interface ITraceWriteQueue
{
    /// <summary>Try to enqueue a write request. Returns true on success.
    /// Synchronous and lock-free — designed for the hot response path.</summary>
    bool TryEnqueue(TraceWriteRequest request);

    /// <summary>The reader the background worker drains.</summary>
    ChannelReader<TraceWriteRequest> Reader { get; }
}

/// <summary>
/// One write request — exactly the parameters the existing CopilotTraceHistoryStore.SaveAsync
/// takes, plus the response + per-step model dictionary. Reference-typed (mutable) deliberately
/// so the queue producer doesn't allocate extra copies on enqueue.
/// </summary>
public sealed class TraceWriteRequest
{
    public required CopilotChatResponse Response { get; init; }
    public required long ElapsedMs { get; init; }
    public int? SessionId { get; init; }
    public string? CaseCode { get; init; }
    public string? PipelineTraceId { get; init; }
    public string? GeneratedScript { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SourceSuite { get; init; }
    public string? ExpectedScript { get; init; }
    public int? LlmCallCount { get; init; }
    public int? TotalPromptTokens { get; init; }
    public int? TotalCompletionTokens { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
    public string? StepModelsJson { get; init; }
    /// <summary>Set when a follow-up embedding write should run after the row lands.
    /// The embedding worker walks back to the row via PipelineTraceId.</summary>
    public bool ShouldEmbedQuestion { get; init; }
    public string? QuestionForEmbedding { get; init; }
}

internal sealed class TraceWriteQueue : ITraceWriteQueue
{
    private readonly Channel<TraceWriteRequest> _channel;

    public TraceWriteQueue()
    {
        // Single-reader (hosted service), multi-producer (any pipeline scope can enqueue).
        // Unbounded — trace rows are small; backpressure via the worker's serial drain is fine.
        _channel = Channel.CreateUnbounded<TraceWriteRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryEnqueue(TraceWriteRequest request)
    {
        return _channel.Writer.TryWrite(request);
    }

    public ChannelReader<TraceWriteRequest> Reader => _channel.Reader;
}
