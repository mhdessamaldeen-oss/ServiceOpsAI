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

        // ─────────────────────────────────────────────────────────────────────────────
        // AUTHORITATIVE policy (Phase D — admin-approved): TimeIntent is the source of
        // truth for the question's temporal scope. When intent is definite, we REMOVE any
        // pre-existing date filter the LLM produced on the same column and replace it.
        // Solves: LLM emits `CreatedAt = pointvalue` for "Feb 2026" — wrong; LLM emits
        // `>= 2026-01-01` for "in 2025" — wrong year; LLM emits two PeriodComparisons
        // legs for "Q1 2025" — duplicate. TimeIntent overwrites all of those.
        // ─────────────────────────────────────────────────────────────────────────────
        var stripped = StripExistingDateFilters(spec, dateCol);
        if (stripped > 0)
            ctx.Diagnostics.Add(new(typeof(TimeIntentSpecRepairPhase).Name,
                $"TimeIntent OVERRIDE: removed {stripped} pre-existing filter(s) on {dateCol}"));

        switch (intent.Kind)
        {
            case TimeIntentKind.Absolute:
            case TimeIntentKind.Relative:
                // Single-period: ALSO clear any LLM PeriodComparisons. The LLM often emits
                // a ghost leg ("Q1" anchored to current year) alongside the real one — Q1
                // 2025 returned 2 rows ("Q1 2025" + "Q1") instead of 1 because of this.
                // TimeIntent authoritative = clear ALL competing PeriodComparisons.
                if (spec.PeriodComparisons.Count > 0)
                {
                    ctx.Diagnostics.Add(new(typeof(TimeIntentSpecRepairPhase).Name,
                        $"TimeIntent OVERRIDE: cleared {spec.PeriodComparisons.Count} stray PeriodComparisons for single-period intent"));
                    spec.PeriodComparisons.Clear();
                }
                if (intent.Range is not null) ApplySingleRange(spec, qualifiedCol, intent.Range, ctx);
                break;

            case TimeIntentKind.MultiPeriod:
                // For multi-period, also clear the LLM's PeriodComparisons (often have a
                // ghost current-year leg next to the real ones). TimeIntent's periods win.
                if (spec.PeriodComparisons.Count > 0)
                {
                    ctx.Diagnostics.Add(new(typeof(TimeIntentSpecRepairPhase).Name,
                        $"TimeIntent OVERRIDE: cleared {spec.PeriodComparisons.Count} pre-existing PeriodComparisons"));
                    spec.PeriodComparisons.Clear();
                }
                ApplyMultiPeriod(spec, qualifiedCol, intent.Periods, ctx);
                break;
        }
    }

    /// <summary>
    /// Remove any FilterSpec whose column refers to the same date column as the
    /// authoritative TimeIntent. Mutates spec.Filters; returns removed count for trace.
    /// Compares qualified, prefix-qualified, and bare forms.
    /// </summary>
    private static int StripExistingDateFilters(QuerySpec spec, string dateCol)
    {
        int removed = 0;
        for (int i = spec.Filters.Count - 1; i >= 0; i--)
        {
            var f = spec.Filters[i];
            if (string.IsNullOrEmpty(f.Column)) continue;
            bool matches = string.Equals(f.Column, dateCol, System.StringComparison.OrdinalIgnoreCase)
                        || f.Column.EndsWith("." + dateCol, System.StringComparison.OrdinalIgnoreCase);
            if (matches) { spec.Filters.RemoveAt(i); removed++; }
        }
        return removed;
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
