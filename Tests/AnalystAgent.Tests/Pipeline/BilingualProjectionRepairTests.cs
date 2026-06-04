namespace AnalystAgent.Tests.Pipeline;

using System;
using System.Collections.Generic;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline;
using AnalystAgent.Schema;
using Xunit;

/// <summary>
/// Deterministic bilingual projection-column repair (DirectAnalystPath.TryResolveInvalidProjectionColumn):
/// when the 7B writes a column the table lacks (its strong "Name" prior over the real NameEn/NameAr),
/// rewrite it to the schema's real bilingual column — locale-aware, only on a confirmed "Invalid column
/// name 'X'" error, never altering a valid query.
/// </summary>
public class BilingualProjectionRepairTests
{
    private static readonly IReadOnlyList<string> Suffixes = new AnalystOptions().BilingualLocaleSuffixes; // { En, Ar }
    private const string En = "en";   // QuestionLanguageDetector.English
    private const string Ar = "ar";   // QuestionLanguageDetector.Arabic
    private const string InvalidName = "Invalid column name 'Name'";

    private static InferredTable Table(string name, string? label, params string[] cols)
    {
        var t = new InferredTable { Name = name, Schema = "dbo" };
        t.Roles.LabelColumn = label;
        foreach (var c in cols) t.Columns.Add(new InferredColumn { Name = c, Type = "nvarchar" });
        return t;
    }

    private static Func<string, InferredTable?> Lookup(params InferredTable[] tables)
    {
        var d = new Dictionary<string, InferredTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables) d[t.Name] = t;
        return n => d.TryGetValue(n, out var t) ? t : null;
    }

    [Fact]
    public void English_NameRewritesToNameEn()
    {
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            "SELECT T.Name FROM Regions T", InvalidName, new[] { "Regions" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn", "NameAr")), Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Equal("SELECT T.[NameEn] FROM Regions T", r);
    }

    [Fact]
    public void Arabic_NameRewritesToNameAr()
    {
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            "SELECT Name FROM Regions", InvalidName, new[] { "Regions" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn", "NameAr")), Suffixes, Ar, out var r);
        Assert.True(ok);
        Assert.Contains("[NameAr]", r);
        Assert.DoesNotContain("[NameEn]", r);
    }

    [Fact]
    public void English_prefersNameEn_whenBothExist()
    {
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            "SELECT Name FROM Regions", InvalidName, new[] { "Regions" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn", "NameAr")), Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Contains("[NameEn]", r);
    }

    [Fact]
    public void TableHasRealNameColumn_neverRewrites()
    {
        var sql = "SELECT Name FROM TicketStatuses";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, InvalidName, new[] { "TicketStatuses" },
            Lookup(Table("TicketStatuses", "Name", "Id", "Name")), Suffixes, En, out var r);
        Assert.False(ok);
        Assert.Equal(sql, r);
    }

    [Fact]
    public void NoTwinAndNoMatchingLabel_noRewrite()
    {
        var sql = "SELECT TotalSpend FROM Customers";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, "Invalid column name 'TotalSpend'", new[] { "Customers" },
            Lookup(Table("Customers", "FullNameEn", "Id", "FullNameEn")), Suffixes, En, out var r);
        Assert.False(ok);
        Assert.Equal(sql, r);
    }

    [Fact]
    public void LabelColumnFallback_NameToFullNameEn()
    {
        // "Name" has no NameEn twin, but LabelColumn FullNameEn ends in "En" and its base "FullName" ends with "Name".
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            "SELECT Name FROM Customers", InvalidName, new[] { "Customers" },
            Lookup(Table("Customers", "FullNameEn", "Id", "FullNameEn", "FullNameAr")), Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Contains("[FullNameEn]", r);
    }

    [Fact]
    public void Ambiguous_twoCandidateOwners_noRewrite()
    {
        var sql = "SELECT Name FROM Regions r JOIN Departments d ON 1=1";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, InvalidName, new[] { "Regions", "Departments" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn"), Table("Departments", "NameEn", "Id", "NameEn")),
            Suffixes, En, out var r);
        Assert.False(ok);
        Assert.Equal(sql, r);
    }

    [Fact]
    public void NotAnInvalidColumnError_noRewrite()
    {
        var sql = "SELECT Name FROM Regions";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, "Timeout expired.", new[] { "Regions" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn")), Suffixes, En, out var r);
        Assert.False(ok);
        Assert.Equal(sql, r);
    }

    [Fact]
    public void MixedOwners_rewritesQualifiedRegionsName_leavesTicketStatusesName()
    {
        // The JOIN-01 bug: Regions has NO plain Name (NameEn/NameAr) while TicketStatuses HAS a real
        // Name. The model wrote r.Name (invalid) AND s.Name (valid). Repair must fix the Regions hit and
        // leave the valid TicketStatuses hit — the old guard bailed entirely because TicketStatuses owns Name.
        var sql = "SELECT r.Name AS Region, s.Name AS Status FROM Regions r JOIN TicketStatuses s ON 1=1";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, InvalidName, new[] { "Regions", "TicketStatuses" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn", "NameAr"),
                   Table("TicketStatuses", "Name", "Id", "Name")),
            Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Contains("r.[NameEn] AS Region", r);
        Assert.Contains("s.Name AS Status", r);   // valid TicketStatuses.Name must stay untouched
    }

    [Fact]
    public void Unqualified_whenAnotherOwnerHasRealName_noRewrite()
    {
        // Unqualified "Name" with TicketStatuses (owns Name) in the slice — cannot be attributed to one
        // table, so it must be left for the normal retry (never guess).
        var sql = "SELECT Name FROM Regions r JOIN TicketStatuses s ON 1=1";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, InvalidName, new[] { "Regions", "TicketStatuses" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn"), Table("TicketStatuses", "Name", "Id", "Name")),
            Suffixes, En, out var r);
        Assert.False(ok);
        Assert.Equal(sql, r);
    }

    [Fact]
    public void QualifiedAndBracketed_rewritesPreservingQualifier_andNotInsideNameEn()
    {
        // dbo.Regions.[Name] → dbo.Regions.[NameEn]; the existing NameEn in a sibling column must not be touched.
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            "SELECT dbo.Regions.[Name], NameEn FROM Regions", InvalidName, new[] { "Regions" },
            Lookup(Table("Regions", "NameEn", "Id", "NameEn")), Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Contains("dbo.Regions.[NameEn]", r);
    }
}
