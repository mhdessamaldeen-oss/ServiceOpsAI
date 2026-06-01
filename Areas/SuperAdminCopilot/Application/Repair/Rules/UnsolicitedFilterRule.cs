namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Exemplar rule #3. Replaces v2's <c>RewriteEmptyToIsNullPhase</c> (all three passes) +
/// <c>StripUnsolicitedStatusOnSuperlativePhase</c> + <c>StripFiltersOnAllTimePhase</c>.
///
/// <para>Detects: filters that contradict the question's intent — three patterns:</para>
/// <list type="number">
///   <item>Absence cue ("without email") + <c>column = ""</c> on a nullable column → rewrite to IS NULL.</item>
///   <item>Absence cue + dangling filter on a table the user never named and that's not
///         referenced anywhere else in the spec → strip it.</item>
///   <item>"All time" cue + status filter on the root → strip the status filter (the user
///         said "ever", not "ever-and-still-Open").</item>
/// </list>
///
/// <para>Each pattern produces a distinct <see cref="Diagnosis"/> sub-payload so
/// <see cref="Apply"/> knows which surgery to perform. The accumulated effect mirrors v2's
/// three passes but with explicit intent rather than ordering-by-DI-registration.</para>
/// </summary>
public sealed class UnsolicitedFilterRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.UnsolicitedFilter;
    public PlannerTier MaxTier => PlannerTier.Medium;
    // Depends only on MissingRoot for now. When MissingLookupFilter ships, add it back.
    // Phantom Requires is a programmer error — RepairBus throws on it at startup.
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var actions = new List<UnsolicitedAction>();

        // Pattern 1 + 2 — absence cue.
        if (ctx.Linguistics.HasCue(ctx.Question, CueKind.Absence))
        {
            // Pattern 1: rewrite eq-empty to is-null where the column is nullable.
            foreach (var f in spec.Filters)
            {
                if ((f.Op ?? "eq").ToLowerInvariant() != "eq") continue;
                if (!IsEmptyValue(f.Value)) continue;
                actions.Add(new UnsolicitedAction(UnsolicitedKind.RewriteEqEmptyToIsNull, f));
            }

            // Pattern 2: strip dangling filters on tables not named in the question and not
            // referenced anywhere else in the spec.
            var explicitTables = ctx.Linguistics.ExtractEntityMentions(ctx.Question)
                                    .Select(m => m.Table)
                                    .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            explicitTables.Add(spec.Root);
            var referenced = CollectReferencedTables(spec);
            foreach (var f in spec.Filters)
            {
                if (string.IsNullOrEmpty(f.Column)) continue;
                if (string.Equals(f.Op, "text_search", System.StringComparison.OrdinalIgnoreCase)) continue;
                var qual = TableQualifier(f.Column);
                if (qual is null) continue;
                if (explicitTables.Contains(qual)) continue;
                if (referenced.Contains(qual)) continue;
                actions.Add(new UnsolicitedAction(UnsolicitedKind.StripDanglingFilter, f));
            }
        }

        // Pattern 3 — all-time cue → strip BOTH status narrowing AND the root's date column.
        // The date-column branch closes the parity gap with v2 StripFiltersOnAllTimePhase.
        if (ctx.Linguistics.HasCue(ctx.Question, CueKind.AllTime))
        {
            var rootDateCol = ctx.Semantic.GetDateColumn(spec.Root);
            foreach (var f in spec.Filters)
            {
                if (string.IsNullOrEmpty(f.Column)) continue;
                var (table, col) = SplitQualified(f.Column);
                if (!string.Equals(table, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (ctx.Semantic.IsTemporalStatusColumn(spec.Root, col))
                    actions.Add(new UnsolicitedAction(UnsolicitedKind.StripStatusOnAllTime, f));
                else if (!string.IsNullOrEmpty(rootDateCol)
                         && string.Equals(col, rootDateCol, System.StringComparison.OrdinalIgnoreCase))
                    actions.Add(new UnsolicitedAction(UnsolicitedKind.StripDateOnAllTime, f));
            }
        }

        // Pattern 4 — superlative aggregate ("largest single bill" / "highest payment") with a
        // single MIN/MAX/AVG/SUM AND a Status filter the user didn't ask for. Replaces v2's
        // StripUnsolicitedStatusOnSuperlativePhase. Skip when the user explicitly named a status
        // (Registry will surface that via ExtractStatus).
        if (spec.Aggregations.Count == 1)
        {
            var fn = (spec.Aggregations[0].Function ?? "").ToUpperInvariant();
            if (fn is "MIN" or "MAX" or "AVG" or "SUM")
            {
                var superlative = ctx.Linguistics.ExtractSuperlative(ctx.Question);
                var hasStatusMention = ctx.Linguistics.ExtractStatus(ctx.Question).Count > 0;
                if (superlative is not null && !hasStatusMention)
                {
                    foreach (var f in spec.Filters)
                    {
                        if (string.IsNullOrEmpty(f.Column)) continue;
                        var (ftable, fcol) = SplitQualified(f.Column);
                        var statusOwner = string.IsNullOrEmpty(ftable) ? spec.Root : ftable;
                        if (ctx.Semantic.IsTemporalStatusColumn(statusOwner, fcol))
                            actions.Add(new UnsolicitedAction(UnsolicitedKind.StripStatusOnSuperlative, f));
                    }
                }
            }
        }

        if (actions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"{actions.Count} unsolicited-filter action(s)",
                          Payload: actions));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<UnsolicitedAction> actions) return spec;
        // Collect indices to remove, process in descending order to preserve index stability.
        var toRemove = new List<int>();
        foreach (var act in actions)
        {
            var idx = spec.Filters.IndexOf(act.Filter);
            if (idx < 0) continue;
            switch (act.Kind)
            {
                case UnsolicitedKind.RewriteEqEmptyToIsNull:
                    spec.Filters[idx].Op = "isnull";
                    spec.Filters[idx].Value = null;
                    break;
                case UnsolicitedKind.StripDanglingFilter:
                case UnsolicitedKind.StripStatusOnAllTime:
                case UnsolicitedKind.StripDateOnAllTime:
                case UnsolicitedKind.StripStatusOnSuperlative:
                    toRemove.Add(idx);
                    break;
            }
        }
        toRemove.Sort();
        for (int i = toRemove.Count - 1; i >= 0; i--)
            spec.Filters.RemoveAt(toRemove[i]);
        return spec;
    }

    // --- helpers ---
    private static bool IsEmptyValue(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0 || string.Equals(s, "null", System.StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string? TableQualifier(string col)
    {
        var dot = col.IndexOf('.');
        return dot <= 0 ? null : col.Substring(0, dot).Trim('[', ']');
    }

    private static (string Table, string Column) SplitQualified(string col)
    {
        if (string.IsNullOrEmpty(col)) return ("", "");
        var dot = col.IndexOf('.');
        return dot <= 0 ? ("", col) : (col.Substring(0, dot).Trim('[', ']'), col.Substring(dot + 1).Trim('[', ']'));
    }

    private static HashSet<string> CollectReferencedTables(QuerySpec spec)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(spec.Root)) set.Add(spec.Root);
        foreach (var s in spec.Select) { var t = TableQualifier(s); if (t is not null) set.Add(t); }
        foreach (var g in spec.GroupBy) { var t = TableQualifier(g); if (t is not null) set.Add(t); }
        foreach (var o in spec.OrderBy) { var t = TableQualifier(o.Column); if (t is not null) set.Add(t); }
        foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j.Table)) set.Add(j.Table);
        foreach (var a in spec.Aggregations) { var t = TableQualifier(a.Column); if (t is not null) set.Add(t); }
        foreach (var c in spec.Computed) { var t = TableQualifier(c.Expression); if (t is not null) set.Add(t); }
        return set;
    }

    private enum UnsolicitedKind
    {
        RewriteEqEmptyToIsNull,
        StripDanglingFilter,
        StripStatusOnAllTime,
        StripDateOnAllTime,
        StripStatusOnSuperlative,
    }
    private sealed record UnsolicitedAction(UnsolicitedKind Kind, FilterSpec Filter);
}
