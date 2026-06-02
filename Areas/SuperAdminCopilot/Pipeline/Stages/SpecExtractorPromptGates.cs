namespace SuperAdminCopilot.Pipeline.Stages;

using SuperAdminCopilot.Configuration;

/// <summary>
/// Pure prompt-assembly gate decisions for <see cref="SpecExtractor"/>, extracted so the tier
/// thresholds are unit-testable in isolation without constructing SpecExtractor's ~16 dependencies
/// (mirrors the RepairBus tier-gate test pattern).
/// </summary>
internal static class SpecExtractorPromptGates
{
    /// <summary>
    /// Whether the copilot-text.json <c>SpecExtractorExtraGuidance</c> block should be appended to the
    /// planner prompt: the active planner tier must meet or exceed the configured minimum. Ordinal
    /// comparison (Weak=0 &lt; Medium=1 &lt; Strong=2). Fail-open: an unresolved/unknown model resolves to
    /// Weak upstream (<see cref="PlannerTierDeriver.FromModel"/>), which is below the default Medium
    /// minimum, so the guidance is conservatively skipped.
    /// </summary>
    internal static bool ShouldIncludeExtraGuidance(
        PlannerCapabilityTier activeTier,
        PlannerCapabilityTier minTier)
        => activeTier >= minTier;
}
