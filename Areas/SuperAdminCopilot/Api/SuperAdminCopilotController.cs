namespace SuperAdminCopilot.Api;

using Microsoft.AspNetCore.Mvc;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;

[ApiController]
[Route("api/super-admin-copilot")]
public sealed class SuperAdminCopilotController : ControllerBase
{
    private readonly ISuperAdminCopilot _copilot;

    public SuperAdminCopilotController(ISuperAdminCopilot copilot) => _copilot = copilot;

    [HttpPost("ask")]
    public async Task<ActionResult<CopilotResponse>> Ask([FromBody] CopilotRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "question is required" });

        var response = await _copilot.AskAsync(request, ct);
        return Ok(response);
    }
}
