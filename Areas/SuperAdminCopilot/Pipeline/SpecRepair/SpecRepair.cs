namespace SuperAdminCopilot.Pipeline.SpecRepair;

using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Application.Repair;
using SuperAdminCopilot.Infrastructure;

/// <summary>
/// Repair coordinator — runs every typed <see cref="IRepairRule"/> implementation via
/// <see cref="RepairBus"/> on the canonical mutable <see cref="QuerySpec"/>. ONE path,
/// ONE spec, no converter. The 2026-06-01 single-spec collapse removed the v3 immutable
/// detour and the lossy v2↔v3 round-trip.
/// </summary>
/// <summary>One repair-rule firing — used by the trace + the SpecExtractor.</summary>
public sealed record SpecRepairDiagnostic(string PhaseName, string Detail);

public interface ISpecRepair
{
    /// <summary>Apply repair rules to the spec in place. Returns per-rule diagnostics.</summary>
    /// <param name="promptShape">Classified question shape (e.g. "GRP-COUNT", "SUB", "WIN-RANK").
    /// Threaded through to <see cref="RepairContext.PromptShape"/> + the bus log line so
    /// operators can aggregate rule firings by shape — answers "for shape X, which rules
    /// fire most?" and "for shape Y, which rules NEVER fire so should be retired?". Empty
    /// when the caller doesn't know the shape; rule firings are still logged, just without
    /// the shape tag.</param>
    System.Collections.Generic.IReadOnlyList<SpecRepairDiagnostic> Apply(
        QuerySpec spec,
        string question,
        System.Collections.Generic.IReadOnlyList<InferredTable> candidateTables,
        string promptShape = "");
}

internal sealed class SpecRepair : ISpecRepair
{
    private readonly RepairBus _bus;
    private readonly ILinguisticRegistry _registry;
    private readonly SuperAdminCopilot.Application.Repair.Schema.ISchemaView _schemaView;
    private readonly SuperAdminCopilot.Application.Repair.Semantic.ISemanticView _semanticView;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<SuperAdminCopilot.Configuration.CopilotOptions> _copilotOptions;
    private readonly ILogger<SpecRepair> _logger;

    public SpecRepair(
        RepairBus bus,
        ILinguisticRegistry registry,
        SuperAdminCopilot.Application.Repair.Schema.ISchemaView schemaView,
        SuperAdminCopilot.Application.Repair.Semantic.ISemanticView semanticView,
        Microsoft.Extensions.Options.IOptionsMonitor<SuperAdminCopilot.Configuration.CopilotOptions> copilotOptions,
        ILogger<SpecRepair> logger)
    {
        _bus = bus;
        _registry = registry;
        _schemaView = schemaView;
        _semanticView = semanticView;
        _copilotOptions = copilotOptions;
        _logger = logger;
    }

    public IReadOnlyList<SpecRepairDiagnostic> Apply(
        QuerySpec spec, string question, IReadOnlyList<InferredTable> candidateTables, string promptShape = "")
    {
        if (spec is null) return Array.Empty<SpecRepairDiagnostic>();
        // Skip non-data intents — clarification specs must surface to user, not be mutated.
        if (!string.IsNullOrEmpty(spec.Intent)
            && !string.Equals(spec.Intent, "data_query", System.StringComparison.OrdinalIgnoreCase))
            return Array.Empty<SpecRepairDiagnostic>();

        var diagnostics = new List<SpecRepairDiagnostic>();
        try
        {
            var ctx2 = new RepairContext(
                Question: question ?? string.Empty,
                Linguistics: _registry,
                Schema: _schemaView,
                Semantic: _semanticView,
                ActiveTier: MapTier(_copilotOptions.CurrentValue.PlannerCapabilityTier),
                PromptShape: promptShape ?? "");

            // Single-spec architecture — bus mutates `spec` in place. No converter, no
            // round-trip, no TimeIntent loss. Rules return the same `spec` instance.
            var result = _bus.Run(spec, ctx2);

            foreach (var dx in result.Applied)
                diagnostics.Add(new SpecRepairDiagnostic(
                    dx.RuleName,
                    $"{dx.Diagnosis.Detail} ({dx.BeforeHash}→{dx.AfterHash})"));

            if (diagnostics.Count > 0)
            {
                // Tagged [copilot.rule_fired] so the log stream is greppable for rule attribution
                // telemetry. Shape included so operators can aggregate by (shape, rule) and see
                // which rules earn their cost on which question shapes. Empty shape is logged
                // as "?" so the aggregation key is still stable.
                var shapeTag = string.IsNullOrEmpty(promptShape) ? "?" : promptShape;
                _logger.LogInformation("[copilot.rule_fired] shape={Shape} count={Count} rules=[{Rules}]",
                    shapeTag,
                    diagnostics.Count,
                    string.Join(",", diagnostics.Select(d => d.PhaseName)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SpecRepair] RepairBus threw; spec returned unchanged.");
        }
        return diagnostics;
    }

    private static PlannerTier MapTier(SuperAdminCopilot.Configuration.PlannerCapabilityTier v2)
    {
        return v2 switch
        {
            SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak   => PlannerTier.Weak,
            SuperAdminCopilot.Configuration.PlannerCapabilityTier.Medium => PlannerTier.Medium,
            SuperAdminCopilot.Configuration.PlannerCapabilityTier.Strong => PlannerTier.Strong,
            _ => PlannerTier.Weak,
        };
    }
}
