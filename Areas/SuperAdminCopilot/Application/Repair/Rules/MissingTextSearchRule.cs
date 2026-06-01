namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Exemplar rule #2. Replaces v2's <c>InjectTextSearchFilterPhase</c>.
///
/// <para>Detects: question carries a text-search trigger ("containing X" / "about X" /
/// "يحتوي على X") AND the root entity has searchable columns declared AND no existing
/// text_search filter is on the spec. Resolution: add a single
/// <c>FilterSpec { Op = "text_search", Column = spec.Root, Value = noun }</c> — the
/// compiler's <c>BuildTextSearchClause</c> handler will expand it into a cross-column OR LIKE.</para>
///
/// <para>Zero hardcoded English / Arabic in this file — triggers come from the Registry.</para>
/// </summary>
public sealed class MissingTextSearchRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingTextSearch;
    public PlannerTier MaxTier => PlannerTier.Weak;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hit = ctx.Linguistics.ExtractTextSearch(ctx.Question);
        if (hit is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var searchable = ctx.Semantic.SearchableColumnsFor(spec.Root);
        if (searchable.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Dedup: skip if the spec already carries a like / text_search filter on the root.
        if (HasExistingTextFilter(spec)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"text-search noun '{hit.Noun}' on {spec.Root}.[{string.Join(",", searchable)}]",
                Payload: hit.Noun));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not string noun) return spec;
        var filter = new FilterSpec
        {
            Column = spec.Root,
            Op = "text_search",
            Value = noun,
        };
        return spec.AddFilter(filter);
    }

    private static bool HasExistingTextFilter(QuerySpec spec)
    {
        foreach (var f in spec.Filters)
        {
            var op = (f.Op ?? "").ToLowerInvariant();
            bool textish = op is "text_search" or "like" or "notlike";
            if (!textish || string.IsNullOrEmpty(f.Column)) continue;
            if (f.Column.StartsWith(spec.Root + ".", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Column, spec.Root, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Column, "expr", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
