namespace AnalystAgent.Tests.Grounding;

using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Grounding;
using AnalystAgent.Pipeline.EntityResolution;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

/// <summary>
/// CLASS 3 — EXACT enum-value binding across whitespace / case / camelCase variants. A compact one-token DB
/// value ('InProgress') must whole-word-match its natural two-word phrasing ("in progress") in the question,
/// and bind the EXACT DB literal ('InProgress'), not the question's spaced form. The fold is purely
/// character-class driven (camelCase / digit / underscore / hyphen boundaries) so it is portable to any
/// compact enum in any database — no table / column / value vocabulary anywhere.
///
/// <para>The pure-function tests pin <see cref="ValueLinker.FoldForMatch"/> /
/// <see cref="ValueLinker.FoldedWholeWordMatch"/> directly; the integration tests prove the lookup +
/// inline-enum passes actually bind through the real <see cref="ValueLinker.LinkAsync"/>.</para>
/// </summary>
public class ValueLinkerFoldedMatchTests
{
    private const string Table = "WorkOrders";

    // ── Pure-function tests: the fold normalizer ────────────────────────────────────────────

    [Theory]
    [InlineData("InProgress", "in progress")]
    [InlineData("OnHold", "on hold")]
    [InlineData("In Progress", "in progress")]   // already spaced + mixed case → same canonical form
    [InlineData("WORK_ORDER", "work order")]     // snake_case
    [InlineData("Open", "open")]                 // single token unchanged
    [InlineData("Tier1", "tier 1")]              // digit↔letter boundary
    [InlineData("HTTPStatus", "http status")]    // acronym run does NOT explode to "h t t p status"
    [InlineData("Pre-Paid", "pre paid")]         // hyphen
    public void FoldForMatch_canonicalizes_compact_and_spaced_forms_identically(string input, string expected)
        => Assert.Equal(expected, ValueLinker.FoldForMatch(input));

    [Fact]
    public void FoldForMatch_is_idempotent_compact_and_spaced_fold_to_the_same_key()
        => Assert.Equal(ValueLinker.FoldForMatch("InProgress"), ValueLinker.FoldForMatch("In Progress"));

    [Fact]
    public void FoldedWholeWordMatch_binds_compact_value_to_spaced_phrase()
    {
        var q = " " + ValueLinker.FoldForMatch("show work orders that are currently in progress now") + " ";
        Assert.True(ValueLinker.FoldedWholeWordMatch(q, "InProgress"));
    }

    [Fact]
    public void FoldedWholeWordMatch_is_whole_word_not_substring()
    {
        // 'progress' inside 'progressively' must NOT bind a value whose fold is 'progress'.
        var q = " " + ValueLinker.FoldForMatch("the project advanced progressively this year") + " ";
        Assert.False(ValueLinker.FoldedWholeWordMatch(q, "Progress"));
    }

    [Fact]
    public void FoldedWholeWordMatch_requires_the_full_multiword_phrase()
    {
        // "in" alone in the question must not bind 'InProgress' — the whole folded phrase is required.
        var q = " " + ValueLinker.FoldForMatch("tickets created in damascus") + " ";
        Assert.False(ValueLinker.FoldedWholeWordMatch(q, "InProgress"));
    }

    // ── Integration: the binding flows through LinkAsync and carries the EXACT DB literal ─────

    [Fact]
    public async Task Lookup_pass_binds_exact_DB_literal_for_compact_value()
    {
        var linker = BuildLookup(("Status", "InProgress"), ("Status", "Open"));

        var bindings = await LinkAsync(linker, "work orders that are currently in progress");

        // Binds the COMPACT DB literal, not the question's spaced "in progress".
        Assert.Contains(bindings, b => b.Value == "InProgress" && b.Column == "Status");
        Assert.DoesNotContain(bindings, b => b.Value == "In Progress");
    }

    [Fact]
    public async Task Inline_enum_pass_binds_exact_DB_literal_for_compact_value()
    {
        var linker = BuildInlineEnum(("Status", "InProgress"), ("Status", "OnHold"));

        var bindings = await LinkAsync(linker, "list the work orders on hold right now");

        Assert.Contains(bindings, b => b.Value == "OnHold" && b.Column == "Status");
        Assert.DoesNotContain(bindings, b => b.Value == "On Hold");
    }

    [Fact]
    public async Task Does_not_overbind_when_phrase_absent()
    {
        var linker = BuildLookup(("Status", "InProgress"));

        var bindings = await LinkAsync(linker, "how many work orders do we have");

        Assert.Empty(bindings);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────

    private static ValueLinker BuildLookup(params (string Col, string Val)[] values)
        => Build(lookupValues: values, inlineEnum: System.Array.Empty<(string, string)>());

    private static ValueLinker BuildInlineEnum(params (string Col, string Val)[] values)
        => Build(lookupValues: System.Array.Empty<(string, string)>(), inlineEnum: values);

    private static ValueLinker Build((string Col, string Val)[] lookupValues, (string Col, string Val)[] inlineEnum)
    {
        var snapshot = new SchemaSnapshot
        {
            Tables = System.Array.Empty<TableInfo>(),
            Columns = System.Array.Empty<ColumnInfo>(),
            ForeignKeys = System.Array.Empty<ForeignKeyInfo>(),
            CapturedAt = DateTimeOffset.UnixEpoch,
        };

        var catalog = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalog.Setup(c => c.Snapshot).Returns(snapshot);
        catalog.Setup(c => c.TableExists(It.IsAny<string>())).Returns(true);
        catalog.Setup(c => c.GetAllLookupValues(It.IsAny<string>(), It.IsAny<int>()))
               .Returns(lookupValues.Select(v => (v.Col, v.Val)).ToList());
        catalog.Setup(c => c.GetInlineEnumValues(It.IsAny<string>(), It.IsAny<int>()))
               .Returns(inlineEnum.Select(v => (v.Col, v.Val)).ToList());

        var semantic = new Mock<ISemanticLayer>(MockBehavior.Loose);
        semantic.Setup(s => s.GetEntityForTable(Table))
                .Returns(new EntityDefinition { Name = Table, Table = Table, IsLookup = true });

        var policy = new Mock<IAnalystSchemaAccessPolicy>(MockBehavior.Loose);
        policy.Setup(p => p.IsTableQueryable(It.IsAny<string>())).Returns(true);

        var options = Options.Create(new AnalystOptions
        {
            MaxLookupValueWords = 4,
            EnableFuzzyValueLinking = false,
        });

        return new ValueLinker(
            catalog.Object,
            semantic.Object,
            Mock.Of<IFuzzyEntityResolver>(),
            Mock.Of<ITextEmbedder>(),
            options,
            policy.Object,
            FakeLinguisticRegistry.WithEnglishVerbCues(),
            NullLogger<ValueLinker>.Instance);
    }

    private static async Task<System.Collections.Generic.IReadOnlyList<ValueLinkBinding>> LinkAsync(
        ValueLinker linker, string question)
    {
        var linked = new[] { new InferredTable { Name = Table, Schema = "dbo" } };
        return await linker.LinkAsync(question, linked);
    }
}
