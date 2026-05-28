namespace SuperAdminCopilot.Pipeline.SpecRepair;

using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>Per-call context for SpecRepair phases. Read-only — phases mutate spec, not context.</summary>
public sealed class SpecRepairContext
{
    public required string Question { get; init; }
    public required System.Collections.Generic.IReadOnlyList<InferredTable> CandidateTables { get; init; }
    public required IEntityCatalog Catalog { get; init; }
    public required ISemanticLayer SemanticLayer { get; init; }
    public required SpecRepairOptions Options { get; init; }
    /// <summary>Phases append here when they mutate the spec; powers observability.</summary>
    public System.Collections.Generic.List<SpecRepairDiagnostic> Diagnostics { get; } = new();
}

public sealed record SpecRepairDiagnostic(string PhaseName, string Detail);
