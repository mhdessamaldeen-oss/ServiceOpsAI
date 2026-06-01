namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Replaces v2 <c>InjectAntiJoinFromQuestionPhase</c> + <c>UpgradeInnerJoinToAntiJoinPhase</c>.
///
/// <para>Detects "X without any Y" patterns. Either (a) adds Y as an anti-join when no join
/// exists, or (b) upgrades an existing INNER join on Y to anti-join. Universal — no entity
/// names hardcoded; target resolved via Registry's anti-join mention.</para>
/// </summary>
public sealed class MissingAntiJoinRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.MissingAntiJoin;
    public PlannerTier MaxTier => PlannerTier.Medium;
    public IReadOnlyList<RepairFaultKind> Requires { get; } = new[] { RepairFaultKind.MissingRoot };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var hit = ctx.Linguistics.ExtractAntiJoin(ctx.Question);
        if (hit is null) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Resolve the noun → target entity via the Registry's entity mentions on a single-token query.
        var mentions = ctx.Linguistics.ExtractEntityMentions(hit.Noun);
        var target = mentions.FirstOrDefault()?.Table;
        if (string.IsNullOrEmpty(target) || !ctx.Schema.TableExists(target))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        if (string.Equals(target, spec.Root, System.StringComparison.OrdinalIgnoreCase))
            return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        // Already has an anti-join on this target? Idempotent.
        foreach (var j in spec.Joins)
            if (string.Equals(j.Table, target, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.Kind, "anti", System.StringComparison.OrdinalIgnoreCase))
                return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass, $"anti-join on {target} (trigger '{hit.TriggerPhrase}', noun '{hit.Noun}')",
                          Payload: target));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not string target) return spec;

        // Upgrade an existing INNER join on the same target.
        for (int i = 0; i < spec.Joins.Count; i++)
        {
            if (string.Equals(spec.Joins[i].Table, target, System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(spec.Joins[i].Kind, "anti", System.StringComparison.OrdinalIgnoreCase))
            {
                spec.Joins[i].Kind = "anti";
                return spec;
            }
        }
        // Otherwise add a new anti-join.
        spec.Joins.Add(new JoinSpec { Table = target, Kind = "anti" });
        return spec;
    }
}
