namespace SuperAdminCopilot.Tests.Repair;

using System.Linq;
using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Application.Repair.Schema;
using SuperAdminCopilot.Models;
using Xunit;

public class ProjectLabelForFkGroupByRuleTests
{
    private readonly ProjectLabelForFkGroupByRule _rule = new();

    [Fact]
    public void SwapsFkIdForLabel_InAggregateWithGroupBy()
    {
        // "count of tickets per service type" → LLM grouped by Tickets.ServiceTypeId (the FK).
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*", Alias = "Count" } },
            GroupBy = { "Tickets.ServiceTypeId" },
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Tickets",
                new ForeignKeyEdge("Tickets", "ServiceTypeId", "ServiceTypes", "Id"))
            .WithLabelColumn("ServiceTypes", "NameEn")
            .WithColumnExists("ServiceTypes", "NameEn", true)
            .Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // GroupBy now uses the label, SELECT projects it, and a LEFT JOIN to ServiceTypes exists.
        Assert.Contains("ServiceTypes.NameEn", spec.GroupBy);
        Assert.DoesNotContain("Tickets.ServiceTypeId", spec.GroupBy);
        Assert.Contains("ServiceTypes.NameEn", spec.Select);
        Assert.Contains(spec.Joins, j => j.Table == "ServiceTypes" && j.Kind == "left");
    }

    [Fact]
    public void NoOp_WhenNoAggregation()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            GroupBy = { "Tickets.ServiceTypeId" },   // group-by but no aggregation
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Tickets", new ForeignKeyEdge("Tickets", "ServiceTypeId", "ServiceTypes", "Id"))
            .WithLabelColumn("ServiceTypes", "NameEn")
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenFkTargetIsAmbiguous()
    {
        // Tickets has TWO FKs to AspNetUsers (CreatedBy / AssignedTo) — can't pick one.
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
            GroupBy = { "Tickets.CreatedByUserId" },
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Tickets",
                new ForeignKeyEdge("Tickets", "CreatedByUserId", "AspNetUsers", "Id"),
                new ForeignKeyEdge("Tickets", "AssignedToUserId", "AspNetUsers", "Id"))
            .WithLabelColumn("AspNetUsers", "UserName")
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenLabelAlreadyProjected()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } },
            GroupBy = { "Tickets.ServiceTypeId" },
            Select = { "ServiceTypes.NameEn" },   // LLM already got it right
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Tickets", new ForeignKeyEdge("Tickets", "ServiceTypeId", "ServiceTypes", "Id"))
            .WithLabelColumn("ServiceTypes", "NameEn")
            .WithColumnExists("ServiceTypes", "NameEn", true)
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }
}
