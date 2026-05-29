namespace SuperAdminCopilot.Pipeline.SpecRepair;

using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>One SpecRepair phase. Mutates spec in place. Idempotent. Order set by DI registration.</summary>
public interface ISpecRepairPhase
{
    /// <summary>Short identifier used in diagnostics.</summary>
    string Name { get; }

    /// <summary>One-line description of what bug class this phase covers.</summary>
    string Covers { get; }

    /// <summary>
    /// Lowest planner tier at which this phase is required to fire. Defaults to
    /// <see cref="PlannerCapabilityTier.Weak"/> (i.e. always). Use a higher value when a
    /// phase only makes sense for cloud-class planners.
    /// </summary>
    PlannerCapabilityTier MinTierRequired => PlannerCapabilityTier.Weak;

    /// <summary>
    /// Highest planner tier at which this phase still fires. Defaults to
    /// <see cref="PlannerCapabilityTier.Strong"/> (i.e. always). Weak-model crutches
    /// (Arabic-vocabulary dispatch, English aggregate-verb regex, possessive markers, etc.)
    /// override this to <see cref="PlannerCapabilityTier.Weak"/> or
    /// <see cref="PlannerCapabilityTier.Medium"/> so they auto-skip once the planner is
    /// strong enough to handle the intent natively.
    /// </summary>
    PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Strong;

    /// <summary>Apply repair. Append to ctx.Diagnostics when something changed.</summary>
    void Apply(QuerySpec spec, SpecRepairContext ctx);
}
