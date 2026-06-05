namespace AnalystAgent.Tests.Semantic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AnalystAgent.Abstractions;
using AnalystAgent.Pipeline.Stages;
using AnalystAgent.Semantic;
using AnalystAgent.Tools;
using Xunit;

/// <summary>
/// Covers the embedding-driven external-tool selector that replaces lexical-keyword tool routing
/// with name+description cosine. Three concerns:
///   (1) BuildToolLabel composes title + description + keywords, skipping empties cleanly;
///   (2) the pure Stage-1 ShouldDispatchTool margin logic (tool-vs-data gate) — a data question
///       (high schemaTop) can never be eaten by a tool, a tool question (high toolTop) routes;
///   (3) RankAsync with a FAKE embedder ranks the closest-by-cosine tool first, and fails open
///       (empty ranking) when the embedder returns an empty vector.
/// No hardcoded tool/domain vocabulary anywhere — every signal comes from the supplied tool rows.
/// </summary>
public class ToolSemanticSelectorTests
{
    // ── BuildToolLabel ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildToolLabel_Composes_Title_Description_Keywords()
    {
        var t = new ToolDefinition(
            ToolKey: "weather", Title: "Weather Lookup",
            Description: "Current weather for a city", EndpointUrl: null,
            KeywordHints: "weather, temperature, forecast", TestPrompt: null);

        Assert.Equal("Weather Lookup — Current weather for a city — weather, temperature, forecast",
            ToolSemanticSelector.BuildToolLabel(t));
    }

    [Fact]
    public void BuildToolLabel_Skips_Empty_Parts()
    {
        // No description, no keywords → just the title (no dangling separators).
        var titleOnly = new ToolDefinition("fx", "FX Rates", Description: null, EndpointUrl: null, KeywordHints: "  ", TestPrompt: null);
        Assert.Equal("FX Rates", ToolSemanticSelector.BuildToolLabel(titleOnly));

        // Title + keywords, no description → two parts only.
        var noDesc = new ToolDefinition("fx", "FX Rates", Description: "", EndpointUrl: null, KeywordHints: "currency, exchange", TestPrompt: null);
        Assert.Equal("FX Rates — currency, exchange", ToolSemanticSelector.BuildToolLabel(noDesc));
    }

    // ── Stage-1 decision (pure margin logic) ─────────────────────────────────────

    [Fact]
    public void ShouldDispatchTool_HighTool_LowSchema_IsTool()
    {
        // toolTop well above floor and well above schemaTop+margin → dispatch.
        // Lexical override OFF (threshold 0.0) → original floor+margin logic.
        Assert.True(ToolHandler.ShouldDispatchTool(toolTop: 0.80, schemaTop: 0.30, minCosine: 0.55, margin: 0.05,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.0));
    }

    [Fact]
    public void ShouldDispatchTool_HighSchema_LowTool_IsData()
    {
        // A data question: schema scores high, tool low → never eaten by a tool.
        Assert.False(ToolHandler.ShouldDispatchTool(toolTop: 0.40, schemaTop: 0.85, minCosine: 0.55, margin: 0.05,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.0));
    }

    [Fact]
    public void ShouldDispatchTool_ToolBelowFloor_IsData()
    {
        // Tool wins the margin but is below the absolute floor → still not a tool.
        Assert.False(ToolHandler.ShouldDispatchTool(toolTop: 0.50, schemaTop: 0.10, minCosine: 0.55, margin: 0.05,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.0));
    }

    [Fact]
    public void ShouldDispatchTool_WithinMargin_IsData()
    {
        // Tool clears the floor but does NOT beat schema by the margin → ambiguous → data path.
        Assert.False(ToolHandler.ShouldDispatchTool(toolTop: 0.60, schemaTop: 0.58, minCosine: 0.55, margin: 0.05,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.0));
    }

    [Fact]
    public void ShouldDispatchTool_ExactlyAtThresholds_IsTool()
    {
        // Boundary: toolTop == floor AND (toolTop - schemaTop) == margin → inclusive → dispatch.
        Assert.True(ToolHandler.ShouldDispatchTool(toolTop: 0.55, schemaTop: 0.50, minCosine: 0.55, margin: 0.05,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.0));
    }

    // ── RankAsync (fake embedder) ────────────────────────────────────────────────

    [Fact]
    public async Task RankAsync_RanksClosestByCosine_First()
    {
        // Three tools; the question vector is identical to the SECOND tool's label vector, so that
        // tool must rank first regardless of input order.
        var weather = Tool("weather");
        var fx = Tool("fx");
        var country = Tool("country");
        var tools = new[] { weather, fx, country };

        var qVec = new[] { 0f, 1f, 0f };
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync("which currency does japan use", It.IsAny<CancellationToken>()))
                .ReturnsAsync(qVec);
        embedder.Setup(e => e.EmbedAsync(ToolSemanticSelector.BuildToolLabel(weather), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { 1f, 0f, 0f });   // orthogonal → cosine 0
        embedder.Setup(e => e.EmbedAsync(ToolSemanticSelector.BuildToolLabel(fx), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { 0f, 1f, 0f });    // identical → cosine 1
        embedder.Setup(e => e.EmbedAsync(ToolSemanticSelector.BuildToolLabel(country), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { 0.7f, 0.7f, 0f }); // 45° → cosine ~0.707

        var selector = NewSelector(embedder.Object);

        var ranked = await selector.RankAsync("which currency does japan use", tools);

        Assert.Equal(3, ranked.Count);
        Assert.Equal("fx", ranked[0].Tool.ToolKey);          // closest
        Assert.True(ranked[0].Score > ranked[1].Score);      // strictly ordered
        Assert.Equal("country", ranked[1].Tool.ToolKey);     // middle
        Assert.Equal("weather", ranked[2].Tool.ToolKey);     // orthogonal, last
    }

    [Fact]
    public async Task RankAsync_FailsOpen_WhenEmbedderReturnsEmpty()
    {
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<float>());   // embedder down → empty vector

        var selector = NewSelector(embedder.Object);

        var ranked = await selector.RankAsync("anything at all", new[] { Tool("weather"), Tool("fx") });

        Assert.Empty(ranked);   // caller falls open to the lexical path
    }

    [Fact]
    public async Task RankAsync_ReturnsEmpty_WhenNoTools()
    {
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");

        var selector = NewSelector(embedder.Object);

        Assert.Empty(await selector.RankAsync("anything", Array.Empty<ToolDefinition>()));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static ToolDefinition Tool(string key) =>
        new(ToolKey: key, Title: $"{key} tool", Description: $"{key} description",
            EndpointUrl: $"https://example/{key}", KeywordHints: $"{key}, hint", TestPrompt: null);

    private static IToolSemanticSelector NewSelector(ITextEmbedder embedder)
    {
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.IsAvailable).Returns(true);
        return new ToolSemanticSelector(
            registry.Object,
            embedder,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ToolSemanticSelector>.Instance);
    }
}
