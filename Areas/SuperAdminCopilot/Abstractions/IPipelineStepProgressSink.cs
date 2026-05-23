namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

/// <summary>
/// Fire-and-forget sink the orchestrator pumps live pipeline events into. The host
/// implementation broadcasts them over SignalR so the chat UI can paint a Claude-style
/// progress timeline ("SemanticSearch ✓ 320ms", "SpecExtractor …") while the question
/// is still in flight. Eval / test hosts can register the <see cref="NullPipelineStepProgressSink"/>
/// no-op to opt out without changing the orchestrator code.
/// </summary>
/// <remarks>
/// <para>Both methods must be non-throwing — pipeline correctness is paramount, broadcast
/// failures (SignalR down, slow client, etc.) must never abort a question.</para>
/// <para>The <see cref="ProgressTarget"/> carries both the SignalR connection id (when known)
/// and the conversation id. The connection id is preferred when present because for a
/// brand-new chat the client can't have joined the chat_{conversationId} group yet — those
/// events would otherwise be lost.</para>
/// </remarks>
public interface IPipelineStepProgressSink
{
    /// <summary>
    /// Called the moment the orchestrator decides to enter a stage, BEFORE the work runs.
    /// Surfaces "I'm now calling X" so the user sees where time is being spent on long stages.
    /// </summary>
    void NotifyStepStarting(ProgressTarget target, string stage);

    /// <summary>
    /// Called when a step is appended to the trace — i.e. after a stage finished. Lets the
    /// UI close out the prior step's elapsed duration. The <paramref name="step"/> carries
    /// the canonical stage name, status, and elapsed time the trace will be persisted with.
    /// </summary>
    void NotifyStepCompleted(ProgressTarget target, PipelineStep step);
}

/// <summary>
/// Bag carrying the addressing info the sink needs to find the right client. Prefer
/// <see cref="ConnectionId"/> when set — group joining has a known race for brand-new chats.
/// </summary>
public readonly record struct ProgressTarget(string? ConnectionId, string? ConversationId)
{
    public bool IsAddressable => !string.IsNullOrEmpty(ConnectionId) || !string.IsNullOrEmpty(ConversationId);
}

/// <summary>
/// No-op sink — used by the eval/test host where no SignalR connection exists. Registered
/// as the default in DI; the production host overrides it with the SignalR-backed adapter.
/// </summary>
public sealed class NullPipelineStepProgressSink : IPipelineStepProgressSink
{
    public static readonly NullPipelineStepProgressSink Instance = new();
    public void NotifyStepStarting(ProgressTarget target, string stage) { }
    public void NotifyStepCompleted(ProgressTarget target, PipelineStep step) { }
}
