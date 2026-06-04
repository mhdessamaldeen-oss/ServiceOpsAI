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
    public void ReverseTwin_NameEnRewritesToPlainName_inWhereClause()
    {
        // The open-tickets bug (live, session 2259): the 7B's "every label is bilingual" prior made it
        // write `TicketStatuses.NameEn = 'Open'`, but TicketStatuses has a PLAIN Name. Repair strips the
        // spurious locale suffix to the real base column — PRESERVING the WHERE filter — so the lossy
        // strip-predicate fallback never drops it (which counted ALL tickets, not just the open ones).
        var sql = "SELECT COUNT(*) FROM Tickets JOIN TicketStatuses s ON Tickets.StatusId = s.Id WHERE s.NameEn = 'Open'";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, "Invalid column name 'NameEn'", new[] { "Tickets", "TicketStatuses" },
            Lookup(Table("Tickets", null, "Id", "StatusId"), Table("TicketStatuses", "Name", "Id", "Name")),
            Suffixes, En, out var r);
        Assert.True(ok);
        Assert.Contains("s.[Name] = 'Open'", r);
        Assert.DoesNotContain("NameEn", r);
    }

    [Fact]
    public void ReverseTwin_doesNotFire_whenBaseAbsent()
    {
        // `IsDeletedEn` has no plain `IsDeleted` base on this table → no rewrite (falls through to the
        // separate strip-predicate fallback). Guards the reverse rule from inventing a base column.
        var sql = "SELECT * FROM Outages o WHERE o.IsDeletedEn = 0";
        var ok = DirectAnalystPath.TryResolveInvalidProjectionColumn(
            sql, "Invalid column name 'IsDeletedEn'", new[] { "Outages" },
            Lookup(Table("Outages", null, "Id", "StartedAt")), Suffixes, En, out var r);
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

    // ── GROUP-BY grain fix: add the entity key to a label-only grouping (generality across tables) ──
    private static InferredTable TableWithPk(string name, string label, string pk, params string[] cols)
    {
        var t = new InferredTable { Name = name, Schema = "dbo" };
        t.Roles.LabelColumn = label;
        foreach (var c in cols) t.Columns.Add(new InferredColumn { Name = c, Type = "nvarchar" });
        t.PrimaryKey.Add(pk);
        return t;
    }

    [Fact]
    public void FixGroupByGrain_AddsEntityKey_GeneralAcrossTables()
    {
        // Customers grouped by NAME (qualified by table name) → add Customers.Id so distinct same-named customers don't merge
        Assert.True(DirectAnalystPath.TryFixGroupByGrain(
            "SELECT TOP 5 Customers.FullNameEn, SUM(b.TotalAmount) FROM Bills b JOIN Customers ON b.CustomerId=Customers.Id GROUP BY Customers.FullNameEn ORDER BY SUM(b.TotalAmount) DESC",
            Lookup(TableWithPk("Customers", "FullNameEn", "Id", "Id", "FullNameEn")), out var r1));
        Assert.Contains("GROUP BY Customers.Id, Customers.FullNameEn", r1);

        // DIFFERENT table, ALIASED (Departments d) → same fix, proving it's not Customers-specific
        Assert.True(DirectAnalystPath.TryFixGroupByGrain(
            "SELECT d.NameEn, COUNT(*) FROM Tickets t JOIN Departments d ON t.DepartmentId=d.Id GROUP BY d.NameEn ORDER BY COUNT(*) DESC",
            Lookup(TableWithPk("Departments", "NameEn", "Id", "Id", "NameEn")), out var r2));
        Assert.Contains("GROUP BY d.Id, d.NameEn", r2);

        // a function/date GROUP BY is never touched (not a label column)
        Assert.False(DirectAnalystPath.TryFixGroupByGrain(
            "SELECT YEAR(CreatedAt), COUNT(*) FROM Tickets GROUP BY YEAR(CreatedAt)",
            Lookup(TableWithPk("Tickets", "Title", "Id", "Id", "Title")), out _));

        // already grouping by the key → no-op
        Assert.False(DirectAnalystPath.TryFixGroupByGrain(
            "SELECT Customers.Id, SUM(x) FROM Bills b JOIN Customers ON 1=1 GROUP BY Customers.Id",
            Lookup(TableWithPk("Customers", "FullNameEn", "Id", "Id", "FullNameEn")), out _));
    }
}
