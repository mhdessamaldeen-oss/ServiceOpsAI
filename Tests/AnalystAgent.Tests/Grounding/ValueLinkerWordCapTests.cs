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
/// B2 — the structural word-count cap on the exact-match lookup pass. A genuine lookup/enum value is a
/// short label ("Open", "In Progress"); a value longer than <see cref="AnalystOptions.MaxLookupValueWords"/>
/// is a free-text column (a Title/Name/Notes field that captured a whole sentence) masquerading as a label.
/// The access-policy gate already keeps the copilot's OWN operational tables out of value-linking; this seals
/// the residual class for queryable BUSINESS tables whose short-row free-text label column could otherwise
/// surface a whole logged question as a bindable "value" and force a spurious WHERE.
///
/// These are INTEGRATION tests through the real <see cref="ValueLinker.LinkAsync"/> (not just the predicate),
/// so they prove the cap is actually wired into the lookup loop — the positive proof the seal fires.
/// </summary>
public class ValueLinkerWordCapTests
{
    private const string Table = "ComplaintNotes";   // a small queryable BUSINESS table with a free-text label col

    // Build a real ValueLinker over a fake catalog whose single lookup table returns <paramref name="values"/>
    // as (LabelColumn, Value) pairs. Fuzzy + cross-lingual passes are disabled/inapplicable (English question),
    // so only the exact-match lookup pass runs — isolating B2's behaviour.
    private static ValueLinker Build(int maxLookupValueWords, params (string Col, string Val)[] values)
    {
        var snapshot = new SchemaSnapshot
        {
            Tables = System.Array.Empty<TableInfo>(),       // no FKs => no neighbour expansion; seed itself is the candidate
            Columns = System.Array.Empty<ColumnInfo>(),
            ForeignKeys = System.Array.Empty<ForeignKeyInfo>(),
            CapturedAt = DateTimeOffset.UnixEpoch,
        };

        var catalog = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalog.Setup(c => c.Snapshot).Returns(snapshot);
        catalog.Setup(c => c.TableExists(It.IsAny<string>())).Returns(true);
        catalog.Setup(c => c.GetAllLookupValues(Table, It.IsAny<int>()))
               .Returns(values.Select(v => (v.Col, v.Val)).ToList());
        catalog.Setup(c => c.GetInlineEnumValues(It.IsAny<string>(), It.IsAny<int>()))
               .Returns(System.Array.Empty<(string, string)>());   // isolate: only the exact lookup pass binds

        // IsLookup=true short-circuits IsLookupShaped so the seed table is treated as a lookup candidate.
        var semantic = new Mock<ISemanticLayer>(MockBehavior.Loose);
        semantic.Setup(s => s.GetEntityForTable(Table))
                .Returns(new EntityDefinition { Name = Table, Table = Table, IsLookup = true });

        var policy = new Mock<IAnalystSchemaAccessPolicy>(MockBehavior.Loose);
        policy.Setup(p => p.IsTableQueryable(It.IsAny<string>())).Returns(true);

        var options = Options.Create(new AnalystOptions
        {
            MaxLookupValueWords = maxLookupValueWords,
            EnableFuzzyValueLinking = false,   // keep the fuzzy pass out of the picture
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

    private static async Task<IReadOnlyList<string>> BoundValuesAsync(
        ValueLinker linker, string question)
    {
        var linked = new[] { new InferredTable { Name = Table, Schema = "dbo" } };
        var bindings = await linker.LinkAsync(question, linked);
        return bindings.Select(b => b.Value).ToList();
    }

    [Fact]
    public async Task Drops_a_captured_sentence_value_but_keeps_the_short_label()
    {
        // The free-text column holds BOTH a real short label ("Open") and a whole logged question that was
        // stored as a row ("please reset my meter it is broken"). The question text contains both verbatim.
        var linker = Build(maxLookupValueWords: 4,
            ("Status", "Open"),
            ("Note", "please reset my meter it is broken"));

        var bound = await BoundValuesAsync(linker,
            "list Open complaints: please reset my meter it is broken");

        Assert.Contains("Open", bound);                                     // short label binds
        Assert.DoesNotContain("please reset my meter it is broken", bound); // 6-word sentence is sealed out
    }

    [Fact]
    public async Task Keeps_the_longest_legitimate_value_In_Progress_at_the_default_cap()
    {
        // 'In Progress' (2 words) is the longest legitimate lookup value in-domain (gold-discriminating's
        // IN ('Open','In Progress')). The default cap of 4 must NOT clip it.
        var linker = Build(maxLookupValueWords: 4, ("Name", "In Progress"));

        var bound = await BoundValuesAsync(linker, "show complaints that are In Progress right now");

        Assert.Contains("In Progress", bound);
    }

    [Fact]
    public async Task Cap_of_one_is_unsafe_it_clips_the_two_word_status()
    {
        // Documents the gold-scan's safe-cap boundary: MaxLookupValueWords=1 would drop the legitimate
        // 2-word status, which is why the default is 4 (and the validated floor is 2).
        var linker = Build(maxLookupValueWords: 1, ("Name", "In Progress"));

        var bound = await BoundValuesAsync(linker, "show complaints that are In Progress right now");

        Assert.DoesNotContain("In Progress", bound);
    }
}
