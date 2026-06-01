namespace SuperAdminCopilot.Tests.Repair;

using System.Collections.Generic;
using Moq;
using SuperAdminCopilot.Application.Repair;
using SuperAdminCopilot.Application.Repair.Schema;
using SuperAdminCopilot.Application.Repair.Semantic;

/// <summary>
/// Reusable builder for repair-rule unit tests. Each test sets up only the mock behavior its
/// rule actually consults, then builds a <see cref="RepairContext"/>. Keeps the per-test
/// boilerplate down to the few <c>.With*</c> calls that matter for the rule under test.
///
/// <para>Added 2026-06-01 alongside the first per-rule unit tests — the
/// <c>DanglingColumnReferenceRule</c> descending-RemoveAt bug found that day would have been
/// caught instantly by a test, so the highest-stakes rules now get coverage.</para>
/// </summary>
internal sealed class RepairRuleHarness
{
    private string _question = "";
    private PlannerTier _tier = PlannerTier.Weak;
    private readonly Mock<ISchemaView> _schema = new(MockBehavior.Loose);
    private readonly Mock<ISemanticView> _semantic = new(MockBehavior.Loose);
    private readonly Mock<ILinguisticRegistry> _linguistics = new(MockBehavior.Loose);

    public RepairRuleHarness()
    {
        // Sensible defaults so a test only overrides what it cares about.
        _schema.Setup(s => s.TableExists(It.IsAny<string>())).Returns(true);
        _schema.Setup(s => s.ColumnExists(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _schema.Setup(s => s.ForeignKeysFrom(It.IsAny<string>())).Returns(System.Array.Empty<ForeignKeyEdge>());
        _schema.Setup(s => s.ForeignKeysTo(It.IsAny<string>())).Returns(System.Array.Empty<ForeignKeyEdge>());
        _schema.Setup(s => s.ColumnsOf(It.IsAny<string>())).Returns(System.Array.Empty<string>());
        _schema.Setup(s => s.IsNumericColumn(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        _semantic.Setup(s => s.DisplayColumnsFor(It.IsAny<string>())).Returns(System.Array.Empty<string>());
        _semantic.Setup(s => s.LabelColumnFor(It.IsAny<string>())).Returns((string?)null);
        _semantic.Setup(s => s.GetDateColumn(It.IsAny<string>(), It.IsAny<string?>())).Returns((string?)null);
        _linguistics.Setup(l => l.LooksLikeAggregateQuery(It.IsAny<string>())).Returns(false);
        _linguistics.Setup(l => l.ExtractTemporal(It.IsAny<string>())).Returns(System.Array.Empty<TemporalSpan>());
    }

    public RepairRuleHarness WithQuestion(string q) { _question = q; return this; }
    public RepairRuleHarness WithTier(PlannerTier t) { _tier = t; return this; }

    public RepairRuleHarness WithForeignKeys(string fromTable, params ForeignKeyEdge[] edges)
    {
        _schema.Setup(s => s.ForeignKeysFrom(fromTable)).Returns(edges);
        return this;
    }

    public RepairRuleHarness WithLabelColumn(string table, string labelColumn)
    {
        _semantic.Setup(s => s.LabelColumnFor(table)).Returns(labelColumn);
        return this;
    }

    public RepairRuleHarness WithDisplayColumns(string table, params string[] cols)
    {
        _semantic.Setup(s => s.DisplayColumnsFor(table)).Returns(cols);
        return this;
    }

    public RepairRuleHarness WithDateColumn(string table, string dateColumn)
    {
        _semantic.Setup(s => s.GetDateColumn(table, It.IsAny<string?>())).Returns(dateColumn);
        return this;
    }

    public RepairRuleHarness WithTemporal(params TemporalSpan[] spans)
    {
        _linguistics.Setup(l => l.ExtractTemporal(It.IsAny<string>())).Returns(spans);
        return this;
    }

    public RepairRuleHarness WithAggregateMarker(bool value)
    {
        _linguistics.Setup(l => l.LooksLikeAggregateQuery(It.IsAny<string>())).Returns(value);
        return this;
    }

    public RepairRuleHarness WithColumnExists(string table, string column, bool exists)
    {
        _schema.Setup(s => s.ColumnExists(table, column)).Returns(exists);
        return this;
    }

    /// <summary>Sets up the semantic + linguistic mocks so <c>NaturalKeyTokenRule</c> fires:
    /// the entity declares a natural-key format, and the linguistic registry "finds" the token in
    /// the question. Without this, the loose mock returns null for <c>NaturalKeyFormats</c>, which
    /// the rule would dereference.</summary>
    public RepairRuleHarness WithNaturalKey(string table, string keyColumn, string token, string formatRegex = ".+")
    {
        _semantic.Setup(s => s.NaturalKeyFormats)
            .Returns(new[] { new NaturalKeyFormat(table, keyColumn, formatRegex) });
        _linguistics.Setup(l => l.ExtractNaturalKeyToken(It.IsAny<string>(), It.IsAny<IReadOnlyList<NaturalKeyFormat>>()))
            .Returns(new NaturalKeyTokenMention(table, keyColumn, token));
        return this;
    }

    public RepairContext Build() => new(
        Question: _question,
        Linguistics: _linguistics.Object,
        Schema: _schema.Object,
        Semantic: _semantic.Object,
        ActiveTier: _tier);

    /// <summary>Detect → assert it found a fault → return the Diagnosis for Apply.</summary>
    public static Diagnosis DetectFault(IRepairRule rule, SuperAdminCopilot.Models.QuerySpec spec, RepairContext ctx)
    {
        var result = rule.Detect(spec, ctx);
        Xunit.Assert.True(result.IsOk, "Detect returned a Fault, expected Ok(Diagnosis).");
        var dx = result.Value;
        Xunit.Assert.NotEqual(RepairFaultKind.None, dx.Kind);
        return dx;
    }

    /// <summary>Detect → assert it found NO fault (NoFault sentinel).</summary>
    public static void DetectNoFault(IRepairRule rule, SuperAdminCopilot.Models.QuerySpec spec, RepairContext ctx)
    {
        var result = rule.Detect(spec, ctx);
        Xunit.Assert.True(result.IsOk, "Detect returned a Fault, expected Ok(NoFault).");
        Xunit.Assert.Equal(RepairFaultKind.None, result.Value.Kind);
    }
}
