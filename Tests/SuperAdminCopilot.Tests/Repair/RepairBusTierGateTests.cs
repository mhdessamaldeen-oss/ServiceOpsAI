namespace SuperAdminCopilot.Tests.Repair;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Application.Repair;
using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;
using Xunit;

/// <summary>
/// Direct coverage for the tier gate in <see cref="RepairBus.Run"/> (RepairBus.cs:31,
/// <c>if ((int)ctx.ActiveTier &gt; (int)rule.MaxTier) continue;</c>). Until 2026-06-02 this line
/// had ZERO test coverage: every other repair test calls <c>rule.Detect/Apply</c> directly via
/// <see cref="RepairRuleHarness"/> and never goes through the bus, so the gate's "skip stronger-
/// model crutches" semantics were unverified. The grand-review panel disagreed about whether the
/// gate was even correct; these tests settle it against the running code.
///
/// <para>Two angles: (1) a stub rule that always fires, swept across the full MaxTier × ActiveTier
/// truth table — isolates the gate logic from any real rule's Detect quirks; (2) two REAL rules
/// (a Weak-tier NLU crutch and a Strong-tier SQL-law) proving the wiring end-to-end through the
/// bus, including topological ordering and in-place mutation.</para>
/// </summary>
public class RepairBusTierGateTests
{
    // ── (1) Gate truth table, isolated via an always-fire stub ───────────────────────

    /// <summary>A rule whose Detect ALWAYS reports a fault, so the ONLY thing deciding whether it
    /// lands in <see cref="RepairResult.Applied"/> is the tier gate. No <c>Requires</c>, so the bus
    /// can run it alone without a dependency chain. Apply is a no-op — the bus records a fired rule
    /// in <c>Applied</c> regardless of whether the spec hash changed.</summary>
    private sealed class AlwaysFireStubRule : IRepairRule
    {
        private readonly RepairFaultKind _kind;
        public AlwaysFireStubRule(RepairFaultKind kind, PlannerTier maxTier) { _kind = kind; MaxTier = maxTier; }
        public RepairFaultKind FaultClass => _kind;
        public PlannerTier MaxTier { get; }
        public IReadOnlyList<RepairFaultKind> Requires { get; } = System.Array.Empty<RepairFaultKind>();
        public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
            => Result.Ok<Diagnosis, Fault>(new Diagnosis(_kind, "stub always fires"));
        public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis) => spec;
    }

    [Theory]
    // MaxTier = Weak (0): fires ONLY at Weak; skipped at Medium/Strong.
    [InlineData(PlannerTier.Weak,   PlannerTier.Weak,   true)]
    [InlineData(PlannerTier.Weak,   PlannerTier.Medium, false)]
    [InlineData(PlannerTier.Weak,   PlannerTier.Strong, false)]
    // MaxTier = Medium (1): fires at Weak/Medium; skipped at Strong.
    [InlineData(PlannerTier.Medium, PlannerTier.Weak,   true)]
    [InlineData(PlannerTier.Medium, PlannerTier.Medium, true)]
    [InlineData(PlannerTier.Medium, PlannerTier.Strong, false)]
    // MaxTier = Strong (2): a SQL-law that fires at EVERY tier (nothing exceeds Strong).
    [InlineData(PlannerTier.Strong, PlannerTier.Weak,   true)]
    [InlineData(PlannerTier.Strong, PlannerTier.Medium, true)]
    [InlineData(PlannerTier.Strong, PlannerTier.Strong, true)]
    public void TierGate_FiresRule_IffActiveTierDoesNotExceedMaxTier(
        PlannerTier ruleMaxTier, PlannerTier activeTier, bool shouldFire)
    {
        var stub = new AlwaysFireStubRule(RepairFaultKind.AmbiguousLimit, ruleMaxTier);
        var bus = new RepairBus(new IRepairRule[] { stub });
        var spec = new QuerySpec { Root = "Tickets" };
        var ctx = new RepairRuleHarness().WithTier(activeTier).Build();

        var result = bus.Run(spec, ctx);

        Assert.Equal(shouldFire, result.Applied.Any(d => d.RuleName == nameof(AlwaysFireStubRule)));
    }

    // ── (2) Real rules through the bus — crutch sheds, SQL-law persists ───────────────

    [Theory]
    [InlineData(PlannerTier.Weak)]
    [InlineData(PlannerTier.Medium)]
    [InlineData(PlannerTier.Strong)]
    public void RealRules_WeakCrutchFiresOnlyAtWeak_StrongLawFiresEverywhere(PlannerTier tier)
    {
        // NaturalKeyTokenRule  → MaxTier=Weak   (pure NLU crutch; a capable model filters itself).
        // NumericAggregationOnNonNumericRule → MaxTier=Strong (type-safety LAW: AVG over nvarchar
        // throws a hard SQL error, so it must fire at every tier).
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "AVG", Column = "Tickets.OutageNumber" } },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("show ticket TKT-00050")
            .WithTier(tier)
            .WithNaturalKey("Tickets", "TicketNumber", "TKT-00050")
            .WithColumnExists("Tickets", "OutageNumber", true) // exists, and IsNumericColumn defaults false → AVG is the fault
            .Build();

        // The bus needs every transitive Requires registered or TopoSort throws: NaturalKeyToken
        // requires MissingRoot; NumericAgg requires MissingRoot + DanglingColumnReference.
        var bus = new RepairBus(new IRepairRule[]
        {
            new MissingRootRule(),
            new DanglingColumnReferenceRule(),
            new NaturalKeyTokenRule(),
            new NumericAggregationOnNonNumericRule(),
        });

        var applied = bus.Run(spec, ctx).Applied.Select(d => d.RuleName).ToHashSet();

        // SQL-law fires regardless of tier.
        Assert.Contains(nameof(NumericAggregationOnNonNumericRule), applied);

        // Weak crutch fires only at Weak.
        if (tier == PlannerTier.Weak)
            Assert.Contains(nameof(NaturalKeyTokenRule), applied);
        else
            Assert.DoesNotContain(nameof(NaturalKeyTokenRule), applied);
    }
}
