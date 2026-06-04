namespace AnalystAgent.Api;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Models;

[ApiController]
[Route("api/analyst-agent")]
public sealed class AnalystAgentController : ControllerBase
{
    private readonly IAnalystAgent _copilot;
    private readonly IConversationPersister _conversationPersister;
    private readonly IOptions<AnalystOptions> _options;

    public AnalystAgentController(
        IAnalystAgent copilot,
        IConversationPersister conversationPersister,
        IOptions<AnalystOptions> options)
    {
        _copilot = copilot;
        _conversationPersister = conversationPersister;
        _options = options;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<AnalystResponse>> Ask([FromBody] AnalystRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "question is required" });

        var maxLen = _options.Value.MaxQuestionLength;
        if (maxLen > 0 && request.Question.Length > maxLen)
            return BadRequest(new { error = $"question exceeds the maximum length of {maxLen} characters" });

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
