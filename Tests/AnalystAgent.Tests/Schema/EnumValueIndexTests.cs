namespace AnalystAgent.Tests.Schema;

using AnalystAgent.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>Covers the entity-subtype VALUE→TABLE index that fixes the entity-type→wrong-table confident-wrong
/// ("how many transformers" counted Customers). The index must catch the entity-subtype values (*Type columns)
/// while leaving attribute values (Status/Priority — common words like "active"/"resolved") OUT, drop generic /
/// short / ambiguous tokens, and stay schema-agnostic.</summary>
public class EnumValueIndexTests
{
    private static IEntityCatalog Catalog(params (string Table, (string Col, string Val)[] Vals)[] tables)
    {
        var m = new Mock<IEntityCatalog>(MockBehavior.Loose);
        m.Setup(c => c.AllTables()).Returns(tables.Select(t => new TableInfo("dbo", t.Table)).ToList());
        foreach (var t in tables)
            m.Setup(c => c.GetInlineEnumValues(t.Table, It.IsAny<int>()))
             .Returns(t.Vals.Select(v => (v.Col, v.Val)).ToList());
        return m.Object;
    }

    [Fact]
    public void IndexesEntitySubtypeValues_AndExcludesAttributeValues()
    {
        var catalog = Catalog(
            ("Assets", new[] { ("AssetType", "Transformer"), ("AssetType", "Generator"), ("Status", "Operational") }),
            ("Customers", new[] { ("Status", "Active") }),                 // attribute col → never indexed
            ("ServiceAccounts", new[] { ("Status", "Active") }));          // ditto (also would be ambiguous)

        var idx = SchemaLinker.BuildEnumValueIndex(catalog, new[] { "Type" }, NullLogger.Instance);

        Assert.Equal(("Assets", "AssetType"), idx["transformer"]);
        Assert.Equal(("Assets", "AssetType"), idx["generator"]);
        Assert.False(idx.ContainsKey("active"));        // Status is an ATTRIBUTE column, not *Type
        Assert.False(idx.ContainsKey("operational"));   // Status value, excluded
    }

    [Fact]
    public void DropsAmbiguous_Generic_Short_AndMultiWord()
    {
        var catalog = Catalog(
            ("Assets", new[] { ("AssetType", "Transformer"), ("AssetType", "Reactive"),
                               ("AssetType", "Other"), ("AssetType", "Bus"), ("AssetType", "Smart Meter") }),
            ("WorkOrders", new[] { ("OrderType", "Reactive") }));          // 'Reactive' now owned by TWO tables

        var idx = SchemaLinker.BuildEnumValueIndex(catalog, new[] { "Type" }, NullLogger.Instance);

        Assert.True(idx.ContainsKey("transformer"));    // distinctive, single-token, long enough
        Assert.False(idx.ContainsKey("reactive"));      // ambiguous (Assets.AssetType + WorkOrders.OrderType) → dropped
        Assert.False(idx.ContainsKey("other"));         // generic stop word
        Assert.False(idx.ContainsKey("bus"));           // < 4 chars (collision-prone)
        Assert.False(idx.ContainsKey("smart meter"));   // multi-word phrase, not a single noun token
    }

    [Fact]
    public void InlineEnumSkipColumnHints_AreExternalizedToConfig_NotAHardcodedWall()
    {
        // The free-text/identifier deny-list used to be a static string[] inside EntityCatalog with no override
        // (the one true no-portability hard-wall). It now lives on AnalystOptions so a schema with a status enum
        // in e.g. "MessagePriority" can clear the offending fragment without a code change.
        var opts = new AnalystAgent.Configuration.AnalystOptions();
        Assert.NotEmpty(opts.InlineEnumSkipColumnHints);                 // sane defaults preserved
        Assert.Contains("Description", opts.InlineEnumSkipColumnHints);
        Assert.Contains("Id", opts.InlineEnumSkipColumnHints);
        opts.InlineEnumSkipColumnHints = new() { "Custom" };            // operator-overridable
        Assert.Equal(new[] { "Custom" }, opts.InlineEnumSkipColumnHints);
    }

    [Fact]
    public void EmptyIndex_WhenNoSubtypeSuffixesOrNoMatches()
    {
        var catalog = Catalog(("Bills", new[] { ("Status", "Paid"), ("PaymentMethod", "Cash") }));
        // No *Type column on this table → nothing indexed.
        Assert.Empty(SchemaLinker.BuildEnumValueIndex(catalog, new[] { "Type" }, NullLogger.Instance));
        // No suffixes configured → feature off → empty.
        Assert.Empty(SchemaLinker.BuildEnumValueIndex(
            Catalog(("Assets", new[] { ("AssetType", "Transformer") })),
            System.Array.Empty<string>(), NullLogger.Instance));
    }
}
