namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Targets the LIST-shape pipeline-refusal pattern observed on 2026-05-30 with
/// "complaints in Damascus this month": the LLM emitted a pure-COUNT spec
/// (<c>aggregations=[COUNT(*)]</c>, empty SELECT, empty GROUP BY) for a question that
/// clearly asks for a LIST of rows. <see cref="SuperAdminCopilot.Pipeline.Stages.SqlIntentGuard"/>
/// correctly detects the shape mismatch and retries the LLM with a "drop the COUNT, project
/// display columns" hint, but a 7B model often re-emits the same COUNT shape; the retry
/// budget is exhausted and the pipeline refuses-hard.
///
/// <para>This rule mirrors the SqlIntentGuard's <c>CheckCountShapeOnNonCountQuestion</c>
/// logic INSIDE SpecRepair, so the LLM mistake is fixed BEFORE the gate runs. Detection is
/// IDENTICAL to the gate's check (pure-COUNT shape + question has no aggregate markers per
/// <see cref="ILinguisticRegistry.LooksLikeAggregateQuery"/>) — same source of truth, can't
/// drift. Apply drops the COUNT aggregation and projects the root entity's display columns
/// + FK labels (same expansion as <see cref="DisplayColumnsMissingRule"/>).</para>
///
/// <para>This rule is INTENTIONALLY conservative — it only fires on the GATE-DEFINED shape
/// (pure COUNT, nothing else, no count-intent words). A question like "how many tickets" /
/// "كم عدد التذاكر" / "count of customers" carries an aggregate marker, the linguistic
/// registry recognises it, and this rule no-ops. The gate then also passes the spec
/// unchanged. Multi-locale safe — vocab lives in <c>linguistic-cues.json</c>, not C#.</para>
/// </summary>
public sealed class SwapPureCountForListWhenQuestionIsListShapeRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.SwapPureCountForListWhenQuestionIsListShape;
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        // Shared predicate with SqlIntentGuard.CheckCountShapeOnNonCountQuestion. Both call the
        // same QuerySpecShapePredicates helper (v2 + v3 overloads colocated) — no drift risk.
        if (!QuerySpecShapePredicates.IsPureCountShape(spec))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        if (string.IsNullOrWhiteSpace(ctx.Question)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (ctx.Linguistics.LooksLikeAggregateQuery(ctx.Question))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Compute the projection now so Apply (which sees only spec+diagnosis, not ctx) has it.
        // Mirrors DisplayColumnsMissingRule: root display columns + FK labels for unambiguous
        // single-FK targets that declare a LabelColumn.
        var display = ctx.Semantic.DisplayColumnsFor(spec.Root);
        if (display.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var selectAdds = new List<string>();
        foreach (var dc in display) selectAdds.Add(spec.Root + "." + dc);

        var fkTargetCounts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var fk in ctx.Schema.ForeignKeysFrom(spec.Root))
        {
            if (string.IsNullOrEmpty(fk.ReferencedTable)) continue;
            fkTargetCounts.TryGetValue(fk.ReferencedTable, out var c);
            fkTargetCounts[fk.ReferencedTable] = c + 1;
        }
        var labelJoins = new List<string>();
        foreach (var fk in ctx.Schema.ForeignKeysFrom(spec.Root))
        {
            if (string.IsNullOrEmpty(fk.ReferencedTable)) continue;
            if (fkTargetCounts[fk.ReferencedTable] > 1) continue;        // ambiguous join key
            var label = ctx.Semantic.LabelColumnFor(fk.ReferencedTable);
            if (string.IsNullOrEmpty(label)) continue;
            selectAdds.Add(fk.ReferencedTable + "." + label);
            labelJoins.Add(fk.ReferencedTable);
        }

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"swap pure COUNT for list projection on '{spec.Root}' ({selectAdds.Count} display+label cols)",
                Payload: new SwapPayload(selectAdds, labelJoins)));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not SwapPayload payload) return spec;

        foreach (var s in payload.Projection) spec.Select.Add(s);

        var existingJoins = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) existingJoins.Add(j.Table);
        foreach (var t in payload.LabelJoinTables)
        {
            if (existingJoins.Contains(t)) continue;
            spec.Joins.Add(new JoinSpec { Table = t, Kind = "left" });
            existingJoins.Add(t);
        }

        spec.Aggregations.Clear();
        return spec;
    }

    private sealed record SwapPayload(
        IReadOnlyList<string> Projection,
        IReadOnlyList<string> LabelJoinTables);
}
