namespace AnalystAgent.Tests.Pipeline.Stages;

using System;
using System.Text;
using AnalystAgent.Pipeline.Stages;
using Xunit;

/// <summary>
/// Pins the grounding block the direct-SQL generator injects — the single load-bearing step proven
/// live (2026-06-02) to turn a silently-wrong join (Tickets.DepartmentId) into the correct one
/// (Tickets.RegionId) on qwen2.5-coder:7b. The block must render the resolved facts VERBATIM, and
/// must be a no-op when there are no hints so the bare path is byte-identical to before.
/// </summary>
public sealed class DirectSqlGroundingPromptTests
{
    [Fact]
    public void Hints_RenderResolvedContextHeaderAndEachHintVerbatim()
    {
        var sb = new StringBuilder();
        LlmDirectSqlEmitter.AppendGroundingBlock(sb, new[]
        {
            "'Malki' is a Region -> JOIN Regions ON Tickets.RegionId = Regions.Id; filter Regions.NameEn = 'Malki'",
            "Bills.TotalAmount is the bill amount; Bills.CustomerId = Customers.Id",
        });
        var s = sb.ToString();

        Assert.Contains("Resolved context (use these EXACT tables/columns/values; do NOT invent names):", s);
        Assert.Contains("- 'Malki' is a Region -> JOIN Regions ON Tickets.RegionId = Regions.Id; filter Regions.NameEn = 'Malki'", s);
        Assert.Contains("- Bills.TotalAmount is the bill amount; Bills.CustomerId = Customers.Id", s);
    }

    [Fact]
    public void NoHints_RendersNothing_BarePathUnchanged()
    {
        var empty = new StringBuilder();
        LlmDirectSqlEmitter.AppendGroundingBlock(empty, Array.Empty<string>());
        Assert.Equal(string.Empty, empty.ToString());

        var nul = new StringBuilder();
        LlmDirectSqlEmitter.AppendGroundingBlock(nul, null);
        Assert.Equal(string.Empty, nul.ToString());
    }

    [Fact]
    public void BlankHints_AreSkipped_NoEmptyBullets()
    {
        var sb = new StringBuilder();
        LlmDirectSqlEmitter.AppendGroundingBlock(sb, new[] { "   ", "real hint" });
        var s = sb.ToString();

        Assert.Contains("- real hint", s);
        // exactly one bullet (the blank hint produced none)
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(s, @"^\s*- ", System.Text.RegularExpressions.RegexOptions.Multiline));
    }
}
