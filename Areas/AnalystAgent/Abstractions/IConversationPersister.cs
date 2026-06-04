namespace AnalystAgent.Abstractions;

/// <summary>
/// Abstracts the host's chat-session/message persistence so the copilot module never
/// reaches into the host's DbContext directly. The controller calls this after each
/// successful <c>AskAsync</c> to record the question + assistant reply against whichever
/// conversation the caller supplied.
///
/// <para>The host implementation (<c>HostConversationPersister</c>) maps the abstract
/// conversation/turn shape onto whatever entities the host actually uses (today:
/// <c>CopilotChatSessions</c> + <c>CopilotChatMessages</c>). Downstream apps wanting to
/// reuse the copilot replace ONLY this bridge — the controller doesn't change.</para>
/// </summary>
public interface IConversationPersister
{
    /// <summary>
    /// Append the user's question and the assistant's reply (or error) to the conversation
    /// identified by <paramref name="conversationId"/>. Implementations should resolve the
    /// id to whatever shape the host uses (int sessionId, GUID, etc.) and silently no-op
    /// when the id doesn't match a known conversation — the copilot already returned its
    /// answer, so persistence failures must not break the user-facing response.
    /// </summary>
    /// <param name="conversationId">Opaque conversation identifier as supplied by the caller.
    /// Implementations choose how to parse it. Null/empty/unknown = no-op.</param>
    /// <param name="question">The user's question. Stored verbatim.</param>
    /// <param name="assistantContent">The assistant's reply, or the error message when
    /// the answer failed. Implementations store whichever was non-empty.</param>
    /// <param name="traceId">Trace-history row id to associate with the assistant message.
    /// Null when the trace sink didn't return one (e.g. trace persistence failed).</param>
    Task PersistTurnAsync(
        string? conversationId,
        string question,
        string assistantContent,
        int? traceId,
        CancellationToken cancellationToken);
}
