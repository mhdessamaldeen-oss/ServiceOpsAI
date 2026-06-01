namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>InjectLookupValueFilterFromQuestionPhase</c> + <c>MapValueSynonymsPhase</c>
/// + <c>ConvertFkEqualsNameToJoinPhase</c> + <c>ConvertNameFilterToLikePhase</c>.
///
/// <para>For each status / severity / categorical mention in the question
/// (from <see cref="ILinguisticRegistry.ExtractStatus"/>), inject a filter on the matching
/// column when none exists. Operator extends matching by adding entries to
/// <c>linguistic-cues.json.locales[*].statusValues</c> — zero hardcoded values in this rule.</para>
///
/// <para>This is the foundational shape; the full FK-graph + catalog-value scan is deferred
/// to a follow-up enhancement (see HANDOFF.md "Outstanding items" — needs
/// <c>ISchemaView.GetReachableLookupTables</c> + <c>ICatalogView.GetAllLookupValues</c>).</para>
/// </summary>
public sealed class MissingLookupFilterRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingLookupFilter;
    public PlannerTier MaxTier => PlannerTier.Weak;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var mentions = ctx.Linguistics.ExtractStatus(ctx.Question);
        if (mentions.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var add = new List<FilterSpec>();
        var existingCols = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var f in spec.Filters)
            if (!string.IsNullOrEmpty(f.Column)) existingCols.Add(f.Column);

        foreach (var m in mentions)
        {
            if (!ctx.Schema.ColumnExists(spec.Root, m.Column)) continue;
            var qual = spec.Root + "." + m.Column;
            if (existingCols.Contains(qual)) continue;
            add.Add(new FilterSpec { Column = qual, Op = "eq", Value = m.CanonicalValue });
            existingCols.Add(qual);
        }

        if (add.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"injecting {add.Count} status/lookup filter(s)", Payload: add));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<FilterSpec> add) return spec;
        spec.Filters.AddRange(add);
        return spec;
    }
}
