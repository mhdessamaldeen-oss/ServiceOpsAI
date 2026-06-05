namespace AnalystAgent.Tests.Pipeline;

using System;
using System.Linq;
using System.Text;
using AnalystAgent.Pipeline.Stages;
using AnalystAgent.Schema;
using Xunit;

/// <summary>
/// ENH-7: the SQL-emitter schema block signposts bilingual label columns ({base}En/{base}Ar) inline,
/// so the model learns there is no plain "{base}" column — reducing how often the downstream
/// Name→NameEn self-heal has to fire. Only genuine pairs (≥2 locale variants) are tagged.
/// </summary>
public class SchemaBlockBilingualTagTests
{
    private static InferredColumn Col(string name) => new() { Name = name, Type = "nvarchar" };

    private static InferredTable Table(params string[] cols) => new()
    {
        Name = "Regions",
        Schema = "dbo",
        Columns = cols.Select(Col).ToList(),
    };

    private static string Render(InferredTable t, params string[] suffixes)
    {
        var sb = new StringBuilder();
        LlmDirectSqlEmitter.AppendSchemaBlock(
            sb, new[] { t }, "regions", Array.Empty<string>(),
            compactAll: false, localeSuffixes: suffixes.Length > 0 ? suffixes : null);
        return sb.ToString();
    }

    [Fact]
    public void Bilingual_pair_is_tagged_with_no_plain_column_guidance()
    {
        var text = Render(Table("Id", "NameEn", "NameAr"));
        Assert.Contains("Regions.NameEn", text);
        Assert.Contains("[localized", text);          // the tag fired
        Assert.Contains("NameEn / NameAr", text);     // both locale variants listed
    }

    [Fact]
    public void Single_locale_variant_is_not_tagged()
    {
        // NameEn with no NameAr sibling is not a genuine bilingual pair → no tag.
        Assert.DoesNotContain("[localized", Render(Table("Id", "NameEn", "Title")));
    }

    [Fact]
    public void Plain_column_ending_in_locale_letters_is_not_mistaken()
    {
        // "Open" ends in "en" but case-sensitive matching must not treat it as an "…En" column.
        Assert.DoesNotContain("[localized", Render(Table("Id", "Open", "Title")));
    }

    [Fact]
    public void Honors_configured_suffixes()
    {
        // A deployment using Fr/De should tag those, not the default En/Ar.
        var text = Render(Table("Id", "LabelFr", "LabelDe"), "Fr", "De");
        Assert.Contains("[localized", text);
        Assert.Contains("LabelFr / LabelDe", text);
    }

    [Fact]
    public void Tail_named_projection_subject_keeps_its_label_columns()
    {
        // "AspNetUsers" is asked for by its bare tail "users", not "aspnetusers". Without the focal tail-match
        // it is classified non-focal and UserName/Email are compacted away — so the 7B has no name column and
        // guesses a non-existent NameEn (the live "list users and their roles" abstain). The tail-match keeps it focal.
        var t = new InferredTable
        {
            Name = "AspNetUsers",
            Schema = "dbo",
            Columns = new[] { "Id", "UserName", "Email", "IsActive", "DepartmentId", "FirstName", "LastName", "PhoneNumber" }
                .Select(Col).ToList(),
        };
        var sb = new StringBuilder();
        LlmDirectSqlEmitter.AppendSchemaBlock(sb, new[] { t }, "list of users and their roles",
            Array.Empty<string>(), compactAll: false);
        var text = sb.ToString();

        Assert.DoesNotContain("key columns only", text);   // focal via the tail "users" — NOT compacted
        Assert.Contains("AspNetUsers.UserName", text);      // the label columns the model needs are visible
        Assert.Contains("AspNetUsers.Email", text);
    }

    [Fact]
    public void NonFocal_neighbor_not_named_stays_compacted()
    {
        // A wide neighbor the question never names stays key-only (the over-fetch guard the tail-match must not break).
        var t = new InferredTable
        {
            Name = "ServiceTypes",
            Schema = "dbo",
            Columns = new[] { "Id", "NameEn", "NameAr", "IconClass", "SortOrder", "IsActive", "Code", "Description" }
                .Select(Col).ToList(),
        };
        var sb = new StringBuilder();
        LlmDirectSqlEmitter.AppendSchemaBlock(sb, new[] { t }, "how many transformers", Array.Empty<string>(), compactAll: false);
        Assert.Contains("key columns only", sb.ToString());  // "transformers" matches neither name nor tail "types"
    }
}
