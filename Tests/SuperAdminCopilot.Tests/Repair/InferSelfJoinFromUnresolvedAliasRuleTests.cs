namespace SuperAdminCopilot.Tests.Repair;

using System.Linq;
using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Application.Repair.Schema;
using SuperAdminCopilot.Models;
using Xunit;

public class InferSelfJoinFromUnresolvedAliasRuleTests
{
    private readonly InferSelfJoinFromUnresolvedAliasRule _rule = new();

    [Fact]
    public void InfersSelfJoin_ForParentRegionAlias()
    {
        // "list every region with its parent region" → LLM projects ParentRegion.NameEn
        // but never declared the self-join. Regions has a self-FK (ParentRegionId → Id).
        var spec = new QuerySpec
        {
            Root = "Regions",
            Select = { "Regions.NameEn", "ParentRegion.NameEn" },
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Regions",
                new ForeignKeyEdge("Regions", "ParentRegionId", "Regions", "Id"))
            .Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // A left self-join aliased "ParentRegion" was inserted.
        Assert.Contains(spec.Joins, j =>
            j.Table == "Regions" && j.Alias == "ParentRegion" && j.Kind == "left");
    }

    [Fact]
    public void NoOp_WhenRootHasNoSelfFk()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Select = { "Tickets.Title", "ParentTicket.Title" },
        };
        // Tickets FK goes to a DIFFERENT table — not a self-FK.
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Tickets",
                new ForeignKeyEdge("Tickets", "RegionId", "Regions", "Id"))
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void NoOp_WhenAliasIsNotSelfJoinShaped()
    {
        // Unresolved alias "Foo" doesn't start with a self-join prefix (Parent/Child/...).
        var spec = new QuerySpec
        {
            Root = "Regions",
            Select = { "Regions.NameEn", "Foo.NameEn" },
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Regions", new ForeignKeyEdge("Regions", "ParentRegionId", "Regions", "Id"))
            .Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    [Fact]
    public void Idempotent_DoesNotAddDuplicateJoin()
    {
        var spec = new QuerySpec
        {
            Root = "Regions",
            Select = { "Regions.NameEn", "ParentRegion.NameEn" },
            Joins = { new JoinSpec { Table = "Regions", Kind = "left", Alias = "ParentRegion" } },
        };
        var ctx = new RepairRuleHarness()
            .WithForeignKeys("Regions", new ForeignKeyEdge("Regions", "ParentRegionId", "Regions", "Id"))
            .Build();

        // The alias is already resolved via the existing join → no fault.
        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }
}
