namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Phase 07 — single owner of temporal-intent injection. Runs FIRST in the SpecRepair
/// pipeline. Consults <see cref="ITimeIntentExtractor"/> to parse the question into a
/// structured <see cref="TimeIntent"/>, then writes that intent into the spec's
/// <c>Filters</c> (single-period) or <c>PeriodComparisons</c> (multi-period) — which the
/// compiler already knows how to render.
///
/// <para>Once this phase has populated the date filter, the legacy temporal phases
/// (<c>InjectTemporalFilterFromQuestionPhase</c>, <c>SpecificYearMonthFilterPhase</c>)
/// detect the existing filter and bow out via their existing early-return. No competing
/// injectors; no duplicate filters.</para>
///
/// <para>When the question contains no temporal phrase, this phase is a no-op
/// (TimeIntent.Kind = Unqualified). The spec is unchanged.</para>
/// </summary>
internal sealed class TimeIntentSpecRepairPhase : ISpecRepairPhase
{
    public string Name => "TimeIntent";
    public string Covers => "Single-owner temporal parsing — fills spec.Filters / PeriodComparisons from TimeIntent before competing temporal phases run";

    private readonly ITimeIntentExtractor _extractor;

    public TimeIntentSpecRepairPhase(ITimeIntentExtractor extractor)
    {
        _extractor = extractor;
    }

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        // Idempotency: don't re-run if TimeIntent already populated (a refinement / retry).
        if (spec.TimeIntent is { Kind: not TimeIntentKind.Unqualified }) return;

        var intent = _extractor.Extract(ctx.Question);
        spec.TimeIntent = intent;
        if (intent.Kind == TimeIntentKind.Unqualified) return;

        // Resolve the date column. Honour the intent's preference first; fall back to the
        // semantic layer's default for the root entity. Skip if neither resolves.
        var dateCol = !string.IsNullOrEmpty(intent.DateColumn)
            ? intent.DateColumn
            : ctx.SemanticLayer.GetDateColumn(spec.Root, intent.DateRoleHint);
        if (string.IsNullOrEmpty(dateCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, dateCol)) return;

        var qualifiedCol = $"{spec.Root}.{dateCol}";

        // If the LLM ALREADY emitted a filter on this date column, leave it alone — we only
        // fill the slot when it's empty. Compares both qualified and bare forms.
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            if (string.Equals(f.Column, qualifiedCol, System.StringComparison.OrdinalIgnoreCase)) return;
            if (f.Column.EndsWith("." + dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(f.Column, dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
        }

        switch (intent.Kind)
        {
            case TimeIntentKind.Absolute:
            case TimeIntentKind.Relative:
                if (intent.Range is not null) ApplySingleRange(spec, qualifiedCol, intent.Range, ctx);
                break;

            case TimeIntentKind.MultiPeriod:
                ApplyMultiPeriod(spec, qualifiedCol, intent.Periods, ctx);
                break;
        }
    }

    /// <summary>Single-period intent → two FilterSpec entries (gte, lt) directly on the spec.</summary>
    private static void ApplySingleRange(QuerySpec spec, string qualifiedCol, TimeRange r, SpecRepairContext ctx)
    {
        if (r.Start.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = r.Start.Value });
        if (r.End.HasValue)
            spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt, Value = r.End.Value });
        ctx.Diagnostics.Add(new(typeof(TimeIntentSpecRepairPhase).Name,
            $"TimeIntent[{r.Label}] → {qualifiedCol} ∈ [{r.Start:yyyy-MM-dd}, {r.End:yyyy-MM-dd})"));
    }

    /// <summary>Multi-period intent → one PeriodSpec leg per range, compiler emits UNION ALL.</summary>
    private static void ApplyMultiPeriod(QuerySpec spec, string qualifiedCol, System.Collections.Generic.List<TimeRange> periods, SpecRepairContext ctx)
    {
        // If PeriodComparisons is already populated (by the LLM or another phase) we don't
        // touch it — the LLM's structured comparison wins.
        if (spec.PeriodComparisons.Count > 0) return;

        foreach (var r in periods)
        {
            var leg = new PeriodSpec { Label = r.Label };
            if (r.Start.HasValue)
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = r.Start.Value });
            if (r.End.HasValue)
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt, Value = r.End.Value });
            spec.PeriodComparisons.Add(leg);
        }
        ctx.Diagnostics.Add(new(typeof(TimeIntentSpecRepairPhase).Name,
            $"TimeIntent[multi-period] → {periods.Count} UNION-ALL legs on {qualifiedCol}: {string.Join(", ", periods.Select(p => p.Label))}"));
    }
}
