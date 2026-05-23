using System.Text.Json.Serialization;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Root model for pipeline execution trace - represents a complete investigation
    /// </summary>
    public class PipelineExecutionTrace
    {
        public string TraceId { get; set; } = Guid.NewGuid().ToString();
        public string SessionId { get; set; } = "";
        public string OriginalQuestion { get; set; } = "";
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public PipelineStatus Status { get; set; } = PipelineStatus.Running;
        
        /// <summary>
        /// Root trees - each compound query sub-question gets its own tree
        /// </summary>
        public List<PipelineTree> Trees { get; set; } = new();
        
        /// <summary>
        /// Merge points where sub-query results combine
        /// </summary>
        public List<PipelineMergePoint> MergePoints { get; set; } = new();
        
        /// <summary>
        /// Final merged result
        /// </summary>
        public PipelineNode? FinalResult { get; set; }
        
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
    
    /// <summary>
    /// A complete tree for one sub-query (compound queries have multiple trees)
    /// </summary>
    public class PipelineTree
    {
        public string TreeId { get; set; } = Guid.NewGuid().ToString();
        public string SubQueryText { get; set; } = "";
        public int SubQueryOrder { get; set; } = 0;
        public PipelineNode RootNode { get; set; } = null!;
        public TreeStatus Status { get; set; } = TreeStatus.Pending;
        public List<string> Dependencies { get; set; } = new(); // Other tree IDs this depends on
    }
    
    /// <summary>
    /// A node in the pipeline tree - represents one execution step
    /// </summary>
    public class PipelineNode
    {
        public string NodeId { get; set; } = Guid.NewGuid().ToString();
        public string TreeId { get; set; } = "";
        public string ParentNodeId { get; set; } = "";
        
        /// <summary>
        /// Display name for the tree
        /// </summary>
        public string Name { get; set; } = "";
        
        /// <summary>
        /// Step type determines color and icon in UI
        /// </summary>
        public PipelineStepType StepType { get; set; }
        
        /// <summary>
        /// Current execution status
        /// </summary>
        public NodeStatus Status { get; set; } = NodeStatus.Pending;
        
        /// <summary>
        /// Timing information
        /// </summary>
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long DurationMs => CompletedAt.HasValue ? (long)(CompletedAt.Value - StartedAt).TotalMilliseconds : 0;
        
        /// <summary>
        /// Detailed step information (click to expand)
        /// </summary>
        public PipelineStepDetails Details { get; set; } = new();
        
        /// <summary>
        /// Child nodes (sub-steps)
        /// </summary>
        public List<PipelineNode> Children { get; set; } = new();
        
        /// <summary>
        /// For fork/join visualization - if this node forks into parallel branches
        /// </summary>
        public bool IsForkPoint { get; set; }
        public List<string>? ForkedNodeIds { get; set; }
        
        /// <summary>
        /// For fork/join visualization - if this node joins branches
        /// </summary>
        public bool IsJoinPoint { get; set; }
        public List<string>? JoinedNodeIds { get; set; }
        
        /// <summary>
        /// Indicates if this node was on the actually executed path
        /// Alternative paths (not taken) will be shown dimmed
        /// </summary>
        public bool IsExecutedPath { get; set; } = true;
    }
    
    /// <summary>
    /// Detailed information about a pipeline step - shown when clicking a node
    /// </summary>
    public class PipelineStepDetails
    {
        /// <summary>
        /// Human-readable description of what this step does
        /// </summary>
        public string Description { get; set; } = "";
        
        /// <summary>
        /// Input data to this step (e.g., user question, previous output)
        /// </summary>
        public PipelineDataSnapshot? Input { get; set; }
        
        /// <summary>
        /// Output data from this step
        /// </summary>
        public PipelineDataSnapshot? Output { get; set; }
        
        /// <summary>
        /// For AI model calls: the full prompt sent
        /// </summary>
        public string? ModelPrompt { get; set; }
        
        /// <summary>
        /// For AI model calls: the raw response
        /// </summary>
        public string? ModelResponse { get; set; }
        
        /// <summary>
        /// For SQL generation: the generated SQL query
        /// </summary>
        public string? GeneratedSql { get; set; }
        
        /// <summary>
        /// For SQL execution: query results
        /// </summary>
        public object? QueryResults { get; set; }
        public int? ResultRowCount { get; set; }
        
        /// <summary>
        /// Validation results if applicable
        /// </summary>
        public ValidationDetails? Validation { get; set; }
        
        /// <summary>
        /// Any errors that occurred
        /// </summary>
        public List<PipelineError> Errors { get; set; } = new();
        
        /// <summary>
        /// Metrics: tokens used, cost, etc.
        /// </summary>
        public PipelineMetrics? Metrics { get; set; }
        
        /// <summary>
        /// Debug information
        /// </summary>
        public Dictionary<string, object> DebugInfo { get; set; } = new();
    }
    
    public class PipelineDataSnapshot
    {
        public string DataType { get; set; } = ""; // e.g., "UserQuestion", "IntentPlan", "SqlQuery"
        public string Content { get; set; } = "";
        public Dictionary<string, object>? StructuredData { get; set; }
    }
    
    public class ValidationDetails
    {
        public bool IsValid { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public string? ValidationSql { get; set; }
    }
    
    public class PipelineMetrics
    {
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public decimal? EstimatedCost { get; set; }
        public long? ProcessingTimeMs { get; set; }
    }
    
    public class PipelineError
    {
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Point where multiple tree results merge
    /// </summary>
    public class PipelineMergePoint
    {
        public string MergeId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public List<string> SourceTreeIds { get; set; } = new();
        public string? TargetTreeId { get; set; }
        public MergeStrategy Strategy { get; set; } = MergeStrategy.Sequential;
        public PipelineNode? MergeNode { get; set; }
    }
    
    public enum PipelineStatus { Running, Completed, Failed, Partial, NotAvailable }
    public enum TreeStatus { Pending, Running, Completed, Failed }
    public enum NodeStatus { Pending, Running, Completed, Failed, Skipped }
    
    public enum PipelineStepType
    {
        // Input/Output
        UserInput,          // Green
        FinalResponse,      // Dark Green
        
        // Classification & Planning
        IntentClassification, // Blue
        QueryDecomposition,   // Light Blue
        EntityResolution,     // Cyan
        TemporalParsing,      // Teal
        OutputShapeDetection, // Indigo
        
        // SQL Generation
        SchemaRetrieval,      // Orange
        SqlGeneration,        // Deep Orange
        SqlValidation,        // Amber
        SqlExecution,         // Yellow
        
        // Data Processing
        DataRetrieval,        // Purple
        DataTransformation,   // Pink
        ResultFormatting,     // Magenta
        
        // Control Flow
        Fork,                 // Gray
        Join,                 // Dark Gray
        Merge,                // Slate
        Retry,                // Red
        Fallback              // Dark Red
    }
    
    public enum MergeStrategy
    {
        Sequential,     // Execute trees one after another
        Parallel,       // Execute trees concurrently
        Conditional,    // Execute based on previous results
        Aggregated      // Combine results into single output
    }
    
    /// <summary>
    /// Request to visualize a pipeline
    /// </summary>
    public class PipelineVisualizationRequest
    {
        public string TraceId { get; set; } = "";
        public string? SessionId { get; set; }
        public string? Question { get; set; }
    }
    
    /// <summary>
    /// Response with tree data for visualization
    /// </summary>
    public class PipelineVisualizationResponse
    {
        public string TraceId { get; set; } = "";
        public PipelineStatus Status { get; set; } = PipelineStatus.NotAvailable;
        public List<PipelineTreeDto> Trees { get; set; } = new();
        public List<MergeConnectionDto> MergeConnections { get; set; } = new();
        public PipelineNodeDto? FinalResult { get; set; }
        public Dictionary<string, string> ColorScheme { get; set; } = new();
    }
    
    /// <summary>
    /// DTO for tree structure (simplified for serialization)
    /// </summary>
    public class PipelineTreeDto
    {
        public string TreeId { get; set; } = "";
        public string SubQueryText { get; set; } = "";
        public int Order { get; set; }
        public string Status { get; set; } = "";
        public PipelineNodeDto Root { get; set; } = null!;
    }
    
    public class PipelineNodeDto
    {
        public string NodeId { get; set; } = "";
        public string Name { get; set; } = "";
        public string StepType { get; set; } = "";
        public string Status { get; set; } = "";
        public long DurationMs { get; set; }
        public bool HasDetails { get; set; }
        public bool IsForkPoint { get; set; }
        public bool IsJoinPoint { get; set; }
        public bool IsExecutedPath { get; set; } = true;
        public List<PipelineNodeDto> Children { get; set; } = new();
    }
    
    public class MergeConnectionDto
    {
        public string MergeId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> SourceTreeIds { get; set; } = new();
        public string? TargetTreeId { get; set; }
        public string Strategy { get; set; } = "";
    }
    
    /// <summary>
    /// Request to get detailed information about a specific node
    /// </summary>
    public class NodeDetailsRequest
    {
        public string TraceId { get; set; } = "";
        public string TreeId { get; set; } = "";
        public string NodeId { get; set; } = "";
    }
    
    public class NodeDetailsResponse
    {
        public string NodeId { get; set; } = "";
        public PipelineStepDetails Details { get; set; } = new();
    }
}
