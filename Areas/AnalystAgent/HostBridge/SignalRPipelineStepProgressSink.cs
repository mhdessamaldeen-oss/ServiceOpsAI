namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;

/// <summary>
/// Host adapter that pumps the orchestrator's live pipeline events through the existing
/// <see cref="CopilotChatHub"/>. The chat UI in Views/AiAnalysis/Copilot.cshtml is already
/// subscribed to <c>ProgressUpdate</c> — the orchestrator just needed a way to emit them.
/// </summary>
/// <remarks>
/// <para>Targets the SignalR connection id directly when present (<see cref="IClientProxy"/>
/// via <c>Clients.Client(id)</c>). Falls back to broadcasting to the <c>chat_{conversationId}</c>
/// group when only the conversation id is known (e.g. cross-tab session, or the front-end
/// didn't send a connection id). Connection-direct delivery sidesteps the brand-new-chat race
/// where the client can't join the group before the server has assigned the sessionId.</para>
/// <para>Sends are fire-and-forget: if the SignalR backend is unhealthy or the client
/// disconnected, the pipeline must NOT block or throw. Failures are logged at Debug because
/// they are routine (e.g. a user closed the tab mid-question).</para>
/// </remarks>
internal sealed class SignalRPipelineStepProgressSink : IPipelineStepProgressSink
{
    private readonly IHubContext<CopilotChatHub> _hub;
    private readonly ILogger<SignalRPipelineStepProgressSink> _logger;

    public SignalRPipelineStepProgressSink(
        IHubContext<CopilotChatHub> hub,
        ILogger<SignalRPipelineStepProgressSink> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void NotifyStepStarting(ProgressTarget target, string stage)
    {
        if (!target.IsAddressable || string.IsNullOrEmpty(stage)) return;
        Broadcast(target, stage);
    }

    public void NotifyStepCompleted(ProgressTarget target, PipelineStep step)
    {
        if (!target.IsAddressable || step is null) return;
        // Carry the elapsed time inline so the UI's "Thought for…" rollup is rich even when
        // the user never expands the timeline. The user-facing label keeps the canonical
        // stage name from StageNames so future stages flow through unchanged — the elapsed
        // suffix is appended only when the step actually measured a non-zero duration
        // (instant gates like AccessPolicy would otherwise read "AccessPolicy 0ms" — noise).
        var label = step.ElapsedMs > 0
            ? $"{step.Stage} · {FormatElapsed(step.ElapsedMs)}"
            : step.Stage;
        Broadcast(target, label);
    }

    private void Broadcast(ProgressTarget target, string status)
    {
        try
        {
            // Pick the most-specific delivery channel: a known connection id beats a group
            // (we may not have joined the group yet on the first turn).
            IClientProxy clientProxy = !string.IsNullOrEmpty(target.ConnectionId)
                ? _hub.Clients.Client(target.ConnectionId!)
                : _hub.Clients.Group($"chat_{target.ConversationId}");

            // Fire-and-forget. SendAsync returns a Task we intentionally discard; SignalR's
            // own retry / circuit-breaker handles transient transport blips. Any unhandled
            // exception inside the continuation is logged below rather than swallowed silently.
            _ = clientProxy
                .SendAsync("ProgressUpdate", status)
                .ContinueWith(t =>
                {
                    if (t.Exception is not null)
                        _logger.LogDebug(t.Exception, "[SignalR] ProgressUpdate send failed (conn={Conn}, conv={Conv})", target.ConnectionId, target.ConversationId);
                }, TaskScheduler.Default);
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "[SignalR] ProgressUpdate broadcast threw (conn={Conn}, conv={Conv})", target.ConnectionId, target.ConversationId);
        }
    }

    private static string FormatElapsed(long ms) =>
        ms < 1000 ? $"{ms} ms" : $"{(ms / 1000.0):F1} s";
}
