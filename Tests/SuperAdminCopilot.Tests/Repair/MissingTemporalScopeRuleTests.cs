namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair;
using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Models;
using Xunit;

public class MissingTemporalScopeRuleTests
{
    private readonly MissingTemporalScopeRule _rule = new();

    /// <summary>
    /// REGRESSION test for the 2026-06-01 null-guard fix: v2 QuerySpec.TimeIntent is nullable.
    /// The rule dereferenced spec.TimeIntent.Kind directly, which NREs when TimeIntent is null
    /// (the common case — the extractor only fills it when a temporal phrase is detected).
    /// With the guard, a null TimeIntent is treated as Unqualified and the rule proceeds safely.
    /// </summary>
    [Fact]
    public void DoesNotThrow_WhenTimeIntentIsNull()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            TimeIntent = null,   // the NRE trigger before the fix
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("tickets created this month")
            .WithDateColumn("Tickets", "CreatedAt")
            .WithTemporal(new TemporalSpan("this month", "@month_start", "@months:1", "range"))
            .Build();

        // Must not throw — and should detect the missing scope (no existing date filter).
        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // A date filter on the root's date column was injected.
        Assert.Contains(spec.Filters, f => f.Column == "Tickets.CreatedAt");
    }

    [Fact]
    public void NoOp_WhenTimeIntentAlreadyPopulated()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            TimeIntent = new TimeIntent { Kind = TimeIntentKind.Absolute },   // plan stage filled it
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("tickets this month")
            .WithDateColumn("Tickets", "CreatedAt")
            .WithTemporal(new TemporalSpan("this month", "@month_start", "@months:1", "range"))
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenQuestionHasNoTemporalPhrase()
    {
        var spec = new QuerySpec { Root = "Tickets", TimeIntent = null };
        var ctx = new RepairRuleHarness()
            .WithQuestion("show all tickets")
            .WithDateColumn("Tickets", "CreatedAt")
            // No temporal spans extracted.
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenDateColumnAlreadyFiltered()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            TimeIntent = null,
            Filters = { new FilterSpec { Column = "Tickets.CreatedAt", Op = "gte", Value = "@month_start" } },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("tickets this month")
            .WithDateColumn("Tickets", "CreatedAt")
            .WithTemporal(new TemporalSpan("this month", "@month_start", "@months:1", "range"))
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }
}
