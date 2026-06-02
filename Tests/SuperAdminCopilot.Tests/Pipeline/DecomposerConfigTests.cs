namespace SuperAdminCopilot.Tests.Pipeline;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 externalization of HeuristicDecomposer's split / sequential / no-decompose
/// patterns to decomposition-cues.json: (1) the SHIPPED config reproduces the in-code English
/// fallback EXACTLY across a corpus that exercises every concern (compound split, sequential chain,
/// grouped-comparison guard, by/per guard, plain single question); (2) an Arabic compound, present
/// only in the config, decomposes — proving multilingual coverage is real.
/// </summary>
public class DecomposerConfigTests
{
    private static IDecomposer Decomposer(string path) =>
        new HeuristicDecomposer(
            Options.Create(new CopilotOptions { DecompositionCuesPath = path }),
            NullLogger<HeuristicDecomposer>.Instance);

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "SuperAdminCopilot", "Configuration", file);
    }

    private static IDecomposer ConfigDecomposer() => Decomposer(RepoConfigPath("decomposition-cues.json"));
    private static IDecomposer FallbackDecomposer() => Decomposer("Areas/SuperAdminCopilot/Configuration/__no_such_decomp_file__.json");

    private static string Ser(DecompositionResult? r) =>
        r is null ? "<null>" : $"{r.Dependency}|{r.Joiner}|{string.Join(" || ", r.SubQuestions)}";

    [Theory]
    [InlineData("how many tickets this week and how many last week")]      // compound -> Independent
    [InlineData("show me open tickets versus closed tickets")]            // versus split
    [InlineData("count of customers as well as count of bills")]          // as well as
    [InlineData("compare tickets this month vs last month")]              // period comparison -> no decompose
    [InlineData("find the customer with most tickets, then show their last 5 tickets")] // sequential
    [InlineData("tickets by priority")]                                   // by/per guard -> no decompose
    [InlineData("how many open tickets")]                                 // single -> null
    [InlineData("list all overdue bills for active customers")]           // single -> null
    public void ShippedConfig_ReproducesEnglishFallback(string q)
    {
        Assert.Equal(Ser(FallbackDecomposer().Decompose(q)), Ser(ConfigDecomposer().Decompose(q)));
    }

    [Fact]
    public void ArabicCompound_Decomposes_FromConfigOnly()
    {
        const string q = "كم عدد العملاء مقابل كم عدد التذاكر"; // how many customers versus how many tickets
        var configResult = ConfigDecomposer().Decompose(q);
        Assert.NotNull(configResult);
        Assert.True(configResult!.SubQuestions.Count >= 2);
        Assert.Null(FallbackDecomposer().Decompose(q)); // English-only fallback can't see 'مقابل'
    }
}
