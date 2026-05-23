using ServiceOpsAI.Models.AI;
using System.Collections.Concurrent;

namespace ServiceOpsAI.Services.AI.Copilot.Trace
{
    /// <summary>
    /// Service to trace and capture the entire pipeline execution for visualization.
    /// Each compound query sub-question gets its own tree.
    /// </summary>
    public interface IPipelineTracingService
    {
        /// <summary>
        /// Start a new pipeline trace for a question
        /// </summary>
        PipelineExecutionTrace StartTrace(string sessionId, string question);
        
        /// <summary>
        /// Create a separate tree for a sub-query (compound queries)
        /// </summary>
        PipelineTree CreateSubQueryTree(string traceId, string subQuery, int order, List<string>? dependencies = null);
        
        /// <summary>
        /// Add a node to a tree
        /// </summary>
        PipelineNode AddNode(string traceId, string treeId, string parentNodeId, string name, PipelineStepType stepType);
        
        /// <summary>
        /// Record step execution start
        /// </summary>
        void StartNode(string traceId, string nodeId);
        
        /// <summary>
        /// Record step execution completion with details
        /// </summary>
        void CompleteNode(string traceId, string nodeId, PipelineStepDetails details);
        
        /// <summary>
        /// Record node failure
        /// </summary>
        void FailNode(string traceId, string nodeId, Exception error, PipelineStepDetails? partialDetails = null);
        
        /// <summary>
        /// Mark a node as a fork point (parallel execution)
        /// </summary>
        void MarkForkPoint(string traceId, string nodeId, List<string> forkedNodeIds);
        
        /// <summary>
        /// Mark a node as a join point (merging results)
        /// </summary>
        void MarkJoinPoint(string traceId, string nodeId, List<string> joinedNodeIds);
        
        /// <summary>
        /// Register a merge point between trees
        /// </summary>
        PipelineMergePoint RegisterMergePoint(string traceId, string name, List<string> sourceTreeIds, MergeStrategy strategy);
        
        /// <summary>
        /// Set final result node
        /// </summary>
        void SetFinalResult(string traceId, PipelineNode finalNode);
        
        /// <summary>
        /// Complete the entire trace
        /// </summary>
        void CompleteTrace(string traceId, PipelineStatus status);
        
        /// <summary>
        /// Get trace by ID
        /// </summary>
        PipelineExecutionTrace? GetTrace(string traceId);
        
        /// <summary>
        /// Get traces for a session
        /// </summary>
        List<PipelineExecutionTrace> GetTracesForSession(string sessionId);
        
        /// <summary>
        /// Get detailed node information
        /// </summary>
        PipelineNode? GetNode(string traceId, string nodeId);
        
        /// <summary>
        /// Helper to capture SQL generation details
        /// </summary>
        PipelineStepDetails CreateSqlGenerationDetails(
            AgenticIntentPlan intent, 
            string generatedSql, 
            string? prunedSchema = null,
            string? modelPrompt = null,
            string? modelResponse = null);
        
        /// <summary>
        /// Helper to capture model call details
        /// </summary>
        PipelineStepDetails CreateModelCallDetails(
            string prompt,
            string response,
            int? promptTokens = null,
            int? completionTokens = null);
    }
    
    public class PipelineTracingService : IPipelineTracingService
    {
        private readonly ILogger<PipelineTracingService> _logger;
        private readonly ConcurrentDictionary<string, PipelineExecutionTrace> _traces = new();
        private readonly ConcurrentDictionary<string, PipelineNode> _nodes = new();
        private readonly ConcurrentDictionary<string, List<string>> _sessionTraces = new();
        
        public PipelineTracingService(ILogger<PipelineTracingService> logger)
        {
            _logger = logger;
        }
        
        public PipelineExecutionTrace StartTrace(string sessionId, string question)
        {
            var trace = new PipelineExecutionTrace
            {
                TraceId = Guid.NewGuid().ToString(),
                SessionId = sessionId,
                OriginalQuestion = question,
                StartedAt = DateTime.UtcNow,
                Status = PipelineStatus.Running
            };
            
            _traces[trace.TraceId] = trace;
            
            // Index by session
            _sessionTraces.AddOrUpdate(sessionId, 
                new List<string> { trace.TraceId },
                (key, list) => { list.Add(trace.TraceId); return list; });
            
            _logger.LogInformation("Started pipeline trace {TraceId} for session {SessionId}", 
                trace.TraceId, sessionId);
            
            return trace;
        }
        
