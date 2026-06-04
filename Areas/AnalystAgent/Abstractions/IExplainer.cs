namespace AnalystAgent.Abstractions;

using AnalystAgent.Models;

public interface IExplainer
{
    /// <summary>
    /// Render a natural-language answer for the executed query result. Returns both the
    /// final reply text AND the substeps captured during the explainer call (LLM call:
    /// prompt + response + ms; or "templated-fallback" when the LLM was skipped). The
    /// orchestrator nests these under the parent Explainer step in the trace tree so the
    /// investigation UI shows the same fidelity the planner already does (F-Trace).
    /// </summary>
    Task<ExplainerResult> ExplainAsync(string question, ExecutionResult result, CompiledSql compiled, CancellationToken cancellationToken = default);
}

/// <summary>Output of one explainer call. <see cref="Reply"/> is the user-visible string;
/// <see cref="SubSteps"/> is the trace-substep list the orchestrator nests under the parent
/// Explainer step so the investigation tree shows real fidelity for the LLM-call.</summary>
public sealed record ExplainerResult(string Reply, IReadOnlyList<PipelineStep> SubSteps);
