namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Defensive phase: when the question contains a temporal scope keyword ("today", "this week",
/// "last 30 days", "Q1 of this year", etc.) and the spec has NO filter on the root entity's
/// default date column, inject one. Catches the common silent-failure where the LLM drops the
/// time constraint entirely (e.g. "how many bills issued this week" → WHERE Status = 'Issued'
/// with no date filter, returning every Issued bill ever).
///
/// <para>The injected filter uses planner '@'-tokens (<c>@today</c>, <c>@week_start</c>,
/// <c>@days:-30</c>) which <c>SqlCompiler.TryExpandTemporalToken</c> already expands to inline
/// T-SQL. No new compiler surface required.</para>
///
/// <para>Skip conditions: spec already has a filter on the date column; question lacks any
/// temporal keyword; root entity has no resolved date column.</para>
/// </summary>
internal sealed partial class InjectTemporalFilterFromQuestionPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public InjectTemporalFilterFromQuestionPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "InjectTemporalFilterFromQuestion";
    public string Covers => "Temporal keyword in question (today, this week, last 30 days, Q1, …) without a date filter → inject WHERE";

    // Tier window override: weak-model crutch.
    // Temporal-keyword fallback. The TimeIntent extractor handles this for both tiers; leave on as insurance until Strong is proven.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Medium;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        // Phase 07 — hard short-circuit. When TimeIntent has populated the temporal slot
        // (single range or multi-period), this legacy phase is OBSOLETE for that question.
        // TimeIntent is the single source of truth — no competing injectors.
        if (spec.TimeIntent is { Kind: not SuperAdminCopilot.Models.TimeIntentKind.Unqualified }) return;

        var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        if (string.IsNullOrEmpty(dateCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, dateCol)) return;

        var qualifiedCol = $"{spec.Root}.{dateCol}";

        // Skip if a filter already exists on the resolved date column — don't double-up. We
        // explicitly allow filters on *different* date columns (a question can mention "this week"
        // but the LLM already filtered on ResolvedAt; we'd add a constraint on CreatedAt too).
        //
        // The comparison normalises both sides:
        //   • Planner often emits `IssuedAt` (unqualified) → StripQualifier is a no-op.
        //   • This phase or SpecificYearMonthFilterPhase emits `Bills.IssuedAt` (qualified).
        //   • An adjacent peer phase may even emit `dbo.Bills.IssuedAt` — StripQualifier
        //     takes the last dot-segment.
        // We ALSO trip the early-return if the filter's column ENDS with the bare dateCol —
        // catches casing variants the StripQualifier didn't normalise.
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            var bareF = StripQualifier(f.Column);
            if (string.Equals(bareF, dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
            if (f.Column.EndsWith(dateCol, System.StringComparison.OrdinalIgnoreCase)) return;
        }
        // Also skip if PeriodComparisons already covers the date column — UNION ALL legs each
        // carry their own date filter, so re-injecting would double-up under each leg.
        if (spec.PeriodComparisons.Count > 0)
        {
            foreach (var p in spec.PeriodComparisons)
            {
                foreach (var f in p.Filters)
                {
                    if (string.IsNullOrEmpty(f.Column)) continue;
                    if (StripQualifier(f.Column).Equals(dateCol, System.StringComparison.OrdinalIgnoreCase)
                        || f.Column.EndsWith(dateCol, System.StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }
        }

        // Strip the "-- requested columns: ..." trailing hint the structural-cue parser injects.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Collect ALL matching patterns (not just the first) so multi-period questions like
        // "Q1 and Q2" / "this month vs last month" / "today and yesterday" produce one
        // filter-leg per matched period. Patterns are arranged most-specific-first; we
        // intentionally let multiple match — a "last 30 days" wouldn't collide with "Q1".
        // De-duplicate by token pair (start, end) so the same period mentioned twice in the
        // question text doesn't produce duplicate legs.
        var hits = new List<(string Label, string StartToken, string? EndToken, string Op)>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Year-qualified periods FIRST ("Q1 2027", "February 2019", "in 2025"). When matched,
        // emit @yearmonth:Y:M tokens (year-specific, no GETDATE() drift) and remove the
        // matched substring from `q` so the year-anchored Q1..Q4 / month patterns below don't
        // double-match. This is the fix for the "Q1 2027 vs Feb 2019" example where the old
        // year-anchored tokens silently used YEAR(GETDATE()) and collapsed both periods.
        q = ScanYearQualifiedPeriods(q, hits, seen);

        // Walk every locale's compiled temporal cues. Patterns + tokens + label all come from
        // linguistic-cues.json's `temporal[]` block per locale — NO hardcoded vocab in this
        // file. Operator adds a new dialect = JSON edit only.
        if (_cues?.Compiled?.Locales is not null)
        {
            foreach (var (_, locale) in _cues.Compiled.Locales)
            {
                if (locale?.Temporal is null) continue;
                foreach (var p in locale.Temporal)
                {
                    if (p?.Pattern is null) continue;
                    var m = p.Pattern.Match(q);
                    if (!m.Success) continue;

                    string startToken = p.Start;
                    string? endToken = p.End;
                    if (m.Groups.Count > 1 && m.Groups[1].Success)
                    {
                        var captured = m.Groups[1].Value;
                        startToken = string.Format(System.Globalization.CultureInfo.InvariantCulture, startToken, captured);
                        if (!string.IsNullOrEmpty(endToken) && endToken.Contains("{0}"))
                            endToken = string.Format(System.Globalization.CultureInfo.InvariantCulture, endToken, captured);
                    }

                    var key = startToken + "|" + (endToken ?? "");
                    if (!seen.Add(key)) continue;
                    var label = string.IsNullOrEmpty(p.Label) ? m.Value.Trim() : p.Label;
                    if (m.Groups.Count > 1 && m.Groups[1].Success && label.Contains("{0}"))
                        label = string.Format(System.Globalization.CultureInfo.InvariantCulture, label, m.Groups[1].Value);
                    hits.Add((label, startToken, endToken, p.Op));
                }
            }
        }

        if (hits.Count == 0) return;

        // Single hit → emit as a plain filter on the spec (existing behaviour). A range becomes
        // gte+lt; a single-bound stays op=gte (default Op on TemporalPattern).
        if (hits.Count == 1)
        {
            var (label, startToken, endToken, op) = hits[0];
            if (endToken is null)
            {
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = op, Value = startToken });
                ctx.Diagnostics.Add(new(Name, $"injected {qualifiedCol} {op} '{startToken}' (matched '{label}')"));
            }
            else
            {
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = startToken });
                spec.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt,  Value = endToken });
                ctx.Diagnostics.Add(new(Name, $"injected {qualifiedCol} in ['{startToken}', '{endToken}') (matched '{label}')"));
            }
            return;
        }

        // Multiple hits → use the spec's PeriodComparisons mechanism. The compiler emits one
        // SELECT leg per PeriodSpec, UNION ALL'd, with the Label as a literal projected column —
        // so the result identifies which period each row belongs to ("Q1" / "Q2" / "Last Month").
        // Base spec filters apply to every leg; each leg layers its date range on top.
        // Skip if PeriodComparisons is already populated (LLM or another phase set it up).
        if (spec.PeriodComparisons.Count > 0) return;

        foreach (var (label, startToken, endToken, op) in hits)
        {
            var leg = new PeriodSpec { Label = NormaliseLabel(label) };
            if (endToken is null)
            {
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = op, Value = startToken });
            }
            else
            {
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Gte, Value = startToken });
                leg.Filters.Add(new FilterSpec { Column = qualifiedCol, Op = SpecConst.FilterOps.Lt,  Value = endToken });
            }
            spec.PeriodComparisons.Add(leg);
        }
        ctx.Diagnostics.Add(new(Name, $"detected {hits.Count} temporal periods in question → injected as PeriodComparisons (UNION ALL legs): {string.Join(", ", hits.Select(h => h.Label))}"));
    }

    private static string NormaliseLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        // Trim, take first letter uppercase. "q1" → "Q1", "last month" → "Last month".
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }

    /// <summary>Strip a <c>Table.Column</c> qualifier so we can compare against a bare column
    /// name. <c>"Bills.IssuedAt"</c> → <c>"IssuedAt"</c>; <c>"IssuedAt"</c> → <c>"IssuedAt"</c>.</summary>
    private static string StripQualifier(string column)
    {
        if (string.IsNullOrEmpty(column)) return column;
        var dot = column.LastIndexOf('.');
        return dot >= 0 && dot < column.Length - 1 ? column[(dot + 1)..] : column;
    }

    // Year-qualified period scanner — Q1 2027, January 2025, in 2027, في 2025, etc. —
    // lives in the partial file InjectTemporalFilterFromQuestionPhase.YearScanner.cs.
    // Parametric (captures + arithmetic on year), so it stays in C# rather than JSON.
}
