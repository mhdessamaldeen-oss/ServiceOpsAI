namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Models;
using Xunit;

public class WarnUnknownFilterOpsRuleTests
{
    private readonly WarnUnknownFilterOpsRule _rule = new();

    [Theory]
    [InlineData("eq")]
    [InlineData("neq")]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("like")]
    [InlineData("in")]
    [InlineData("is_null")]
    [InlineData("text_search")]
    public void NoOp_ForKnownOps(string op)
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Tickets.StatusId", Op = op, Value = "Open" } },
        };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Theory]
    [InlineData("equ")]      // typo of "eq"
    [InlineData("equals")]
    [InlineData("greater")]
    [InlineData("contains")]
    public void RewritesUnknownOpToEq(string badOp)
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Tickets.StatusId", Op = badOp, Value = "Open" } },
        };
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        Assert.Equal("eq", spec.Filters[0].Op);
        Assert.Equal("Open", spec.Filters[0].Value);   // value preserved
        Assert.Equal("Tickets.StatusId", spec.Filters[0].Column);   // column preserved
    }

    [Fact]
    public void EmptyOp_IsHarmless_NoFault()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Tickets.StatusId", Op = "", Value = "Open" } },
        };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void OnlyRewritesBadOps_LeavesGoodOnesAlone()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters =
            {
                new FilterSpec { Column = "Tickets.A", Op = "eq",  Value = "1" },
                new FilterSpec { Column = "Tickets.B", Op = "equ", Value = "2" },   // typo
                new FilterSpec { Column = "Tickets.C", Op = "gte", Value = "3" },
            },
        };
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        Assert.Equal("eq",  spec.Filters[0].Op);
        Assert.Equal("eq",  spec.Filters[1].Op);   // rewritten
        Assert.Equal("gte", spec.Filters[2].Op);   // untouched
    }
}