        public PipelineTree CreateSubQueryTree(string traceId, string subQuery, int order, List<string>? dependencies = null)
        {
            var trace = GetTraceRequired(traceId);
            
            var tree = new PipelineTree
            {
                TreeId = Guid.NewGuid().ToString(),
                SubQueryText = subQuery,
                SubQueryOrder = order,
                Dependencies = dependencies ?? new List<string>(),
                Status = TreeStatus.Pending
            };
            
            trace.Trees.Add(tree);
            
            _logger.LogDebug("Created sub-query tree {TreeId} for trace {TraceId}: {SubQuery}", 
                tree.TreeId, traceId, subQuery);
            
            return tree;
        }
        
        public PipelineNode AddNode(string traceId, string treeId, string parentNodeId, string name, PipelineStepType stepType)
        {
            var node = new PipelineNode
            {
                NodeId = Guid.NewGuid().ToString(),
                TreeId = treeId,
                ParentNodeId = parentNodeId,
                Name = name,
                StepType = stepType,
                Status = NodeStatus.Pending,
                StartedAt = DateTime.UtcNow
            };
            
            _nodes[node.NodeId] = node;
            
            // Add to tree structure
            var trace = GetTraceRequired(traceId);
            var tree = trace.Trees.FirstOrDefault(t => t.TreeId == treeId);
            
            if (tree != null)
            {
                if (string.IsNullOrEmpty(parentNodeId))
                {
                    // This is the root node
                    tree.RootNode = node;
                }
                else if (_nodes.TryGetValue(parentNodeId, out var parent))
                {
                    parent.Children.Add(node);
                }
            }
            
            _logger.LogDebug("Added node {NodeId} ({Name}) to tree {TreeId}", 
                node.NodeId, name, treeId);
            
            return node;
        }
        
        public void StartNode(string traceId, string nodeId)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Running;
                node.StartedAt = DateTime.UtcNow;
                
                var tree = GetTreeForNode(traceId, node);
                if (tree != null && tree.Status == TreeStatus.Pending)
                {
                    tree.Status = TreeStatus.Running;
                }
                
