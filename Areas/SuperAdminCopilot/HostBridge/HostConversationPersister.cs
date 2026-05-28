namespace SuperAdminCopilot.HostBridge;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Host bridge implementing <see cref="IConversationPersister"/> against the host's
/// CopilotChatSessions / CopilotChatMessages tables. THIS IS THE ONLY conversation-persistence
/// file that knows the host's chat schema. Replace this class to port the copilot to a
/// host with a different conversation model — the controller and orchestrator don't change.
/// </summary>
internal sealed class HostConversationPersister : IConversationPersister
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<HostConversationPersister> _logger;

    public HostConversationPersister(ApplicationDbContext db, ILogger<HostConversationPersister> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task PersistTurnAsync(
        string? conversationId,
        string question,
        string assistantContent,
        int? traceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        if (!int.TryParse(conversationId, out var sessionId) || sessionId <= 0) return;

        try
        {
            var session = await _db.CopilotChatSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId && !s.IsDeleted, cancellationToken);
            if (session is null) return;

            var now = DateTime.UtcNow;
            _db.CopilotChatMessages.Add(new CopilotChatMessageEntity
            {
                SessionId = sessionId,
                Role = ChatMessageRole.User,
                Content = question,
                CreatedAt = now,
            });
            _db.CopilotChatMessages.Add(new CopilotChatMessageEntity
            {
                SessionId = sessionId,
                Role = ChatMessageRole.Assistant,
                Content = assistantContent,
                TraceId = traceId,
                CreatedAt = now.AddTicks(1),
            });
            session.LastInteractionAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HostConversationPersister] Failed to persist chat turn for session {SessionId}.", sessionId);
        }
    }
}
