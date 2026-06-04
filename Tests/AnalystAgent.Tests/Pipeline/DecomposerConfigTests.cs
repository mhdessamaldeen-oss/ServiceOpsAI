namespace AnalystAgent.Tests.Pipeline;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.Stages;
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
            Options.Create(new AnalystOptions { DecompositionCuesPath = path }),
            NullLogger<HeuristicDecomposer>.Instance);

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "AnalystAgent", "Configuration", file);
    }

    private static IDecomposer ConfigDecomposer() => Decomposer(RepoConfigPath("decomposition-cues.json"));
    private static IDecomposer FallbackDecomposer() => Decomposer("Areas/AnalystAgent/Configuration/__no_such_decomp_file__.json");

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

    // ENH-1 pre-gate: MightBeCompound is true exactly when the question carries a compound/comparison/
    // sequential signal (and is NOT a single grouped-comparison) — so atomic questions skip the LLM call.
    [Theory]
    [InlineData("how many tickets this week and how many last week", true)]   // compound "and how many"
    [InlineData("show me open tickets versus closed tickets", true)]          // versus
    [InlineData("find the customer with most tickets, then show their last 5 tickets", true)] // sequential
    // ENH-1: different-dimension compounds the LLM splits must NOT be marked atomic by the grouped guard.
    [InlineData("top 3 regions by ticket count and the top 3 departments by ticket count", true)]
    [InlineData("the 5 biggest customers by billing and the 5 busiest regions by outages", true)]
    // combineGuard: "...in one list" wants a single UNION result, NOT a split — stays atomic.
    [InlineData("top 3 customers by total billed and top 3 regions by ticket count in one list", false)]
    [InlineData("how many open tickets", false)]                              // atomic
    [InlineData("list all overdue bills for active customers", false)]        // atomic
    [InlineData("tickets by priority", false)]                                // single grouped-by → one query
    [InlineData("count tickets by region and priority", false)]              // ONE group-by (two dims) → one query
    [InlineData("compare tickets this month vs last month", false)]           // grouped comparison → single query
    public void MightBeCompound_gatesOnCompoundSignal(string q, bool expected)
        => Assert.Equal(expected, ConfigDecomposer().MightBeCompound(q));

    // FIX-8: the amputated-continuation guard reuses the config LeadingConjunction cue (EN + AR).
    [Theory]
    [InlineData("and status open", true)]
    [InlineData("or closed", true)]
    [InlineData("ثم اعرض تذاكرهم", true)]                                     // Arabic "then show their tickets"
    [InlineData("how many tickets are open", false)]
    [InlineData("orders placed yesterday", false)]                           // "or" inside "orders" must not match
    public void StartsWithLeadingConjunction_isBilingual(string text, bool expected)
        => Assert.Equal(expected, ConfigDecomposer().StartsWithLeadingConjunction(text));

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
