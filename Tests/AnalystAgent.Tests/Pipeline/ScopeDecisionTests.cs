namespace AnalystAgent.Tests.Pipeline;

using AnalystAgent.Pipeline;
using AnalystAgent.Pipeline.Stages;
using Xunit;

/// <summary>
/// The PRIMARY scope-gate fix: AnalystOrchestrator.DecideScope honors the IntentClassifier's verdict
/// (a high-confidence OUT_OF_SCOPE refuses outright instead of leaking to the weak cosine gate — the
/// "who won the world cup → Syria" hallucination). Tests the pure decision so a future refactor can't
/// silently re-open the bug without standing up the whole orchestrator.
/// </summary>
public class ScopeDecisionTests
{
    private const double Decisive = 0.90;
    private static AnalystOrchestrator.ScopeDecision Decide(ClassifierIntent i, double c) =>
        AnalystOrchestrator.DecideScope(i, c, Decisive);

    [Fact]
    public void DecisiveOutOfScope_refusesOutright() =>
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RefuseOutOfScope, Decide(ClassifierIntent.OutOfScope, 0.96));

    [Fact]
    public void OutOfScope_atExactFloor_refuses() =>   // the >= boundary
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RefuseOutOfScope, Decide(ClassifierIntent.OutOfScope, 0.90));

    [Fact]
    public void OutOfScope_belowFloor_defersToGate() =>
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RunGate, Decide(ClassifierIntent.OutOfScope, 0.50));

    [Fact]
    public void DecisiveSql_skipsGate() =>            // prior behavior preserved
        Assert.Equal(AnalystOrchestrator.ScopeDecision.SkipGate, Decide(ClassifierIntent.Sql, 0.96));

    [Fact]
    public void Sql_atExactFloor_skipsGate() =>
        Assert.Equal(AnalystOrchestrator.ScopeDecision.SkipGate, Decide(ClassifierIntent.Sql, 0.90));

    [Fact]
    public void Sql_belowFloor_runsGate() =>
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RunGate, Decide(ClassifierIntent.Sql, 0.50));

    [Fact]
    public void Chat_runsGate() =>                    // neither decisive-SQL nor decisive-OOS
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RunGate, Decide(ClassifierIntent.Chat, 0.99));

    [Fact]
    public void Unknown_runsGate() =>
        Assert.Equal(AnalystOrchestrator.ScopeDecision.RunGate, Decide(ClassifierIntent.Unknown, 0.99));
}
