namespace AnalystAgent.Tests.Configuration;

using AnalystAgent.Configuration;
using Xunit;

/// <summary>
/// Locks the model→tier derivation that drives weak-model-crutch gating. The single most
/// important case is the "gpt-4o-mini" trap: a Medium model whose name contains the Strong
/// substring "gpt-4o" must resolve to Medium, not Strong — otherwise Medium safety nets are
/// wrongly disabled on a mid-tier model.
/// </summary>
public class PlannerTierDeriverTests
{
    [Theory]
    // ── Strong (frontier) ──────────────────────────────────────────────
    [InlineData("claude-opus-4-8", PlannerCapabilityTier.Strong)]
    [InlineData("claude-sonnet-4-6", PlannerCapabilityTier.Strong)]
    [InlineData("gpt-4o", PlannerCapabilityTier.Strong)]
    [InlineData("gpt-4o-2024-11-20", PlannerCapabilityTier.Strong)]
    [InlineData("gemini-2.5-pro", PlannerCapabilityTier.Strong)]
    [InlineData("deepseek-v3", PlannerCapabilityTier.Strong)]
    public void Strong_models(string model, PlannerCapabilityTier expected)
        => Assert.Equal(expected, PlannerTierDeriver.FromModel(model));

    [Theory]
    // ── Medium (fast cloud + 14-32B local) ─────────────────────────────
    [InlineData("gemini-2.5-flash", PlannerCapabilityTier.Medium)]
    [InlineData("gemini-2.5-flash-lite", PlannerCapabilityTier.Medium)]
    [InlineData("gpt-4o-mini", PlannerCapabilityTier.Medium)]          // THE TRAP — contains "gpt-4o"
    [InlineData("claude-3-5-haiku", PlannerCapabilityTier.Medium)]
    [InlineData("qwen2.5-coder:32b", PlannerCapabilityTier.Medium)]
    [InlineData("qwen2.5-coder:14b", PlannerCapabilityTier.Medium)]
    [InlineData("deepseek-coder-v2", PlannerCapabilityTier.Medium)]
    public void Medium_models(string model, PlannerCapabilityTier expected)
        => Assert.Equal(expected, PlannerTierDeriver.FromModel(model));

    [Theory]
    // ── Weak (local 7B + unknown + empty) ──────────────────────────────
    [InlineData("qwen2.5-coder:7b", PlannerCapabilityTier.Weak)]
    [InlineData("llama3.1:8b", PlannerCapabilityTier.Weak)]
    [InlineData("some-unknown-model", PlannerCapabilityTier.Weak)]
    [InlineData("", PlannerCapabilityTier.Weak)]
    [InlineData(null, PlannerCapabilityTier.Weak)]
    public void Weak_or_unknown_models(string? model, PlannerCapabilityTier expected)
        => Assert.Equal(expected, PlannerTierDeriver.FromModel(model));

    [Fact]
    public void TheTrap_gpt4oMini_isMediumNotStrong()
    {
        // Explicit, named regression guard for the substring-order trap.
        Assert.Equal(PlannerCapabilityTier.Medium, PlannerTierDeriver.FromModel("gpt-4o-mini"));
        Assert.Equal(PlannerCapabilityTier.Strong, PlannerTierDeriver.FromModel("gpt-4o"));
    }
}
