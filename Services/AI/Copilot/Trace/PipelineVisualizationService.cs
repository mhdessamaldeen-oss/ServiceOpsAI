using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Data;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Services.AI.Copilot.Trace
{
    /// <summary>
    /// Service to format pipeline traces for visualization
    /// </summary>
    public interface IPipelineVisualizationService
    {
        /// <summary>
        /// Get tree visualization data for a trace
        /// </summary>
        PipelineVisualizationResponse GetVisualization(string traceId);
        
        /// <summary>
        /// Get detailed information for a specific node
        /// </summary>
        NodeDetailsResponse GetNodeDetails(string traceId, string nodeId);
        
        /// <summary>
        /// List all traces for a session
        /// </summary>
        List<PipelineTraceSummary> GetTracesForSession(string sessionId);
        
        /// <summary>
        /// Get color scheme for different step types
        /// </summary>
        Dictionary<string, string> GetColorScheme();
    }
    
    public class PipelineVisualizationService : IPipelineVisualizationService
    {
        private readonly IPipelineTracingService _tracingService;
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ILogger<PipelineVisualizationService> _logger;
        
        public PipelineVisualizationService(
            IPipelineTracingService tracingService,
            IDbContextFactory<ApplicationDbContext> _contextFactory,
            ILogger<PipelineVisualizationService> logger)
        {
            _tracingService = tracingService;
            this._contextFactory = _contextFactory;
            _logger = logger;
        }
        
        public PipelineVisualizationResponse GetVisualization(string traceId)
        {
            var resolvedTraceId = ResolveTraceId(traceId);
            var trace = _tracingService.GetTrace(resolvedTraceId);
            if (trace == null)
            {
                throw new ArgumentException($"Trace {traceId} not found (resolved to {resolvedTraceId})");
            }
            
            var response = new PipelineVisualizationResponse
            {
                TraceId = traceId,
                Status = trace.Status,
                ColorScheme = GetColorScheme()
            };
            
            // Convert trees to DTOs
            foreach (var tree in trace.Trees.OrderBy(t => t.SubQueryOrder))
            {
                var treeDto = new PipelineTreeDto
                {
                    TreeId = tree.TreeId,
                    SubQueryText = tree.SubQueryText,
                    Order = tree.SubQueryOrder,
                    Status = tree.Status.ToString(),
                    Root = ConvertNodeToDto(tree.RootNode)
                };
                
                response.Trees.Add(treeDto);
            }
            
            // Convert merge points
            foreach (var merge in trace.MergePoints)
            {
                response.MergeConnections.Add(new MergeConnectionDto
                {
                    MergeId = merge.MergeId,
                    Name = merge.Name,
                    SourceTreeIds = merge.SourceTreeIds,
                    TargetTreeId = merge.TargetTreeId,
                    Strategy = merge.Strategy.ToString()
                });
            }
            
            // Set final result
            if (trace.FinalResult != null)
            {
                response.FinalResult = ConvertNodeToDto(trace.FinalResult);
            }
            
            return response;
        }
        
        public NodeDetailsResponse GetNodeDetails(string traceId, string nodeId)
        {
            var resolvedTraceId = ResolveTraceId(traceId);
            var node = _tracingService.GetNode(resolvedTraceId, nodeId);
            if (node == null)
            {
                throw new ArgumentException($"Node {nodeId} not found in trace {resolvedTraceId}");
            }
            
            return new NodeDetailsResponse
            {
                NodeId = nodeId,
                Details = node.Details
            };
        }
        
        public List<PipelineTraceSummary> GetTracesForSession(string sessionId)
        {
            var traces = _tracingService.GetTracesForSession(sessionId);
            
            return traces.Select(t => new PipelineTraceSummary
            {
                TraceId = t.TraceId,
                OriginalQuestion = t.OriginalQuestion,
                StartedAt = t.StartedAt,
                CompletedAt = t.CompletedAt,
                Status = t.Status.ToString(),
                TreeCount = t.Trees.Count,
                DurationMs = t.CompletedAt.HasValue 
                    ? (long)(t.CompletedAt.Value - t.StartedAt).TotalMilliseconds 
                    : (long)(DateTime.UtcNow - t.StartedAt).TotalMilliseconds
            }).ToList();
        }
        
        public Dictionary<string, string> GetColorScheme()
        {
            return new Dictionary<string, string>
            {
                // Input/Output
                ["UserInput"] = "#22c55e",      // Green-500
                ["FinalResponse"] = "#15803d",  // Green-700
                
                // Classification & Planning
                ["IntentClassification"] = "#3b82f6",   // Blue-500
                ["QueryDecomposition"] = "#60a5fa",     // Blue-400
                ["EntityResolution"] = "#06b6d4",       // Cyan-500
                ["TemporalParsing"] = "#14b8a6",        // Teal-500
                ["OutputShapeDetection"] = "#6366f1",  // Indigo-500
                
                // SQL Generation
                ["SchemaRetrieval"] = "#f97316",   // Orange-500
                ["SqlGeneration"] = "#ea580c",     // Orange-600
                ["SqlValidation"] = "#f59e0b",     // Amber-500
                ["SqlExecution"] = "#eab308",      // Yellow-500
                
                // Data Processing
                ["DataRetrieval"] = "#a855f7",      // Purple-500
                ["DataTransformation"] = "#ec4899", // Pink-500
                ["ResultFormatting"] = "#d946ef", // Magenta-500
                
                // Control Flow
                ["Fork"] = "#6b7280",       // Gray-500
                ["Join"] = "#4b5563",       // Gray-600
                ["Merge"] = "#64748b",      // Slate-500
                ["Retry"] = "#ef4444",      // Red-500
                ["Fallback"] = "#b91c1c"    // Red-700
            };
        }
        
        private PipelineNodeDto ConvertNodeToDto(PipelineNode node)
        {
            var dto = new PipelineNodeDto
            {
                NodeId = node.NodeId,
                Name = node.Name,
                StepType = node.StepType.ToString(),
                Status = node.Status.ToString(),
                DurationMs = node.DurationMs,
                HasDetails = HasMeaningfulDetails(node.Details),
                IsForkPoint = node.IsForkPoint,
                IsJoinPoint = node.IsJoinPoint,
                IsExecutedPath = node.IsExecutedPath
            };
            
            foreach (var child in node.Children)
            {
                dto.Children.Add(ConvertNodeToDto(child));
            }
            
            return dto;
        }

        private bool HasMeaningfulDetails(PipelineStepDetails details)
        {
            return !string.IsNullOrEmpty(details.ModelPrompt) 
                || !string.IsNullOrEmpty(details.GeneratedSql)
                || details.QueryResults != null
                || details.Validation != null
                || details.Errors.Any();
        }
        
        private string ResolveTraceId(string traceId)
        {
            if (string.IsNullOrEmpty(traceId)) return traceId;

            // If it's a GUID, it's already a PipelineTraceId
            if (Guid.TryParse(traceId, out _)) return traceId;

            // If it's an integer, it's likely a database ID
            if (int.TryParse(traceId, out int dbId))
            {
                using var context = _contextFactory.CreateDbContext();
                var traceRecord = context.CopilotTraceHistories.Find(dbId);
                if (traceRecord != null && !string.IsNullOrEmpty(traceRecord.PipelineTraceId))
                {
                    return traceRecord.PipelineTraceId;
                }
            }

            return traceId;
        }
    }
    
    public class PipelineTraceSummary
    {
        public string TraceId { get; set; } = "";
        public string OriginalQuestion { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "";
        public int TreeCount { get; set; }
        public long DurationMs { get; set; }
    }
}