                _logger.LogDebug("Started node {NodeId}: {Name}", nodeId, node.Name);
            }
        }
        
        public void CompleteNode(string traceId, string nodeId, PipelineStepDetails details)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Completed;
                node.CompletedAt = DateTime.UtcNow;
                node.Details = details;
                
                _logger.LogDebug("Completed node {NodeId}: {Name} in {Duration}ms", 
                    nodeId, node.Name, node.DurationMs);
            }
        }
        
        public void FailNode(string traceId, string nodeId, Exception error, PipelineStepDetails? partialDetails = null)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Failed;
                node.CompletedAt = DateTime.UtcNow;
                node.Details = partialDetails ?? new PipelineStepDetails();
                node.Details.Errors.Add(new PipelineError
                {
                    Type = error.GetType().Name,
                    Message = error.Message,
                    StackTrace = error.StackTrace
                });
                
                _logger.LogError(error, "Node {NodeId} failed: {Name}", nodeId, node.Name);
            }
        }
        
        public void MarkForkPoint(string traceId, string nodeId, List<string> forkedNodeIds)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.IsForkPoint = true;
                node.ForkedNodeIds = forkedNodeIds;
                
                _logger.LogDebug("Marked node {NodeId} as fork point to {Count} branches", 
                    nodeId, forkedNodeIds.Count);
            }
        }
        
        public void MarkJoinPoint(string traceId, string nodeId, List<string> joinedNodeIds)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.IsJoinPoint = true;
                node.JoinedNodeIds = joinedNodeIds;
                
                _logger.LogDebug("Marked node {NodeId} as join point from {Count} branches", 
                    nodeId, joinedNodeIds.Count);
            }
        }
        
        public PipelineMergePoint RegisterMergePoint(string traceId, string name, List<string> sourceTreeIds, MergeStrategy strategy)
        {
            var trace = GetTraceRequired(traceId);
            
            var mergePoint = new PipelineMergePoint
            {
                MergeId = Guid.NewGuid().ToString(),
                Name = name,
                SourceTreeIds = sourceTreeIds,
                Strategy = strategy
            };
            
            trace.MergePoints.Add(mergePoint);
            
            _logger.LogDebug("Registered merge point {MergeId}: {Name} merging {Count} trees", 
                mergePoint.MergeId, name, sourceTreeIds.Count);
            
            return mergePoint;
        }
        
        public void SetFinalResult(string traceId, PipelineNode finalNode)
        {
            var trace = GetTraceRequired(traceId);
            trace.FinalResult = finalNode;
            
            _logger.LogDebug("Set final result node {NodeId} for trace {TraceId}", 
                finalNode.NodeId, traceId);
        }
        
        public void CompleteTrace(string traceId, PipelineStatus status)
        {
            if (_traces.TryGetValue(traceId, out var trace))
            {
                trace.Status = status;
                trace.CompletedAt = DateTime.UtcNow;
                
                _logger.LogInformation(
                    "Completed pipeline trace {TraceId} with status {Status} in {Duration}ms", 
                    traceId, status, 
                    (trace.CompletedAt.Value - trace.StartedAt).TotalMilliseconds);
            }
        }
        
        public PipelineExecutionTrace? GetTrace(string traceId)
        {
            _traces.TryGetValue(traceId, out var trace);
            return trace;
        }
        
        public List<PipelineExecutionTrace> GetTracesForSession(string sessionId)
        {
            if (_sessionTraces.TryGetValue(sessionId, out var traceIds))
            {
                return traceIds
                    .Select(id => _traces.TryGetValue(id, out var trace) ? trace : null)
                    .Where(t => t != null)
                    .Cast<PipelineExecutionTrace>()
                    .ToList();
            }
            return new List<PipelineExecutionTrace>();
        }
        
        public PipelineNode? GetNode(string traceId, string nodeId)
        {
            _nodes.TryGetValue(nodeId, out var node);
            return node;
        }
        
        public PipelineStepDetails CreateSqlGenerationDetails(
            AgenticIntentPlan intent, 
            string generatedSql, 
            string? prunedSchema = null,
            string? modelPrompt = null,
            string? modelResponse = null)
        {
            return new PipelineStepDetails
            {
                Description = $"Generated SQL for entity '{intent.PrimaryEntity}' with action '{intent.Action}'",
                Input = new PipelineDataSnapshot
                {
                    DataType = "IntentPlan",
                    Content = System.Text.Json.JsonSerializer.Serialize(intent, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                },
                Output = new PipelineDataSnapshot
                {
                    DataType = "SqlQuery",
                    Content = generatedSql,
                    StructuredData = new Dictionary<string, object>
                    {
                        ["entity"] = intent.PrimaryEntity,
                        ["action"] = intent.Action,
                        ["fields"] = intent.Fields,
                        ["filters"] = intent.Filters
                    }
                },
                GeneratedSql = generatedSql,
                ModelPrompt = modelPrompt,
                ModelResponse = modelResponse,
                DebugInfo = new Dictionary<string, object>
                {
                    ["prunedSchema"] = prunedSchema ?? "N/A",
                    ["hasTemporalFilter"] = intent.Temporal != null,
                    ["outputShape"] = intent.OutputShape.ToString()
                }
            };
        }
        
        public PipelineStepDetails CreateModelCallDetails(
            string prompt,
            string response,
            int? promptTokens = null,
            int? completionTokens = null)
        {
            return new PipelineStepDetails
            {
                Description = "AI model inference call",
                ModelPrompt = prompt,
                ModelResponse = response,
                Input = new PipelineDataSnapshot
                {
                    DataType = "ModelPrompt",
                    Content = prompt
                },
                Output = new PipelineDataSnapshot
                {
                    DataType = "ModelResponse",
                    Content = response
                },
                Metrics = new PipelineMetrics
                {
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = (promptTokens ?? 0) + (completionTokens ?? 0)
                }
            };
        }
        
        private PipelineExecutionTrace GetTraceRequired(string traceId)
        {
            if (!_traces.TryGetValue(traceId, out var trace))
            {
                throw new ArgumentException($"Trace {traceId} not found");
            }
            return trace;
        }
        
        private PipelineTree? GetTreeForNode(string traceId, PipelineNode node)
        {
            var trace = GetTrace(traceId);
            return trace?.Trees.FirstOrDefault(t => t.TreeId == node.TreeId);
        }
    }
}
