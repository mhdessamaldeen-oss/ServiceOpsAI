namespace SuperAdminCopilot.Tests.Repair;

using SuperAdminCopilot.Application.Repair.Rules;
using SuperAdminCopilot.Models;
using Xunit;

public class DanglingColumnReferenceRuleTests
{
    private readonly DanglingColumnReferenceRule _rule = new();

    [Fact]
    public void QualifiesBareColumnReference_OnRoot()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Status", Op = "eq", Value = "Open" } },
        };
        // ColumnExists("Tickets","Status") defaults true in the harness.
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        Assert.Equal("Tickets.Status", spec.Filters[0].Column);
    }

    [Fact]
    public void LeavesAlreadyQualifiedColumns_Untouched()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Tickets.Status", Op = "eq", Value = "Open" } },
            Select = { "Tickets.Title" },
        };
        var ctx = new RepairRuleHarness().Build();

        RepairRuleHarness.DetectNoFault(_rule, spec, ctx);
    }

    /// <summary>
    /// REGRESSION test for the 2026-06-01 descending-RemoveAt fragility: when MULTIPLE
    /// non-adjacent SELECT columns are covered by aggregations and must be dropped, the
    /// surviving columns must be exactly the uncovered ones. An ascending RemoveAt would
    /// shift indices and drop the wrong columns (or throw). Locks the contract regardless
    /// of the order Detect collects the drop actions.
    /// </summary>
    [Fact]
    public void DropsMultipleAggregatedSelectColumns_KeepsCorrectSurvivors()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            // A and C are covered by aggregations; B is not. All qualified so Pattern-1 skips them.
            Select = { "Tickets.A", "Tickets.B", "Tickets.C" },
            Aggregations =
            {
                new AggregateSpec { Function = "MAX", Column = "Tickets.A" },
                new AggregateSpec { Function = "MIN", Column = "Tickets.C" },
            },
        };
        var ctx = new RepairRuleHarness().Build();

        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);
        _rule.Apply(spec, dx);

        // Exactly the uncovered column survives; A and C were dropped without index skew.
        Assert.Equal(new[] { "Tickets.B" }, spec.Select);
    }

    [Fact]
    public void Apply_ReturnsSameInstance()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "Status", Op = "eq", Value = "Open" } },
        };
        var ctx = new RepairRuleHarness().Build();
        var dx = RepairRuleHarness.DetectFault(_rule, spec, ctx);

        Assert.Same(spec, _rule.Apply(spec, dx));
    }
}
