namespace AnalystAgent.Tests.Validation;

using System;
using System.Collections.Generic;
using AnalystAgent.Validation;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

/// <summary>
/// STAGE-2 AST repair passes (<see cref="SqlAstRepairs"/>) — direct unit tests of the three repairs
/// re-expressed as ScriptDom tree mutations. These prove the MECHANISM: GROUP-BY grain preserves the
/// HAVING/ORDER BY boundary (the gluing bug that motivated the rewrite), the status strip removes a
/// predicate from a complex AND/OR/parenthesized WHERE leaving the rest valid, and multi-value injection
/// adds a single IN predicate. Policy inputs are passed in as closures so these mirror the regex versions'
/// decisions while only the mechanism is tested here.
/// </summary>
public class SqlAstRepairsTests
{
    // Label NAME-shape and status NAME-shape the production adapters use (kept local so the test pins the
    // exact contract independently of the private regex fields in DirectAnalystPath).
    private static bool IsLabel(string col) =>
        col.Contains("Name", StringComparison.OrdinalIgnoreCase)
        || col.Contains("Title", StringComparison.OrdinalIgnoreCase)
        || col.Contains("Label", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusColumn(string col) =>
        col.EndsWith("Status", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("State", StringComparison.OrdinalIgnoreCase);

    // ── Pass 1: GROUP-BY grain ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Grain_AddsPk_AndPreservesHavingBoundary()
    {
        // The motivating bug: rebuilding GROUP BY must NOT glue "pm.NameEn" to "HAVING". The AST grammar
        // re-serializes, so a separator is guaranteed — there is no string concatenation to get wrong.
        var sql = "SELECT pm.NameEn, COUNT(p.Id) FROM Payments p JOIN PaymentMethods pm ON p.PaymentMethodId = pm.Id GROUP BY pm.NameEn HAVING COUNT(p.Id) > 100";
        var ok = SqlAstRepairs.TryFixGroupByGrain(
            sql, IsLabel, _ => ("pm", "Id"), out var r);
        Assert.True(ok);
        Assert.DoesNotContain("NameEnHAVING", r, StringComparison.OrdinalIgnoreCase);
        // The PK is the FIRST grouping key and the original label survives.
        Assert.Contains("pm.Id", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pm.NameEn", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", r, StringComparison.OrdinalIgnoreCase);
        // Re-parses clean (gluing-proof by construction).
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
    }

    [Fact]
    public void Grain_PreservesOrderByBoundary()
    {
        var sql = "SELECT pm.NameEn, COUNT(*) FROM Payments p JOIN PaymentMethods pm ON p.PaymentMethodId = pm.Id GROUP BY pm.NameEn ORDER BY COUNT(*) DESC";
        var ok = SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => ("pm", "Id"), out var r);
        Assert.True(ok);
        Assert.DoesNotContain("NameEnORDER", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pm.Id", r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grain_NoAggregate_NoChange()
    {
        // No aggregate in the SELECT → the grain fix never fires (matches the regex aggregate guard).
        var sql = "SELECT r.NameEn FROM Regions r GROUP BY r.NameEn";
        Assert.False(SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => ("r", "Id"), out var r));
        Assert.Equal(sql, r);
    }

    [Fact]
    public void Grain_NonLabelColumn_NoChange()
    {
        // A function/date grouping is not a label column → untouched.
        var sql = "SELECT YEAR(CreatedAt), COUNT(*) FROM Tickets GROUP BY YEAR(CreatedAt)";
        Assert.False(SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => ("Tickets", "Id"), out _));
    }

    [Fact]
    public void Grain_AlreadyGroupingByKey_NoChange()
    {
        var sql = "SELECT c.Id, SUM(b.Amount) FROM Bills b JOIN Customers c ON b.CustomerId = c.Id GROUP BY c.Id";
        Assert.False(SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => ("c", "Id"), out _));
    }

