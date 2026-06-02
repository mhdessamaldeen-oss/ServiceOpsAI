namespace SuperAdminCopilot.Tests.Pipeline;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 externalization of RefinementDetector's cues to refinement-cues.json:
/// (1) the SHIPPED config reproduces the in-code English fallback exactly (behavior-neutral — a
/// missing/empty file path drives the fallback, the real file drives config, and they must agree on
/// English); (2) the new Arabic cues actually fire on a refinement longer than the word-count
/// heuristic, proving multilingual coverage is real and not just the ≤5-word shortcut.
/// </summary>
public class RefinementDetectorConfigTests
{
    private static IRefinementDetector Detector(string path) =>
        new RefinementDetector(
            Options.Create(new CopilotOptions { RefinementCuesPath = path }),
            NullLogger<RefinementDetector>.Instance);

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "SuperAdminCopilot", "Configuration", file);
    }

    private static IRefinementDetector ConfigDetector() => Detector(RepoConfigPath("refinement-cues.json"));
    private static IRefinementDetector FallbackDetector() => Detector("Areas/SuperAdminCopilot/Configuration/__no_such_refinement_file__.json");

    [Theory]
    // Refinements (true via leading connector / anaphora / phrase / short reply)
    [InlineData("actually show me the breakdown by region")]
    [InlineData("but only the overdue ones from last month")]
    [InlineData("sort them by total amount descending please")]
    [InlineData("break that down by status and priority")]
    [InlineData("what about the previous quarter numbers please")]
    [InlineData("the same but grouped by department now")]
    [InlineData("show me")]
    // Fresh queries (false — no cue, > 5 words)
    [InlineData("how many tickets do we have in total")]
    [InlineData("list all overdue bills for active customers")]
    [InlineData("top 5 customers by total billed amount")]
    public void ShippedConfig_ReproducesEnglishFallback(string question)
    {
        Assert.Equal(FallbackDetector().LooksLikeRefinement(question),
                     ConfigDetector().LooksLikeRefinement(question));
    }

    [Fact]
    public void ArabicCue_Fires_BeyondWordCountHeuristic()
    {
        // 7 words (> the ≤5 short-reply threshold), so detection must come from the Arabic phrase cue.
        const string q = "رتبها حسب المنطقة ثم حسب الحالة والأولوية";
        Assert.True(q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5);
        Assert.True(ConfigDetector().LooksLikeRefinement(q));   // Arabic 'رتبها حسب' phrase → refinement
        Assert.False(FallbackDetector().LooksLikeRefinement(q)); // English-only fallback can't see it
    }
}
