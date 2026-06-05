namespace AnalystAgent.Tests.Stages;

using AnalystAgent.Pipeline.Stages;
using Xunit;

/// <summary>
/// FIX B — TOOL-vs-DATA lexical override. A tiny lookup table ("Currencies") has a weak schema EMBEDDING
/// cosine, so a domain-overlapping tool can win the floor+margin even though the question literally NAMES the
/// schema entity ("list the currency codes"). When the question lexically/anchor-matches a schema entity AND
/// the top tool's OWN cosine is below the override threshold, the question is treated as DATA (tool
/// suppressed). A genuinely strong tool match still dispatches; with the threshold at 0.0 the override is OFF
/// and the result is byte-identical to the pre-fix floor+margin logic.
///
/// <para>Pure-function tests on <see cref="ToolHandler.ShouldDispatchTool"/> — no schema/tool vocabulary,
/// portable.</para>
/// </summary>
public class ToolHandlerShouldDispatchTests
{
    private const double Floor = 0.55;   // ToolSelectMinCosine
    private const double Margin = 0.05;  // ToolVsSchemaMargin

    // (1) Override OFF (threshold 0.0) → behaves EXACTLY as before regardless of hasSchemaLexicalMatch.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Threshold_zero_dispatches_when_floor_and_margin_met_regardless_of_lexical(bool lexical)
    {
        // toolTop above floor and beats schemaTop by the margin → dispatch. The lexical flag must be ignored
        // because the override is disabled (threshold 0.0).
        Assert.True(ToolHandler.ShouldDispatchTool(
            toolTop: 0.80, schemaTop: 0.30, minCosine: Floor, margin: Margin,
            hasSchemaLexicalMatch: lexical, lexicalOverrideToolThreshold: 0.0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Threshold_zero_data_when_floor_or_margin_not_met_regardless_of_lexical(bool lexical)
    {
        // Tool below floor → DATA, with override disabled, regardless of the lexical flag.
        Assert.False(ToolHandler.ShouldDispatchTool(
            toolTop: 0.50, schemaTop: 0.10, minCosine: Floor, margin: Margin,
            hasSchemaLexicalMatch: lexical, lexicalOverrideToolThreshold: 0.0));
    }

    // (2) Override ON, lexical match, WEAK tool → suppressed (DATA).

    [Fact]
    public void Override_on_lexical_match_weak_tool_is_suppressed()
    {
        // toolTop 0.60 clears floor+margin (would dispatch pre-fix) but is below the 0.66 override threshold
        // AND the question names a schema entity → treat as DATA → false.
        Assert.False(ToolHandler.ShouldDispatchTool(
            toolTop: 0.60, schemaTop: 0.30, minCosine: Floor, margin: Margin,
            hasSchemaLexicalMatch: true, lexicalOverrideToolThreshold: 0.66));
    }

    // (3) Override ON, lexical match, STRONG tool → still dispatches.

    [Fact]
    public void Override_on_lexical_match_strong_tool_still_dispatches()
    {
        // toolTop 0.80 is at/above the override threshold → a real tool question that merely shares a word
        // with a schema entity is NOT starved → dispatch.
        Assert.True(ToolHandler.ShouldDispatchTool(
            toolTop: 0.80, schemaTop: 0.30, minCosine: Floor, margin: Margin,
            hasSchemaLexicalMatch: true, lexicalOverrideToolThreshold: 0.66));
    }

    // (4) Override ON, NO lexical match → override inert; dispatches on floor+margin alone.

    [Fact]
    public void Override_on_no_lexical_match_dispatches()
    {
        // No schema entity named → the override can't fire → weak-but-qualifying tool dispatches as before.
        Assert.True(ToolHandler.ShouldDispatchTool(
            toolTop: 0.60, schemaTop: 0.30, minCosine: Floor, margin: Margin,
            hasSchemaLexicalMatch: false, lexicalOverrideToolThreshold: 0.66));
    }
}
