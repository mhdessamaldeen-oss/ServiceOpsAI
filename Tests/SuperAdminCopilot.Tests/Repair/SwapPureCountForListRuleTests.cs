namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Application.Repair.Schema;
using SuperAdminCopilot.Models;
using Xunit;

public class SwapPureCountForListRuleTests
{
    private readonly SwapPureCountForListWhenQuestionIsListShapeRule _rule = new();

    [Fact]
    public void SwapsPureCountForList_WhenQuestionIsListShape()
    {
        // "complaints in Damascus" → LLM emitted pure COUNT, but it's a LIST question.
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("complaints in Damascus")
            .WithAggregateMarker(false)   // no "how many" / "count of" marker
            .WithDisplayColumns("Tickets", "Title", "Status")
            .Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // COUNT dropped; display columns projected.
        Assert.Empty(spec.Aggregations);
        Assert.Contains("Tickets.Title", spec.Select);
        Assert.Contains("Tickets.Status", spec.Select);
    }

    [Fact]
    public void NoOp_WhenQuestionHasAggregateMarker()
    {
        // "how many tickets" carries a count marker → COUNT shape is legitimate.
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("how many tickets")
            .WithAggregateMarker(true)
            .WithDisplayColumns("Tickets", "Title")
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenSpecIsNotPureCount()
    {
        // Has a SELECT column → not the pure-COUNT shape.
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
            Select = { "Tickets.Title" },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("complaints in Damascus")
            .WithAggregateMarker(false)
            .WithDisplayColumns("Tickets", "Title")
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenRootHasNoDisplayColumns()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
        };
        var ctx = new RepairRuleHarness()
            .WithQuestion("complaints in Damascus")
            .WithAggregateMarker(false)
            // No display columns configured for the root.
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }
}
