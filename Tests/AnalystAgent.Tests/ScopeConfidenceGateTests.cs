namespace AnalystAgent.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.Stages;
using AnalystAgent.Retrieval;
using AnalystAgent.Schema;
using Xunit;

/// <summary>
/// Integration tests for the ScopeConfidenceGate (B1). Pins the positive scope-definition
/// contract: refuse only when ALL fast paths missed AND both signals are below floor. The
/// two failing OOS cases in suite-7 (pasta recipe, treehouse build) live here as unit-level
/// proof that the gate behaves correctly without needing a full pipeline run.
/// </summary>
public class ScopeConfidenceGateTests
{
    private const double SchemaFloor = 0.25;
    private const double VqFloor = 0.55;

    private static ScopeConfidenceGate BuildGate(
        float vqMaxSimilarity,
        float schemaTopScore,
        bool gateEnabled = true)
    {
        var vqMatcher = new Mock<IVerifiedQueryMatcher>();
        vqMatcher.Setup(m => m.MaxSimilarityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(vqMaxSimilarity);

        var schemaRetriever = new Mock<ISchemaSemanticRetriever>();
        var schemaResult = new SchemaSemanticRetrieval
        {
            Tables = schemaTopScore > 0
                ? new[] { new TableMatch(new InferredTable { Name = "FakeTable", Schema = "dbo" }, schemaTopScore) }
                : Array.Empty<TableMatch>()
        };
        schemaRetriever.Setup(m => m.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(schemaResult);

        var opts = new AnalystOptions
        {
            EnableScopeConfidenceGate = gateEnabled,
            OutOfScopeSchemaFloor = SchemaFloor,
            OutOfScopeVerifiedQueryFloor = VqFloor
        };
        var optsMonitor = Mock.Of<IOptionsMonitor<AnalystOptions>>(m => m.CurrentValue == opts);

        var text = new CopilotTextCatalog { PreflightOutOfScope = "Out of scope." };
        var textMonitor = Mock.Of<IOptionsMonitor<CopilotTextCatalog>>(m => m.CurrentValue == text);

        return new ScopeConfidenceGate(
            vqMatcher.Object,
            schemaRetriever.Object,
            optsMonitor,
            textMonitor,
            NullLogger<ScopeConfidenceGate>.Instance);
    }

    [Fact]
    public async Task Refuses_when_both_signals_below_floor()
    {
        // "what's a good recipe for pasta" — no schema match, no VQ match. Refuse.
        var gate = BuildGate(vqMaxSimilarity: 0.10f, schemaTopScore: 0.15f);
        var result = await gate.CheckAsync("what's a good recipe for pasta");
        Assert.NotNull(result.Refusal);
        Assert.Equal("low-scope-confidence", result.Refusal!.MatchedPattern);
        Assert.NotNull(result.Signals);   // scores captured on refuse too
    }

    [Fact]
    public async Task Refuses_treehouse_question()
    {
        // The other deferred-failing case in suite-7 — unrelated domain entirely.
        var gate = BuildGate(vqMaxSimilarity: 0.08f, schemaTopScore: 0.12f);
        Assert.NotNull((await gate.CheckAsync("how do I build a treehouse")).Refusal);
    }

    [Fact]
    public async Task Passes_when_schema_signal_strong()
    {
        // A real data question — "how many tickets" — cosine-matches the Tickets table strongly
        // even if it didn't appear in the VQ catalog. Should pass.
        var gate = BuildGate(vqMaxSimilarity: 0.20f, schemaTopScore: 0.80f);
        Assert.Null((await gate.CheckAsync("how many tickets do we have")).Refusal);
    }

    [Fact]
    public async Task Passes_when_vq_signal_strong()
    {
        // Question matches a catalog entry but schema retrieval is noisy. Should pass — the VQ
        // signal alone is sufficient evidence the question is in scope.
        var gate = BuildGate(vqMaxSimilarity: 0.75f, schemaTopScore: 0.10f);
        Assert.Null((await gate.CheckAsync("show me open tickets")).Refusal);
    }

    [Fact]
    public async Task Passes_at_exact_schema_floor()
    {
        // Boundary test: exactly at the floor (>=) should be in scope.
        var gate = BuildGate(vqMaxSimilarity: 0.10f, schemaTopScore: 0.25f);
        Assert.Null((await gate.CheckAsync("some borderline question")).Refusal);
    }

    [Fact]
    public async Task Passes_at_exact_vq_floor()
    {
        var gate = BuildGate(vqMaxSimilarity: 0.55f, schemaTopScore: 0.10f);
        Assert.Null((await gate.CheckAsync("some borderline question")).Refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Empty_or_whitespace_question_passes(string? question)
    {
        var gate = BuildGate(vqMaxSimilarity: 0.0f, schemaTopScore: 0.0f);
        Assert.Null((await gate.CheckAsync(question!)).Refusal);
    }

    [Fact]
    public async Task Fails_open_when_both_signals_exactly_zero()
    {
        // Both retrievers return 0f from their catch blocks / empty-vector guards on embedder
        // failure (observed live: Ollama returning NaN for one input). Treat the exact-zero
        // pair as "no signal" and pass — refusing in this case mis-rejected a clear data query.
        var gate = BuildGate(vqMaxSimilarity: 0f, schemaTopScore: 0f);
        Assert.Null((await gate.CheckAsync("list users with their roles and ticket counts")).Refusal);
    }

    [Fact]
    public async Task Returns_null_when_gate_disabled()
    {
        // Even when signals are below floor, a disabled gate must pass everything through.
        // This is the kill switch — operators flip EnableScopeConfidenceGate=false to bypass.
        var gate = BuildGate(vqMaxSimilarity: 0.0f, schemaTopScore: 0.0f, gateEnabled: false);
        Assert.Null((await gate.CheckAsync("what's a good recipe for pasta")).Refusal);
    }
}
