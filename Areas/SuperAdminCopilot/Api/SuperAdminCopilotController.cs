namespace SuperAdminCopilot.Api;

using Microsoft.AspNetCore.Mvc;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;

[ApiController]
[Route("api/super-admin-copilot")]
public sealed class SuperAdminCopilotController : ControllerBase
{
    private readonly ISuperAdminCopilot _copilot;
    private readonly IConversationPersister _conversationPersister;

    public SuperAdminCopilotController(
        ISuperAdminCopilot copilot,
        IConversationPersister conversationPersister)
    {
        _copilot = copilot;
        _conversationPersister = conversationPersister;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<CopilotResponse>> Ask([FromBody] CopilotRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "question is required" });

        var response = await _copilot.AskAsync(request, ct);

        // Persist via the host-bridged persister (HostConversationPersister maps onto
        // CopilotChatSessions / CopilotChatMessages today). The controller is now free of
        // host schema knowledge — downstream apps swap the bridge, not this file.
        var assistantContent = !string.IsNullOrWhiteSpace(response.Error)
            ? response.Error!
            : (response.Reply ?? string.Empty);
        await _conversationPersister.PersistTurnAsync(
            conversationId: request.ConversationId,
            question: request.Question,
            assistantContent: assistantContent,
            traceId: response.TraceId,
            cancellationToken: ct);

        return Ok(response);
    }
}
