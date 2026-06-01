namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Models;
using Xunit;

public class DropUnsafeComputedExpressionsRuleTests
{
    private readonly DropUnsafeComputedExpressionsRule _rule = new();

    [Fact]
    public void DropsComputedWithRawSubquery_AndScrubsReferences()
    {
        // SUB-shape: LLM put a raw SELECT into a Computed expression. The whole pipeline would
        // refuse-hard at the access-policy gate; this rule drops it for a partial answer.
        var spec = new QuerySpec
        {
            Root = "Regions",
            Computed = { new ComputedSpec { Alias = "AvgCount", Expression = "(SELECT COUNT(*) FROM Tickets)" } },
            Select = { "Regions.NameEn", "AvgCount" },
            GroupBy = { "AvgCount" },
            Having = { new HavingSpec { Function = "COUNT", Column = "AvgCount", Op = "gt", Value = 5 } },
        };
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // The unsafe Computed and every reference to its alias are gone; safe parts survive.
        Assert.DoesNotContain(spec.Computed, c => c.Alias == "AvgCount");
        Assert.DoesNotContain("AvgCount", spec.Select);
        Assert.Contains("Regions.NameEn", spec.Select);   // safe column kept
        Assert.DoesNotContain("AvgCount", spec.GroupBy);
        Assert.DoesNotContain(spec.Having, h => h.Column == "AvgCount");
    }

    [Fact]
    public void NoOp_WhenAllComputedExpressionsAreSafe()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Computed = { new ComputedSpec { Alias = "AgeDays", Expression = "DATEDIFF(day, Tickets.CreatedAt, GETDATE())" } },
            Select = { "Tickets.Title", "AgeDays" },
        };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenNoComputedExpressions()
    {
        var spec = new QuerySpec { Root = "Tickets", Select = { "Tickets.Title" } };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }
}
