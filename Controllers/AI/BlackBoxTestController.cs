using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.Common;
using AISupportAnalysisPlatform.Services.AI.Copilot.Assessment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AISupportAnalysisPlatform.Controllers.AI;

// TEMPORARY — re-added for the iterative benchmark plan. Deletes at Phase 7.
// Unauthenticated POST endpoint for the local benchmark runner; bypasses the Razor antiforgery
// flow so curl can trigger a suite end-to-end without juggling cookies/CSRF tokens.
// DO NOT ship this to any environment other than dev/local.
[AllowAnonymous]
[ApiController]
[Route("api/blackbox")]
public sealed class BlackBoxTestController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CopilotAssessmentHandler _assessmentHandler;
    private readonly ILogger<BlackBoxTestController> _logger;

    public BlackBoxTestController(
        ApplicationDbContext context,
        IServiceScopeFactory scopeFactory,
        CopilotAssessmentHandler assessmentHandler,
        ILogger<BlackBoxTestController> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _assessmentHandler = assessmentHandler;
        _logger = logger;
    }

    [HttpPost("run/{suiteName}")]
    public async Task<IActionResult> Run(string suiteName)
    {
        if (string.IsNullOrWhiteSpace(suiteName))
            return BadRequest(ApiResponse.Fail("suiteName is required."));

        var suiteFile = suiteName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? suiteName
            : suiteName + ".json";
        _assessmentHandler.SetActiveSuites(new[] { suiteFile });

        var suite = await _assessmentHandler.GetDefaultTestSuiteAsync();
        if (suite is null || suite.Count == 0)
            return NotFound(ApiResponse.Fail($"Suite '{suiteFile}' not found or empty."));

        var session = CopilotAssessmentHandler.CreateNewAssessmentSession(userId: null);
        _context.CopilotChatSessions.Add(session);
        await _context.SaveChangesAsync();
        var sessionId = session.Id;
        var runId = Guid.NewGuid();
        var totalCases = suite.Count;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedHandler = scope.ServiceProvider.GetRequiredService<CopilotAssessmentHandler>();
            var scopedLogger = scope.ServiceProvider.GetRequiredService<ILogger<BlackBoxTestController>>();
            scopedHandler.SetActiveSuites(new[] { suiteFile });
            try
            {
                await scopedHandler.RunAssessmentAsync(suite, userId: null, sessionId, runId);
                scopedLogger.LogInformation("[BlackBox] suite={Suite} runId={RunId} completed.", suiteFile, runId);
            }
            catch (Exception ex)
            {
                scopedLogger.LogError(ex, "[BlackBox] suite={Suite} runId={RunId} failed.", suiteFile, runId);
            }
        });

        return Ok(ApiResponse<object>.Ok(
            new { suiteFile, sessionId, totalCases, runId },
            $"BlackBox run started for '{suiteFile}' ({totalCases} cases)."));
    }

    [HttpGet("status/{suiteName}")]
    public IActionResult Status(string suiteName)
    {
        if (string.IsNullOrWhiteSpace(suiteName))
            return BadRequest(ApiResponse.Fail("suiteName is required."));
        var baseName = suiteName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? suiteName[..^5]
            : suiteName;
        var count = _context.CopilotTraceHistories.Count(t => t.SourceSuite == baseName);
        return Ok(ApiResponse<object>.Ok(new { suiteName = baseName, traceCount = count }, "ok"));
    }
}
