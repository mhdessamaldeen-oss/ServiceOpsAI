namespace SuperAdminCopilot.Tests.Pipeline.Stages;

using System;
using System.IO;
using System.Text.Json;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 tier-gating of <see cref="CopilotTextCatalog.SpecExtractorExtraGuidance"/>:
/// the copilot-text.json iteration-loop rules inject only when the active planner tier meets the
/// configured minimum. Two angles: (1) the pure gate decision across the full (active × minimum)
/// truth table; (2) the operator-facing knob in copilot-options.json binds to a valid enum name —
/// so a typo'd/misplaced key can't silently fall back to the C# default unnoticed.
/// </summary>
public class SpecExtractorPromptTierGateTests
{
    [Theory]
    // MinTier = Medium (shipped default): skip ONLY Weak.
    [InlineData(PlannerCapabilityTier.Weak,   PlannerCapabilityTier.Medium, false)]
    [InlineData(PlannerCapabilityTier.Medium, PlannerCapabilityTier.Medium, true)]
    [InlineData(PlannerCapabilityTier.Strong, PlannerCapabilityTier.Medium, true)]
    // MinTier = Weak: include at every tier (operator opt-in to always-inject — preserves pre-gate behavior).
    [InlineData(PlannerCapabilityTier.Weak,   PlannerCapabilityTier.Weak,   true)]
    [InlineData(PlannerCapabilityTier.Medium, PlannerCapabilityTier.Weak,   true)]
    [InlineData(PlannerCapabilityTier.Strong, PlannerCapabilityTier.Weak,   true)]
    // MinTier = Strong: include ONLY at Strong.
    [InlineData(PlannerCapabilityTier.Weak,   PlannerCapabilityTier.Strong, false)]
    [InlineData(PlannerCapabilityTier.Medium, PlannerCapabilityTier.Strong, false)]
    [InlineData(PlannerCapabilityTier.Strong, PlannerCapabilityTier.Strong, true)]
    public void Gate_IncludesIff_ActiveTierMeetsOrExceedsMinimum(
        PlannerCapabilityTier activeTier, PlannerCapabilityTier minTier, bool expected)
        => Assert.Equal(expected, SpecExtractorPromptGates.ShouldIncludeExtraGuidance(activeTier, minTier));

    [Fact]
    public void CopilotOptionsJson_Knob_IsPresentAndBindsToValidEnumName()
    {
        var path = RepoConfigPath("copilot-options.json");
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var value = doc.RootElement
            .GetProperty("SuperAdminCopilot")
            .GetProperty("SpecExtractorExtraGuidanceMinTier")
            .GetString();

        Assert.True(
            Enum.TryParse<PlannerCapabilityTier>(value, ignoreCase: true, out var tier),
            $"copilot-options.json SpecExtractorExtraGuidanceMinTier='{value}' must be a valid PlannerCapabilityTier name.");
        Assert.Equal(PlannerCapabilityTier.Medium, tier); // shipped default — change here if the default moves
    }

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "SuperAdminCopilot", "Configuration", file);
    }
}
