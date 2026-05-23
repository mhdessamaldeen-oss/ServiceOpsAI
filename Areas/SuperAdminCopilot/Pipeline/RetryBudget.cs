namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Per-question budget for expensive operations (LLM calls and wall-clock time). Every LLM
/// caller (LlmPlanner, LlmExplainer, future SemanticSearchHandler-with-LLM) consults the
/// budget before hitting the model — once the cap is reached, further attempts are refused
/// with <see cref="StageNames.RetryBudgetExhausted"/>.
///
/// <para>Why this matters in production: a runaway question (planner keeps emitting bad SQL,
/// retry loop fires, explainer fires too) can cost 5+ LLM calls and 60+ seconds. The budget
/// is the hard backstop. Defaults:
/// <list type="bullet">
///   <item><see cref="CopilotOptions.MaxLlmCallsPerQuestion"/> = 3</item>
///   <item><see cref="CopilotOptions.MaxQuestionWallClockSeconds"/> = 30</item>
/// </list></para>
///
/// <para>Scope: the budget is request-scoped — every <see cref="ISuperAdminCopilot.AskAsync"/>
/// gets a fresh budget. Internally it's tracked per <see cref="CopilotRequest"/> via the
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> below so we don't have to plumb the budget
/// through every constructor.</para>
/// </summary>
public interface IRetryBudget
{
    /// <summary>
    /// Try to consume one LLM-call worth of budget. Returns true when the call may proceed,
    /// false when the budget is exhausted (caller logs and surfaces a clean refusal).
    /// </summary>
    bool TryConsumeLlmCall(string stage);

    /// <summary>Snapshot for trace records: how much of the budget is left.</summary>
    BudgetSnapshot GetSnapshot();

    /// <summary>
    /// Reset the budget to fresh state (zero LLM calls used, wall-clock stopwatch restarted).
    /// Called by the orchestrator at the top of <see cref="ISuperAdminCopilot.AskAsync"/> so the
    /// budget is truly per-question even when the consuming DI scope is reused — e.g. the eval
    /// runner's <c>GoldenSetRunner</c> goes through one scope but iterates through hundreds of
    /// questions, and without this reset the budget exhausts globally after the first 3 LLM calls.
    /// </summary>
    void Reset();
}

public sealed record BudgetSnapshot(int LlmCallsUsed, int LlmCallsMax, long ElapsedMs, long MaxMs);

/// <summary>
/// Thrown by an LLM caller (LlmPlanner, LlmExplainer) when the retry budget is exhausted.
/// The orchestrator catches this and surfaces it as <see cref="StageNames.RetryBudgetExhausted"/>
/// rather than a generic planner error — so the user gets a clear "we tried twice and gave up"
/// message instead of a confusing JSON / SQL error.
/// </summary>
public sealed class RetryBudgetExhaustedException : Exception
{
    public string Stage { get; }
    public RetryBudgetExhaustedException(string stage)
        : base($"Retry budget exhausted at stage '{stage}'. Increase MaxLlmCallsPerQuestion or simplify the question.")
    {
        Stage = stage;
    }
}

internal sealed class RetryBudget : IRetryBudget
{
    private readonly CopilotOptions _options;
    private readonly ILogger<RetryBudget> _logger;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private int _llmCalls;

    public RetryBudget(IOptions<CopilotOptions> options, ILogger<RetryBudget> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool TryConsumeLlmCall(string stage)
    {
        var max = Math.Max(0, _options.MaxLlmCallsPerQuestion);
        var maxMs = Math.Max(0, _options.MaxQuestionWallClockSeconds) * 1000L;

        if (max > 0 && _llmCalls >= max)
        {
            _logger.LogWarning("[RetryBudget] LLM-call cap hit at stage {Stage} ({Used}/{Max}).", stage, _llmCalls, max);
            return false;
        }
        if (maxMs > 0 && _sw.ElapsedMilliseconds >= maxMs)
        {
            _logger.LogWarning("[RetryBudget] Wall-clock cap hit at stage {Stage} ({Elapsed}ms / {Max}ms).", stage, _sw.ElapsedMilliseconds, maxMs);
            return false;
        }

        _llmCalls++;
        _logger.LogDebug("[RetryBudget] Consumed at stage {Stage} ({Used}/{Max}, {Elapsed}ms).", stage, _llmCalls, max, _sw.ElapsedMilliseconds);
        return true;
    }

    public BudgetSnapshot GetSnapshot() => new(_llmCalls, _options.MaxLlmCallsPerQuestion, _sw.ElapsedMilliseconds, _options.MaxQuestionWallClockSeconds * 1000L);

    public void Reset()
    {
        _llmCalls = 0;
        _sw.Restart();
    }
}
