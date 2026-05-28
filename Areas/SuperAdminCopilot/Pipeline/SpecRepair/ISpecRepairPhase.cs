namespace SuperAdminCopilot.Pipeline.SpecRepair;

using SuperAdminCopilot.Models;

/// <summary>One SpecRepair phase. Mutates spec in place. Idempotent. Order set by DI registration.</summary>
public interface ISpecRepairPhase
{
    /// <summary>Short identifier used in diagnostics.</summary>
    string Name { get; }

    /// <summary>One-line description of what bug class this phase covers.</summary>
    string Covers { get; }

    /// <summary>Apply repair. Append to ctx.Diagnostics when something changed.</summary>
    void Apply(QuerySpec spec, SpecRepairContext ctx);
}