    [Fact]
    public void Grain_MultiColumnGroupBy_NoChange()
    {
        // More than one grouping column → conservative skip (single label-only grouping is the bug shape).
        var sql = "SELECT c.NameEn, c.RegionId, SUM(b.Amount) FROM Bills b JOIN Customers c ON b.CustomerId = c.Id GROUP BY c.NameEn, c.RegionId";
        Assert.False(SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => ("c", "Id"), out _));
    }

    [Fact]
    public void Grain_NoPkOwner_NoChange()
    {
        // resolvePk returns null (no unambiguous single-column PK owner) → no change.
        var sql = "SELECT c.NameEn, COUNT(*) FROM Customers c GROUP BY c.NameEn";
        Assert.False(SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, _ => null, out _));
    }

    // ── Pass 2: status predicate strip ──────────────────────────────────────────────────────────

    [Fact]
    public void Strip_RemovesStatus_FromComplexAndOrParenWhere()
    {
        // A WHERE the regex string-strip can't safely touch: AND + OR + parentheses. The AST removes the
        // status leaf and the grammar re-folds the remaining AND/OR tree into valid SQL.
        var sql = "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId = s.Id " +
                  "WHERE (b.Amount > 100 OR b.Amount < 10) AND s.Status = 'Paid' AND b.RegionId = 5";
        var ok = SqlAstRepairs.TryStripUnrequestedStatusFilter(
            sql, IsStatusColumn, _ => true /* everything unrequested */, out var r);
        Assert.True(ok);
        Assert.DoesNotContain("'Paid'", r);
        // The other predicates survive, still valid.
        Assert.Contains("Amount", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RegionId", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OR", r, StringComparison.OrdinalIgnoreCase);   // the OR group is intact
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
    }

    [Fact]
    public void Strip_SolePredicate_DropsWhereEntirely()
    {
        // WHERE has ONLY the status predicate → the whole WHERE clause is removed cleanly (no dangling WHERE).
        var sql = "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId = s.Id WHERE s.Status = 'Paid'";
        var ok = SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, _ => true, out var r);
        Assert.True(ok);
        Assert.DoesNotContain("WHERE", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'Paid'", r);
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
    }

    [Fact]
    public void Strip_KeepsGroundedStatus()
    {
        // shouldStrip=false (the literal is grounded / requested) → nothing is removed.
        var sql = "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId = s.Id WHERE s.Status = 'Overdue'";
        Assert.False(SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, lit => lit != "Overdue", out var r));
        Assert.Equal(sql, r);
    }

    [Fact]
    public void Strip_IgnoresNonStatusColumn()
    {
        // A non-status column equality is never matched (NAME-shape gate).
        var sql = "SELECT COUNT(*) FROM Bills b WHERE b.RegionName = 'Damascus'";
        Assert.False(SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, _ => true, out _));
    }

    [Fact]
    public void Strip_HandlesInequality()
    {
        // != / <> are stripped too (the over-filter goes both directions: Status != 'Churned').
        var sql = "SELECT COUNT(*) FROM Customers c JOIN CustomerStates s ON c.StateId = s.Id WHERE s.State <> 'Churned'";
        var ok = SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, _ => true, out var r);
        Assert.True(ok);
        Assert.DoesNotContain("Churned", r);
    }

    [Fact]
    public void Strip_RemovesFromHavingToo()
    {
        var sql = "SELECT s.Status, COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId = s.Id " +
                  "GROUP BY s.Status HAVING s.Status = 'Paid'";
        var ok = SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, _ => true, out var r);
        Assert.True(ok);
        Assert.DoesNotContain("'Paid'", r);
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
    }

    // ── Pass 3: multi-value injection ───────────────────────────────────────────────────────────

    [Fact]
    public void Inject_AddsSingleInPredicate_ForTwoValues()
    {
        // Two values on one (table,column) → a single IN, ANDed into the existing WHERE.
        var sql = "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id WHERE t.IsDeleted = 0";
        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Regions", "NameEn", new[] { "Damascus", "Aleppo" }),
        };
        var ok = SqlAstRepairs.TryInjectGroundedValueFilters(
            sql, groups, table => table == "Regions" ? "r" : null, out var r);
        Assert.True(ok);
        Assert.Contains("IN", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Damascus", r);
        Assert.Contains("Aleppo", r);
        Assert.Contains("IsDeleted", r, StringComparison.OrdinalIgnoreCase);   // existing predicate preserved
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
        // Exactly ONE IN predicate (not two equalities ANDed to empty).
        int inCount = System.Text.RegularExpressions.Regex.Matches(r, @"\bIN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        Assert.Equal(1, inCount);
    }

    [Fact]
    public void Inject_CreatesWhere_WhenAbsent()
    {
        var sql = "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id";
        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Regions", "NameEn", new[] { "Damascus", "Aleppo" }),
        };
        var ok = SqlAstRepairs.TryInjectGroundedValueFilters(sql, groups, _ => "r", out var r);
        Assert.True(ok);
        Assert.Contains("WHERE", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IN", r, StringComparison.OrdinalIgnoreCase);
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);
    }

    [Fact]
    public void Inject_SingleValue_NoChange()
    {
        // A single value is NOT this pass's job (the equality path / regex handles it).
        var sql = "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id";
        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Regions", "NameEn", new[] { "Damascus" }),
        };
        Assert.False(SqlAstRepairs.TryInjectGroundedValueFilters(sql, groups, _ => "r", out var r));
        Assert.Equal(sql, r);
    }

    [Fact]
    public void Inject_TableNotInQuery_Skipped()
    {
        var sql = "SELECT COUNT(*) FROM Tickets t";
        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Regions", "NameEn", new[] { "Damascus", "Aleppo" }),
        };
        Assert.False(SqlAstRepairs.TryInjectGroundedValueFilters(sql, groups, _ => null /* not in query */, out _));
    }

    [Fact]
    public void Inject_EscapesQuoteInValue()
    {
        // O'Brien must round-trip to a valid doubled-quote literal via the grammar (no manual escaping).
        var sql = "SELECT COUNT(*) FROM Customers c";
        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Customers", "FullNameEn", new[] { "O'Brien", "O'Neil" }),
        };
        var ok = SqlAstRepairs.TryInjectGroundedValueFilters(sql, groups, _ => "c", out var r);
        Assert.True(ok);
        SqlAstService.Parse(r, out var errs);
        Assert.Empty(errs);            // valid T-SQL despite the apostrophes
        Assert.Contains("O''Brien", r);
    }
}
