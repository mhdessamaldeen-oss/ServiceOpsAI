namespace SuperAdminCopilot.Pipeline.SpecRepair;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>Single owner of LLM-output mutation. Phases run in DI-registered order.</summary>
public interface ISpecRepair
{
    /// <summary>Apply all phases to spec in place. Returns per-phase diagnostics.</summary>
    System.Collections.Generic.IReadOnlyList<SpecRepairDiagnostic> Apply(
        QuerySpec spec,
        string question,
        System.Collections.Generic.IReadOnlyList<InferredTable> candidateTables);
}

internal sealed class SpecRepair : ISpecRepair
{
    private readonly IReadOnlyList<ISpecRepairPhase> _phases;
    private readonly IOptionsMonitor<SpecRepairOptions> _options;
    private readonly IEntityCatalog _catalog;
    private readonly ISemanticLayer _semanticLayer;
    private readonly ILogger<SpecRepair> _logger;

    public SpecRepair(
        IEnumerable<ISpecRepairPhase> phases,
        IOptionsMonitor<SpecRepairOptions> options,
        IEntityCatalog catalog,
        ISemanticLayer semanticLayer,
        ILogger<SpecRepair> logger)
    {
        _phases = phases.ToList();
        _options = options;
        _catalog = catalog;
        _semanticLayer = semanticLayer;
        _logger = logger;
    }

    public IReadOnlyList<SpecRepairDiagnostic> Apply(
        QuerySpec spec, string question, IReadOnlyList<InferredTable> candidateTables)
    {
        if (spec is null) return Array.Empty<SpecRepairDiagnostic>();
        // Skip non-data intents — clarification specs must surface to user, not be mutated.
        if (!string.IsNullOrEmpty(spec.Intent)
            && !string.Equals(spec.Intent, "data_query", System.StringComparison.OrdinalIgnoreCase))
            return Array.Empty<SpecRepairDiagnostic>();

        var ctx = new SpecRepairContext
        {
            Question = question ?? string.Empty,
            CandidateTables = candidateTables ?? Array.Empty<InferredTable>(),
            Catalog = _catalog,
            SemanticLayer = _semanticLayer,
            Options = _options.CurrentValue,
        };
        foreach (var phase in _phases)
        {
            try { phase.Apply(spec, ctx); }
            catch (Exception ex)
            {
                // A phase failure must not abort the pipeline; later phases still get a chance.
                _logger.LogWarning(ex, "[SpecRepair] phase '{Phase}' threw", phase.Name);
            }
        }
        if (ctx.Diagnostics.Count > 0)
        {
            _logger.LogInformation("[SpecRepair] {Count} mutations: {Phases}",
                ctx.Diagnostics.Count,
                string.Join(", ", ctx.Diagnostics.Select(d => d.PhaseName)));
        }
        return ctx.Diagnostics;
    }
}
