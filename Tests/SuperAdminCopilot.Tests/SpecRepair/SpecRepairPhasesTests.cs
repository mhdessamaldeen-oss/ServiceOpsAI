namespace SuperAdminCopilot.Tests.SpecRepair;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.SpecRepair;
using SuperAdminCopilot.Pipeline.SpecRepair.Phases;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using Xunit;

/// <summary>
/// Unit tests for every SpecRepair phase. Each test:
///   1. Constructs the broken QuerySpec exactly as the LLM has been observed emitting it
///      (taken from real captured-trace fixtures in CopilotTraceHistories).
///   2. Runs the phase.
///   3. Asserts the spec is now well-formed.
///   4. Asserts the phase emitted a diagnostic when it mutated, and emitted nothing on a no-op.
/// These tests are the safety net for the consolidation refactor — a future code change can
/// break a phase, but cannot break it silently.
/// </summary>
public class SpecRepairPhasesTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────────────────

    private static SpecRepairOptions DefaultOptions() => new()
    {
        AggregationIntentPattern = @"^\s*(how\s+many|count\s+of|number\s+of|total\s+|sum\s+|average\s+|avg\s+|highest\s+|lowest\s+|max\s+|min\s+)",
        AggregationSqlPattern = @"\b(COUNT|SUM|AVG|MIN|MAX|COUNT_BIG)\s*\(",
        ListIntentPattern = @"^\s*(list|show|give\s+me)",
        AntiJoinIntentPatterns = new() { @"\bwith\s+no\b", @"\bwithout\s+any\b", @"\bthat\s+have\s+(no|not|never)\b" },
        SqlComparisonOperatorMap = new()
        {
            { "=", "eq" }, { "!=", "neq" }, { "<>", "neq" },
            { ">", "gt" }, { ">=", "gte" }, { "<", "lt" }, { "<=", "lte" },
        },
        QuotedValueDelimiters = new() { "'", "\"" },
        AggregationFunctionAliases = new()
        {
            { "COUNT(DISTINCT)", "COUNT" }, { "COUNT_DISTINCT", "COUNT" },
            { "COUNTDISTINCT", "COUNT" }, { "COUNT DISTINCT", "COUNT" },
        },
    };

    private static SpecRepairContext Ctx(string question = "", System.Collections.Generic.IReadOnlyList<InferredTable>? tables = null, IEntityCatalog? catalog = null) => new()
    {
        Question = question,
        CandidateTables = tables ?? System.Array.Empty<InferredTable>(),
        Catalog = catalog ?? Mock.Of<IEntityCatalog>(),
        SemanticLayer = Mock.Of<ISemanticLayer>(),
        Options = DefaultOptions(),
    };

    // ── HoistInlineAggregatesPhase ───────────────────────────────────────────────────────

    [Fact]
    public void HoistInline_SumWithAlias_MovesToAggregations()
    {
        // From real trace: "total amount across all bills"
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "SUM(Bills.TotalAmount) AS total_billed" },
        };
        var ctx = Ctx();
        new HoistInlineAggregatesPhase().Apply(spec, ctx);

        Assert.Empty(spec.Select);
        Assert.Single(spec.Aggregations);
        Assert.Equal("SUM", spec.Aggregations[0].Function);
        Assert.Equal("Bills.TotalAmount", spec.Aggregations[0].Column);
        Assert.Equal("total_billed", spec.Aggregations[0].Alias);
        Assert.Single(ctx.Diagnostics);
    }

    [Fact]
    public void HoistInline_BareColumnWithAlias_StripsAliasOnly()
    {
        var spec = new QuerySpec
        {
            Root = "Customers",
            Select = { "Customers.FullNameEn AS customer" },
        };
        var ctx = Ctx();
        new HoistInlineAggregatesPhase().Apply(spec, ctx);

        Assert.Single(spec.Select);
        Assert.Equal("Customers.FullNameEn", spec.Select[0]);
        Assert.Empty(spec.Aggregations);
    }

    [Fact]
    public void HoistInline_PlainColumn_NoOp_NoDiagnostic()
    {
        var spec = new QuerySpec { Root = "Bills", Select = { "Bills.TotalAmount" } };
        var ctx = Ctx();
        new HoistInlineAggregatesPhase().Apply(spec, ctx);

        Assert.Single(spec.Select);
        Assert.Equal("Bills.TotalAmount", spec.Select[0]);
        Assert.Empty(ctx.Diagnostics);
    }

    // ── NormalizeAggregationFunctionPhase ─────────────────────────────────────────────────

    [Fact]
    public void NormalizeAgg_CountDistinctVariant_BecomesCountWithDistinct()
    {
        // From real trace: BL-CNT-003
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT(DISTINCT)", Column = "TicketCategories.ServiceTypeId" } },
        };
        var ctx = Ctx();
        new NormalizeAggregationFunctionPhase().Apply(spec, ctx);

        Assert.Equal("COUNT", spec.Aggregations[0].Function);
        Assert.True(spec.Aggregations[0].Distinct);
    }

    [Fact]
    public void NormalizeAgg_DistinctInColumn_LiftsDistinctFlag()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "DISTINCT Tickets.CategoryId" } },
        };
        new NormalizeAggregationFunctionPhase().Apply(spec, Ctx());

        Assert.Equal("Tickets.CategoryId", spec.Aggregations[0].Column);
        Assert.True(spec.Aggregations[0].Distinct);
    }

    // ── NormalizeFilterOperatorPhase ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("=", "eq")]
    [InlineData("!=", "neq")]
    [InlineData("<>", "neq")]
    [InlineData(">", "gt")]
    [InlineData(">=", "gte")]
    [InlineData("<", "lt")]
    [InlineData("<=", "lte")]
    public void NormalizeOp_RawSqlOperator_BecomesSymbolic(string raw, string expected)
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = raw, Value = "Paid" } },
        };
        new NormalizeFilterOperatorPhase().Apply(spec, Ctx());

        Assert.Equal(expected, spec.Filters[0].Op);
    }

    [Fact]
    public void NormalizeOp_AlreadySymbolic_NoOp()
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = "eq", Value = "Paid" } },
        };
        var ctx = Ctx();
        new NormalizeFilterOperatorPhase().Apply(spec, ctx);

        Assert.Equal("eq", spec.Filters[0].Op);
        Assert.Empty(ctx.Diagnostics);
    }

    // ── StripQuotedFilterValuesPhase ──────────────────────────────────────────────────────

    [Fact]
    public void StripQuoted_SingleQuotedString_Unquoted()
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = "eq", Value = "'Overdue'" } },
        };
        new StripQuotedFilterValuesPhase().Apply(spec, Ctx());

        Assert.Equal("Overdue", spec.Filters[0].Value);
    }

    [Fact]
    public void StripQuoted_JsonElementString_Unquoted()
    {
        // FilterSpec.Value is object?, so after System.Text.Json deserialization
        // a string value lands as a JsonElement of kind String.
        using var doc = JsonDocument.Parse("\"'Overdue'\"");
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = "eq", Value = doc.RootElement.Clone() } },
        };
        new StripQuotedFilterValuesPhase().Apply(spec, Ctx());

        Assert.Equal("Overdue", spec.Filters[0].Value);
    }

    [Fact]
    public void StripQuoted_UnquotedString_NoOp()
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = "eq", Value = "Overdue" } },
        };
        var ctx = Ctx();
        new StripQuotedFilterValuesPhase().Apply(spec, ctx);

        Assert.Equal("Overdue", spec.Filters[0].Value);
        Assert.Empty(ctx.Diagnostics);
    }

    // ── InferRootFromQuestionPhase ────────────────────────────────────────────────────────

    [Fact]
    public void InferRoot_FromSingularInQuestion_PicksFactTable()
    {
        var spec = new QuerySpec
        {
            Root = "", // missing
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "CASE WHEN TicketStatuses.Name='Open' THEN 1 ELSE 0 END", Alias = "open" } },
        };
        var tables = new[]
        {
            MkTable("TicketStatuses"),
            MkTable("Tickets"),
            MkTable("TicketCategories"),
        };
        // "compare open vs closed ticket counts" contains "ticket" (singular for Tickets)
        var ctx = Ctx(question: "compare open vs closed ticket counts", tables: tables);
        new InferRootFromQuestionPhase().Apply(spec, ctx);

        // Tickets should win over TicketStatuses despite TicketStatuses being referenced in
        // the aggregation expression, because the question-text match is more reliable.
        Assert.Equal("Tickets", spec.Root);
    }

    [Fact]
    public void InferRoot_StripsAnnotationLines_BeforeMatching()
    {
        // The retriever appends "-- resolved: ... TicketStatuses ..." annotations that
        // would bias scoring toward the lookup table if not stripped.
        var spec = new QuerySpec { Root = "" };
        var tables = new[] { MkTable("Tickets"), MkTable("TicketStatuses") };
        var question = "compare open vs closed ticket counts\n-- resolved: 'open' = TicketStatuses.Name 'Open'\n-- requested columns: open, closed";
        var ctx = Ctx(question: question, tables: tables);
        new InferRootFromQuestionPhase().Apply(spec, ctx);

        Assert.Equal("Tickets", spec.Root);
    }

    [Fact]
    public void InferRoot_RootAlreadySet_SingleWordMatch_DoesNotOverride()
    {
        // LLM picked Bills; question mentions "tickets" but only single-word match — keep LLM choice.
        var spec = new QuerySpec { Root = "Bills" };
        var ctx = Ctx(question: "how many tickets", tables: new[] { MkTable("Tickets") });
        new InferRootFromQuestionPhase().Apply(spec, ctx);

        Assert.Equal("Bills", spec.Root);
    }

    [Fact]
    public void InferRoot_MultiWordPascalMatch_OverridesLlm()
    {
        // From real trace 504: LLM picked Tickets for "how many ticket categories".
        // TicketCategories → "ticket categories" is a 2-word PascalCase phrase that appears
        // in the question — much stronger signal than Tickets' single-word "ticket" match.
        var spec = new QuerySpec { Root = "Tickets" };
        var tables = new[] { MkTable("Tickets"), MkTable("TicketCategories"), MkTable("TicketPriorities") };
        var ctx = Ctx(question: "how many ticket categories", tables: tables);
        new InferRootFromQuestionPhase().Apply(spec, ctx);

        Assert.Equal("TicketCategories", spec.Root);
        Assert.Single(ctx.Diagnostics);
        Assert.Contains("overrode", ctx.Diagnostics[0].Detail);
    }

    // ── InferRootFromColumnRefsPhase ──────────────────────────────────────────────────────

    [Fact]
    public void InferRootFromColumnRefs_FromFirstQualifiedRef()
    {
        var spec = new QuerySpec
        {
            Root = "",
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "Bills.Id" } },
        };
        new InferRootFromColumnRefsPhase().Apply(spec, Ctx());

        Assert.Equal("Bills", spec.Root);
    }

    [Fact]
    public void InferRootFromColumnRefs_NoQualifiedRef_NoOp()
    {
        var spec = new QuerySpec { Root = "", Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*" } } };
        var ctx = Ctx();
        new InferRootFromColumnRefsPhase().Apply(spec, ctx);

        Assert.Equal("", spec.Root);
        Assert.Empty(ctx.Diagnostics);
    }

    // ── AutoQualifyColumnsPhase ───────────────────────────────────────────────────────────

    [Fact]
    public void AutoQualify_BareColumnOnRoot_PrependsRootTable()
    {
        var catalog = new Mock<IEntityCatalog>();
        catalog.Setup(c => c.TableExists("Bills")).Returns(true);
        catalog.Setup(c => c.ColumnExists("Bills", "TotalAmount")).Returns(true);
        var spec = new QuerySpec
        {
            Root = "Bills",
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "TotalAmount" } },
        };
        new AutoQualifyColumnsPhase().Apply(spec, Ctx(catalog: catalog.Object));

        Assert.Equal("Bills.TotalAmount", spec.Aggregations[0].Column);
    }

    [Fact]
    public void AutoQualify_AlreadyQualified_NoOp()
    {
        var catalog = new Mock<IEntityCatalog>();
        catalog.Setup(c => c.TableExists("Bills")).Returns(true);
        var spec = new QuerySpec
        {
            Root = "Bills",
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Bills.TotalAmount" } },
        };
        new AutoQualifyColumnsPhase().Apply(spec, Ctx(catalog: catalog.Object));

        Assert.Equal("Bills.TotalAmount", spec.Aggregations[0].Column);
    }

    [Fact]
    public void AutoQualify_ExpressionColumn_NotMangled()
    {
        var catalog = new Mock<IEntityCatalog>();
        catalog.Setup(c => c.TableExists("Bills")).Returns(true);
        var spec = new QuerySpec
        {
            Root = "Bills",
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "CASE WHEN Bills.Status='Paid' THEN 1 ELSE 0 END" } },
        };
        new AutoQualifyColumnsPhase().Apply(spec, Ctx(catalog: catalog.Object));

        Assert.StartsWith("CASE WHEN", spec.Aggregations[0].Column);
    }

    [Fact]
    public void AutoQualify_UnknownBareColumn_StaysBare()
    {
        // Real typo — column doesn't exist on root. Phase must NOT invent a qualification.
        // The downstream validator should fail loudly so the user sees the typo.
        var catalog = new Mock<IEntityCatalog>();
        catalog.Setup(c => c.TableExists("Bills")).Returns(true);
        catalog.Setup(c => c.ColumnExists("Bills", "TotalAmout")).Returns(false);
        var spec = new QuerySpec
        {
            Root = "Bills",
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "TotalAmout" } },
        };
        new AutoQualifyColumnsPhase().Apply(spec, Ctx(catalog: catalog.Object));

        Assert.Equal("TotalAmout", spec.Aggregations[0].Column);
    }

    // ── DropAggregatedColumnsFromSelectPhase ──────────────────────────────────────────────

    [Fact]
    public void DropAggregated_MetricInSelectNoGroupBy_DropsIt()
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.TotalAmount" },
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Bills.TotalAmount", Alias = "total" } },
        };
        new DropAggregatedColumnsFromSelectPhase().Apply(spec, Ctx());

        Assert.Empty(spec.Select);
    }

    [Fact]
    public void DropAggregated_HasGroupBy_KeepsSelect()
    {
        // Legitimate per-group SUM — user wants SUM per row, leave select alone.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.Status" },
            GroupBy = { "Bills.Status" },
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Bills.TotalAmount", Alias = "total" } },
        };
        new DropAggregatedColumnsFromSelectPhase().Apply(spec, Ctx());

        Assert.Single(spec.Select);
        Assert.Equal("Bills.Status", spec.Select[0]);
    }

    // ── DropFilterContradictingGroupByPhase ──────────────────────────────────────────────

    [Fact]
    public void DropContradictingFilter_EqFilterOnGroupByCol_Dropped()
    {
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.Status", Op = "eq", Value = "Paid" } },
            GroupBy = { "Bills.Status" },
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Bills.TotalAmount", Alias = "total" } },
        };
        new DropFilterContradictingGroupByPhase().Apply(spec, Ctx());

        Assert.Empty(spec.Filters);
    }

    [Fact]
    public void DropContradictingFilter_FilterAndGroupByOnDifferentColumns_NoOp()
    {
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "TicketStatuses.Name", Op = "eq", Value = "Open" } },
            GroupBy = { "TicketCategories.Name" },
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*", Alias = "cnt" } },
        };
        var ctx = Ctx();
        new DropFilterContradictingGroupByPhase().Apply(spec, ctx);

        Assert.Single(spec.Filters);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void DropContradictingFilter_NonEqOperator_NotDropped()
    {
        // A `gt` filter on the group-by column (e.g. "bills > 100 grouped by status") is a
        // legitimate HAVING-like pattern; the eq-specific drop must not touch it.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Filters = { new FilterSpec { Column = "Bills.TotalAmount", Op = "gt", Value = 100 } },
            GroupBy = { "Bills.TotalAmount" }, // hypothetical
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*", Alias = "cnt" } },
        };
        new DropFilterContradictingGroupByPhase().Apply(spec, Ctx());

        Assert.Single(spec.Filters);
    }

    // ── UpgradeInnerJoinToAntiJoinPhase ──────────────────────────────────────────────────

    [Fact]
    public void UpgradeAntiJoin_QuestionSaysNoY_InnerBecomesAnti()
    {
        // "departments that have no tickets" → LLM emits INNER JOIN to Tickets;
        // phase upgrades to kind="anti".
        var spec = new QuerySpec
        {
            Root = "Departments",
            Joins = { new JoinSpec { Table = "Tickets", Kind = "inner" } },
        };
        var ctx = Ctx(question: "departments that have no tickets");
        new UpgradeInnerJoinToAntiJoinPhase().Apply(spec, ctx);

        Assert.Equal("anti", spec.Joins[0].Kind);
        Assert.Single(ctx.Diagnostics);
    }

    [Fact]
    public void UpgradeAntiJoin_QuestionWithoutAntiIntent_NoOp()
    {
        var spec = new QuerySpec
        {
            Root = "Departments",
            Joins = { new JoinSpec { Table = "Tickets", Kind = "inner" } },
        };
        var ctx = Ctx(question: "list all departments with their tickets");
        new UpgradeInnerJoinToAntiJoinPhase().Apply(spec, ctx);

        Assert.Equal("inner", spec.Joins[0].Kind);
        Assert.Empty(ctx.Diagnostics);
    }

    // ── DropSpuriousGroupByForScalarAggregationPhase ─────────────────────────────────────

    [Fact]
    public void DropSpuriousGroupBy_MaxWithGroupByOnNaturalKey_DropsGroupBy()
    {
        // From real trace: "largest bill" → LLM emits MAX(TotalAmount) + GROUP BY BillNumber
        // → returns 1000 rows of per-bill maxes instead of one scalar.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.BillNumber" },
            Aggregations = { new AggregateSpec { Function = "MAX", Column = "Bills.TotalAmount", Alias = "largest" } },
            GroupBy = { "Bills.BillNumber" },
        };
        new DropSpuriousGroupByForScalarAggregationPhase().Apply(spec, Ctx());

        Assert.Empty(spec.GroupBy);
        Assert.Empty(spec.Select);
    }

    [Fact]
    public void DropSpuriousGroupBy_GroupByOnDimension_KeepsGroupBy()
    {
        // Legit per-dimension MAX — GROUP BY is on a non-identifier column.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.Status" },
            Aggregations = { new AggregateSpec { Function = "MAX", Column = "Bills.TotalAmount", Alias = "max_amount" } },
            GroupBy = { "Bills.Status" },
        };
        new DropSpuriousGroupByForScalarAggregationPhase().Apply(spec, Ctx());

        Assert.Single(spec.GroupBy);
        Assert.Equal("Bills.Status", spec.GroupBy[0]);
    }

    [Fact]
    public void DropSpuriousGroupBy_CountAggregation_NotTouched()
    {
        // COUNT can legitimately group by anything.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.BillNumber" },
            Aggregations = { new AggregateSpec { Function = "COUNT", Column = "*", Alias = "cnt" } },
            GroupBy = { "Bills.BillNumber" },
        };
        new DropSpuriousGroupByForScalarAggregationPhase().Apply(spec, Ctx());

        Assert.Single(spec.GroupBy);
    }

    // ── MapValueSynonymsPhase ────────────────────────────────────────────────────────────

    [Fact]
    public void MapValueSynonyms_CanonicaliseUrgentToCritical()
    {
        // Wire a SemanticLayer mock that resolves "urgent" → "Critical" for the priority column.
        var sl = new Mock<ISemanticLayer>();
        sl.Setup(s => s.ResolveSynonymValue("TicketPriorities.Name", "urgent")).Returns("Critical");
        sl.Setup(s => s.ResolveSynonymValue(It.IsAny<string>(), It.IsAny<string>())).Returns((string c, string v) =>
            c == "TicketPriorities.Name" && v == "urgent" ? "Critical" : v);

        var ctx = new SpecRepairContext
        {
            Question = "count of urgent priority complaints",
            CandidateTables = System.Array.Empty<InferredTable>(),
            Catalog = Mock.Of<IEntityCatalog>(),
            SemanticLayer = sl.Object,
            Options = DefaultOptions(),
        };
        var spec = new QuerySpec
        {
            Root = "Tickets",
            Filters = { new FilterSpec { Column = "TicketPriorities.Name", Op = "eq", Value = "urgent" } },
        };
        new MapValueSynonymsPhase().Apply(spec, ctx);

        Assert.Equal("Critical", spec.Filters[0].Value);
    }

    // ── InferRootFromQuestion — synonym-aware scoring ────────────────────────────────────

    [Fact]
    public void InferRoot_EntitySynonym_BeatsOtherCandidates()
    {
        // "satisfaction survey responses" — "satisfaction" and "survey" are CsatResponse synonyms.
        // Without the synonym scoring, the LLM picks Tickets ("responses" loosely matches);
        // with synonyms, CsatResponses wins.
        var sl = new Mock<ISemanticLayer>();
        sl.Setup(s => s.GetEntityForTable("CsatResponses")).Returns(new EntityDefinition
        {
            Name = "CsatResponse",
            Table = "CsatResponses",
            Synonyms = new() { "csat", "satisfaction", "survey", "feedback" },
        });
        sl.Setup(s => s.GetEntityForTable("Tickets")).Returns(new EntityDefinition
        {
            Name = "Ticket",
            Table = "Tickets",
            Synonyms = new() { "ticket", "tickets", "issue", "complaint" },
        });

        var spec = new QuerySpec { Root = "" };
        var ctx = new SpecRepairContext
        {
            Question = "how many satisfaction survey responses are there",
            CandidateTables = new[] { MkTable("Tickets"), MkTable("CsatResponses") },
            Catalog = Mock.Of<IEntityCatalog>(),
            SemanticLayer = sl.Object,
            Options = DefaultOptions(),
        };
        new InferRootFromQuestionPhase().Apply(spec, ctx);

        Assert.Equal("CsatResponses", spec.Root);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static InferredTable MkTable(string name) => new()
    {
        Name = name,
        Schema = "dbo",
    };
}
