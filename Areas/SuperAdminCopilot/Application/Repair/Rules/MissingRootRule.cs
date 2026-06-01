namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Exemplar rule #1. Replaces v2's <c>InferRootFromQuestionPhase</c> + <c>InferRootFromColumnRefsPhase</c>
/// + <c>ArabicQuestionDispatchPhase</c>'s root-setting branch.
///
/// <para>Detects: <see cref="QuerySpec.Root"/> is empty. Resolution: walk every entity in the
/// semantic layer; pick the one whose name or synonym matches the longest substring of the
/// question. Universal — no hardcoded entity names. NO regex; everything comes from
/// <see cref="ILinguisticRegistry"/>.</para>
///
/// <para>If no entity matches, fall back to inferring from the first qualified column reference
/// already present in the spec (Filters/Select/Aggregations).</para>
/// </summary>
public sealed class MissingRootRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingRoot;
    public PlannerTier MaxTier => PlannerTier.Strong;       // even strong planners forget the root sometimes
    public IReadOnlyList<RepairFaultKind> Requires { get; } = System.Array.Empty<RepairFaultKind>();

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (!string.IsNullOrEmpty(spec.Root) && ctx.Schema.TableExists(spec.Root))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (string.IsNullOrWhiteSpace(ctx.Question))
            return Result.Fail<Diagnosis, Fault>(new MissingInputFault("question"));

        // Phase A — entity-mention scoring via the Registry.
        var mentions = ctx.Linguistics.ExtractEntityMentions(ctx.Question);
        if (mentions.Count > 0)
        {
            return Result.Ok<Diagnosis, Fault>(
                new Diagnosis(FaultClass, $"matched entity '{mentions[0].Table}' via '{mentions[0].MatchedSynonym}'",
                              Payload: mentions[0].Table));
        }

        // Phase B — fallback to a qualified column reference already in the spec.
        var fromColumns = FirstQualifiedTable(spec);
        if (fromColumns is not null && ctx.Schema.TableExists(fromColumns))
        {
            return Result.Ok<Diagnosis, Fault>(
                new Diagnosis(FaultClass, $"inferred root '{fromColumns}' from a qualified column reference",
                              Payload: fromColumns));
        }

        // No diagnosable fix — let validation catch the missing root downstream.
        return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not string root) return spec;
        return spec.WithRoot(root);
    }

    private static string? FirstQualifiedTable(QuerySpec spec)
    {
        foreach (var s in spec.Select.Concat(spec.GroupBy))
        {
            var t = TableQualifier(s);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        foreach (var f in spec.Filters)
        {
            var t = TableQualifier(f.Column);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        foreach (var a in spec.Aggregations)
        {
            var t = TableQualifier(a.Column);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return null;
    }

    private static string? TableQualifier(string? colRef)
    {
        if (string.IsNullOrWhiteSpace(colRef)) return null;
        var clean = colRef.Replace("[", "").Replace("]", "");
        var dot = clean.IndexOf('.');
        return dot <= 0 ? null : clean.Substring(0, dot);
    }
}
