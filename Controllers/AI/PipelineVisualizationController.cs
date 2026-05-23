using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Copilot.Trace;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Controllers.AI
{
    /// <summary>
    /// API for visualizing copilot pipeline execution
    /// Shows tree structure with fork/merge points, SQL generation, model I/O
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class PipelineVisualizationController : ControllerBase
    {
        private readonly IPipelineTracingService _tracingService;
        private readonly IPipelineVisualizationService _visualizationService;
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ILogger<PipelineVisualizationController> _logger;

        public PipelineVisualizationController(
            IPipelineTracingService tracingService,
            IPipelineVisualizationService visualizationService,
            IDbContextFactory<ApplicationDbContext> contextFactory,
            ILogger<PipelineVisualizationController> logger)
        {
            _tracingService = tracingService;
            _visualizationService = visualizationService;
            _contextFactory = contextFactory;
            _logger = logger;
        }

        /// <summary>
        /// Get the full tree visualization for a trace.
        /// First tries the in-memory tracing service (for live traces in this app instance).
        /// Falls back to building a synthetic visualization from the persisted ExecutionPlan JSON
        /// in CopilotTraceHistories so the Pipeline tab works for traces created before the
        /// current app process started, or via paths that didn't register with the tracing service.
        /// </summary>
        [HttpGet("trace/{traceId}")]
        public async Task<ActionResult<PipelineVisualizationResponse>> GetTraceVisualization(string traceId)
        {
            try
            {
                var visualization = _visualizationService.GetVisualization(traceId);
                return Ok(visualization);
            }
            catch (ArgumentException)
            {
                // Live trace gone — try to rebuild from persisted history.
                var fallback = await BuildVisualizationFromHistoryAsync(traceId);
                return Ok(fallback);
            }
        }

        /// <summary>
        /// Reconstruct a Pipeline visualization from the saved <c>CopilotTraceHistory.ExecutionPlan</c>
        /// JSON. The traceId may be either:
        ///   1. A pipeline trace GUID stored in <c>PipelineTraceId</c>
        ///   2. A numeric <c>CopilotTraceHistory.Id</c> (used as fallback in the view's data-trace-id)
        /// Returns a single-tree visualization where each <c>CopilotExecutionStep</c> is one node.
        /// </summary>
        private async Task<PipelineVisualizationResponse> BuildVisualizationFromHistoryAsync(string traceId)
        {
            var notAvailable = new PipelineVisualizationResponse
            {
                TraceId = traceId,
                Status = PipelineStatus.NotAvailable,
                Trees = new List<PipelineTreeDto>(),
                MergeConnections = new List<MergeConnectionDto>()
            };

            try
            {
                using var db = await _contextFactory.CreateDbContextAsync();
                CopilotTraceHistory? history = null;

                if (int.TryParse(traceId, out var numericId))
                {
                    history = await db.CopilotTraceHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == numericId);
                }
                history ??= await db.CopilotTraceHistories.AsNoTracking()
                    .FirstOrDefaultAsync(h => h.PipelineTraceId == traceId);

                if (history == null || string.IsNullOrWhiteSpace(history.ExecutionPlan)) return notAvailable;

                var details = JsonSerializer.Deserialize<AdminCopilotExecutionDetails>(history.ExecutionPlan);
                if (details?.Steps == null || details.Steps.Count == 0) return notAvailable;

                // Build a single tree where the question is the root and each step becomes a child node.
                var rootNode = new PipelineNodeDto
                {
                    NodeId = "root",
                    Name = string.IsNullOrWhiteSpace(details.SearchQuery) ? "Pipeline" : details.SearchQuery,
                    StepType = "UserInput",
                    Status = "Completed",
                    DurationMs = details.TotalElapsedMs,
                    HasDetails = false,
                    IsExecutedPath = true,
                    Children = details.Steps.Select((step, idx) => new PipelineNodeDto
                    {
                        NodeId = $"step-{idx}",
                        Name = step.Action ?? $"Step {idx + 1}",
                        StepType = MapLayerToStepType(step.Layer),
                        Status = step.Status switch
                        {
                            CopilotStepStatus.Ok    => "Completed",
                            CopilotStepStatus.Warn  => "Completed",
                            CopilotStepStatus.Error => "Failed",
                            CopilotStepStatus.Skip  => "Skipped",
                            _                       => "Completed"
                        },
                        DurationMs = step.ElapsedMs,
                        HasDetails = !string.IsNullOrWhiteSpace(step.TechnicalData) || !string.IsNullOrWhiteSpace(step.Detail),
                        IsExecutedPath = step.Status != CopilotStepStatus.Skip,
                        Children = new List<PipelineNodeDto>()
                    }).ToList()
                };

                var tree = new PipelineTreeDto
                {
                    TreeId = "main",
                    SubQueryText = string.IsNullOrWhiteSpace(details.SearchQuery) ? "Main pipeline" : details.SearchQuery,
                    Order = 0,
                    Status = details.Steps.Any(s => s.Status == CopilotStepStatus.Error) ? "Failed" : "Completed",
                    Root = rootNode
                };

                return new PipelineVisualizationResponse
                {
                    TraceId = traceId,
                    Status = details.Steps.Any(s => s.Status == CopilotStepStatus.Error) ? PipelineStatus.Failed : PipelineStatus.Completed,
                    Trees = new List<PipelineTreeDto> { tree },
                    MergeConnections = new List<MergeConnectionDto>(),
                    ColorScheme = _visualizationService.GetColorScheme()
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build fallback pipeline visualization for trace {TraceId}", traceId);
                return notAvailable;
            }
        }

        private static string MapLayerToStepType(CopilotExecutionLayer layer) => layer switch
        {
            CopilotExecutionLayer.Context       => "UserInput",
            CopilotExecutionLayer.Router        => "IntentClassification",
            CopilotExecutionLayer.DataPlanning  => "SqlGeneration",
            CopilotExecutionLayer.DataExecution => "SqlExecution",
            CopilotExecutionLayer.Executor      => "SqlExecution",
            CopilotExecutionLayer.Complete      => "FinalResponse",
            _                                   => "DataTransformation"
        };

        /// <summary>
        /// Get detailed information for a specific node
        /// Shows: SQL, model prompts, inputs/outputs, errors
        /// </summary>
        [HttpGet("trace/{traceId}/node/{nodeId}")]
        public ActionResult<NodeDetailsResponse> GetNodeDetails(string traceId, string nodeId)
        {
            try
            {
                var details = _visualizationService.GetNodeDetails(traceId, nodeId);
                return Ok(details);
            }
            catch (ArgumentException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get all traces for a session
        /// </summary>
        [HttpGet("session/{sessionId}/traces")]
        public ActionResult<List<PipelineTraceSummary>> GetSessionTraces(string sessionId)
        {
            var traces = _visualizationService.GetTracesForSession(sessionId);
            return Ok(traces);
        }

        /// <summary>
        /// Get color scheme for different step types
        /// </summary>
        [HttpGet("colors")]
        public ActionResult<Dictionary<string, string>> GetColorScheme()
        {
            return Ok(_visualizationService.GetColorScheme());
        }

        /// <summary>
        /// Get the raw trace data (for debugging)
        /// </summary>
        [HttpGet("trace/{traceId}/raw")]
        public ActionResult<PipelineExecutionTrace> GetRawTrace(string traceId)
        {
            var trace = _tracingService.GetTrace(traceId);
            if (trace == null)
            {
                return NotFound(new { error = $"Trace {traceId} not found" });
            }
            return Ok(trace);
        }
    }
}
