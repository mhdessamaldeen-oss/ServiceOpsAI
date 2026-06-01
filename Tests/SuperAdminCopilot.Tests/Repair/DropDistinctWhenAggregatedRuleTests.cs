namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Models;
using Xunit;

public class DropDistinctWhenAggregatedRuleTests
{
    private readonly DropDistinctWhenAggregatedRule _rule = new();

    [Fact]
    public void Fires_WhenDistinctTrueAndAggregationPresent()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Distinct = true,
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
        };
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        Assert.False(spec.Distinct);
    }

    [Fact]
    public void NoOp_WhenDistinctTrueButNoAggregation()
    {
        var spec = new QuerySpec { Root = "Tickets", Distinct = true };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenAggregationPresentButDistinctFalse()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Distinct = false,
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Tickets.Amount" } },
        };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void Apply_ReturnsSameInstance()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Distinct = true,
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
        };
        var ctx = new RepairRuleHarness().Build();
        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);

        var result = _rule.Apply(spec, dx);

        Assert.Same(spec, result);   // bus relies on identity threading
    }
}
