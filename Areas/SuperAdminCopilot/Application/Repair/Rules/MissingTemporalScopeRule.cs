namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>InjectTemporalFilterFromQuestionPhase</c> + <c>SpecificYearMonthFilterPhase</c>
/// + <c>SwapDateColumnByVerbPhase</c>.
///
/// <para>When the question carries a temporal scope and the spec has no date filter, inject one
/// using the root entity's default date column. Multi-period intents (Q1 vs Q2, this month vs
/// last month) emit one PeriodSpec per detected span.</para>
/// </summary>
public sealed class MissingTemporalScopeRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingTemporalScope;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        // v2 QuerySpec.TimeIntent is nullable; treat null as Unqualified (no temporal scope set).
        if (spec.TimeIntent is not null && spec.TimeIntent.Kind != TimeIntentKind.Unqualified)
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);    // Plan stage already populated

        var dateCol = ctx.Semantic.GetDateColumn(spec.Root);
        if (string.IsNullOrEmpty(dateCol)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var qualified = spec.Root + "." + dateCol;
        foreach (var f in spec.Filters)
            if (string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);    // already filtered

        var hits = ctx.Linguistics.ExtractTemporal(ctx.Question);
        if (hits.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting temporal scope on {qualified} ({hits.Count} hit(s))",
                          Payload: new TemporalPayload(qualified, hits)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not TemporalPayload payload) return spec;

        // Single hit → plain filter pair on the spec.
        if (payload.Hits.Count == 1)
        {
            var h = payload.Hits[0];
            if (h.EndToken is null)
                spec.Filters.Add(new FilterSpec { Column = payload.DateColumn, Op = h.Op, Value = h.StartToken });
            else
            {
                spec.Filters.Add(new FilterSpec { Column = payload.DateColumn, Op = "gte", Value = h.StartToken });
                spec.Filters.Add(new FilterSpec { Column = payload.DateColumn, Op = "lt",  Value = h.EndToken });
            }
            return spec;
        }

        // Multiple hits → PeriodComparisons (UNION ALL legs).
        foreach (var h in payload.Hits)
        {
            var legFilters = new List<FilterSpec>();
            if (h.EndToken is null)
                legFilters.Add(new FilterSpec { Column = payload.DateColumn, Op = h.Op, Value = h.StartToken });
            else
            {
                legFilters.Add(new FilterSpec { Column = payload.DateColumn, Op = "gte", Value = h.StartToken });
                legFilters.Add(new FilterSpec { Column = payload.DateColumn, Op = "lt",  Value = h.EndToken });
            }
            spec.PeriodComparisons.Add(new PeriodSpec { Label = h.Label, Filters = legFilters });
        }
        return spec;
    }

    private sealed record TemporalPayload(string DateColumn, IReadOnlyList<TemporalSpan> Hits);
}
