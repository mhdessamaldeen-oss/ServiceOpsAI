using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline;

namespace AISupportAnalysisPlatform.Controllers.AI;

// Backend-authoritative workflow graph endpoint.
//
// The Investigation > Workflow tab used to hardcode stage descriptions, route detection,
// and graph layout in Razor (806 lines) + investigation-pipeline.js (846 lines). Every
// orchestrator change required three coordinated edits and they drifted. This endpoint
// replaces that: returns a fully-formed WorkflowGraph for any trace; the frontend
// renders verbatim with no per-stage logic of its own.
//
// Migration path for the frontend:
//   1. Call GET /api/workflowgraph/trace/{traceId} on tab open.
//   2. Iterate nodes[] — render each as a box with label / description / status / kind.
//   3. Iterate edges[] — render each as an arrow; color by wasTaken.
//   4. Delete STEP_DESCRIPTIONS dictionary in investigation-pipeline.js.
//   5. Delete the route-detection block in _InvestigationWorkflow.cshtml.
[ApiController]
[Route("api/[controller]")]
public sealed class WorkflowGraphController : ControllerBase
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly IWorkflowGraphBuilder _builder;
    private readonly ILogger<WorkflowGraphController> _logger;

    public WorkflowGraphController(
        IDbContextFactory<ApplicationDbContext> contextFactory,
        IWorkflowGraphBuilder builder,
        ILogger<WorkflowGraphController> logger)
    {
        _contextFactory = contextFactory;
        _builder = builder;
        _logger = logger;
    }

    // GET /api/workflowgraph/trace/{traceId}
    // traceId may be a numeric CopilotTraceHistories.Id or a string PipelineTraceId.
    [HttpGet("trace/{traceId}")]
    public async Task<IActionResult> Get(string traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId)) return BadRequest(new { error = "traceId required" });

        await using var db = await _contextFactory.CreateDbContextAsync();
        CopilotTraceHistory? history = null;
        if (int.TryParse(traceId, out var numericId))
            history = await db.CopilotTraceHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == numericId);
        history ??= await db.CopilotTraceHistories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.PipelineTraceId == traceId);

        if (history is null || string.IsNullOrWhiteSpace(history.ExecutionPlan))
            return NotFound(new { error = $"trace '{traceId}' not found" });

        AdminCopilotExecutionDetails? details;
        try
        {
            details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(history.ExecutionPlan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WorkflowGraph] failed to parse ExecutionPlan for trace {TraceId}", traceId);
            return Problem("ExecutionPlan JSON could not be parsed.");
        }

        var pipelineSteps = (details?.Steps ?? new List<CopilotExecutionStep>())
            .Select(ToPipelineStep)
            .ToList();

        var graph = _builder.Build(pipelineSteps, history.ErrorMessage, history.TotalElapsedMs);
        return Ok(graph);
    }

    // Convert the host-side CopilotExecutionStep (persisted shape) into the orchestrator-side
    // PipelineStep so the WorkflowGraphBuilder can consume it. Same data, different POCO.
    private static PipelineStep ToPipelineStep(CopilotExecutionStep s)
    {
        var status = s.Status == CopilotStepStatus.Error
            ? StageNames.StatusFailed
            : StageNames.StatusOk;
        return new PipelineStep(
            Stage: s.Action ?? string.Empty,
            Status: status,
            ElapsedMs: s.ElapsedMs,
            StartedAt: s.StartedAt,
            Detail: s.Detail,
            TechnicalData: s.TechnicalData,
            Kind: null,
            SubSteps: s.SubSteps?.Select(ToPipelineStep).ToList());
    }
}
