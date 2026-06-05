namespace AnalystAgent.Tests.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using AnalystAgent.Pipeline;
using AnalystAgent.Schema;
using AnalystAgent.Validation;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

/// <summary>
/// STAGE-2 EQUIVALENCE proof — for a set of representative SQL inputs, the AST repair output
/// (<see cref="SqlAstRepairs"/>) is SEMANTICALLY EQUAL to the regex repair output
/// (<c>DirectAnalystPath.Try*</c>): same normalized canonical SQL (grain) or same predicate set
/// (strip / multi-value inject). This is what makes flipping <c>EnableAstRepairs</c> safe — the two
/// mechanisms produce equivalent SQL on the shapes the repairs target. The closures the AST passes
/// receive here mirror the production adapters' policy (same label/status NAME-shape, same getTable
/// owner/PK resolution, same grounded-keep decision).
/// </summary>
public class SqlAstRepairEquivalenceTests
{
    // ── shared schema fixtures (mirror BilingualProjectionRepairTests' helpers) ──────────────────
    private static InferredTable TableWithPk(string name, string label, string pk, params string[] cols)
    {
        var t = new InferredTable { Name = name, Schema = "dbo" };
        t.Roles.LabelColumn = label;
        foreach (var c in cols) t.Columns.Add(new InferredColumn { Name = c, Type = "nvarchar" });
        t.PrimaryKey.Add(pk);
        return t;
    }

    private static Func<string, InferredTable?> Lookup(params InferredTable[] tables)
    {
        var d = new Dictionary<string, InferredTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables) d[t.Name] = t;
        return n => d.TryGetValue(n, out var t) ? t : null;
    }

    // Canonical normalization: parse + render both candidates through the SAME generator so two
    // semantically-equal trees compare equal regardless of whitespace/casing/bracketing.
    private static string Norm(string sql)
    {
        var f = SqlAstService.Parse(sql, out var errs);
        Assert.True(errs.Count == 0, "must parse for normalization: " + string.Join("; ", errs.Select(e => e.Message)) + "\nSQL: " + sql);
        return SqlAstService.Render(f!);
    }

    // The set of quoted string literals appearing in predicate equality/IN positions — a mechanism-
    // independent "predicate value set" for comparing strip/inject outputs.
    private static HashSet<string> LiteralSet(string sql)
    {
        var f = SqlAstService.Parse(sql, out var errs);
        Assert.True(errs.Count == 0, "must parse: " + sql);
        var v = new LiteralCollector();
        f!.Accept(v);
        return v.Literals;
    }

    private sealed class LiteralCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Literals { get; } = new(StringComparer.Ordinal);
        public override void Visit(StringLiteral node) => Literals.Add(node.Value);
    }

    // ── closures replicating the production AST adapters' policy ──────────────────────────────────
    private static bool IsLabel(string col) =>
        col.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0
        || col.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0
        || col.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsStatusColumn(string col) =>
        col.EndsWith("Status", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("State", StringComparison.OrdinalIgnoreCase);

    // ── GRAIN equivalence ────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> GrainCases() => new List<object[]>
    {
        new object[] { "SELECT TOP 5 Customers.FullNameEn, SUM(b.TotalAmount) FROM Bills b JOIN Customers ON b.CustomerId=Customers.Id GROUP BY Customers.FullNameEn ORDER BY SUM(b.TotalAmount) DESC" },
        new object[] { "SELECT d.NameEn, COUNT(*) FROM Tickets t JOIN Departments d ON t.DepartmentId=d.Id GROUP BY d.NameEn ORDER BY COUNT(*) DESC" },
        new object[] { "SELECT pm.NameEn, COUNT(p.Id) FROM Payments p JOIN PaymentMethods pm ON p.PaymentMethodId=pm.Id GROUP BY pm.NameEn HAVING COUNT(p.Id) > 100" },
    };

    [Theory]
    [MemberData(nameof(GrainCases))]
    public void Grain_AstEquivalentToRegex(string sql)
    {
        var getTable = Lookup(
            TableWithPk("Customers", "FullNameEn", "Id", "Id", "FullNameEn"),
            TableWithPk("Departments", "NameEn", "Id", "Id", "NameEn"),
            TableWithPk("PaymentMethods", "NameEn", "Id", "Id", "NameEn"));

        var regexOk = DirectAnalystPath.TryFixGroupByGrain(sql, getTable, out var regexOut);

        // Build the same resolvePk closure the production adapter builds (qualifier→table via the alias map,
        // else single owner with the column; single-column PK).
        var aliasMap = BuildAliasToTableMapViaRegexParity(sql);
        (string, string)? ResolvePk(ColumnReferenceExpression colRef)
        {
            var ids = colRef.MultiPartIdentifier?.Identifiers;
            if (ids is not { Count: > 0 }) return null;
            var col = ids[^1].Value;
            var qual = ids.Count > 1 ? ids[^2].Value : null;
            string? table = null;
            if (!string.IsNullOrEmpty(qual) && aliasMap.TryGetValue(qual, out var tq)) table = tq;
            else
            {
                var owners = aliasMap.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(tn => { var ti = getTable(tn); return ti is not null && ti.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)); })
                    .ToList();
                if (owners.Count == 1) table = owners[0];
            }
            if (table is null) return null;
            var t = getTable(table);
            if (t?.PrimaryKey is not { Count: 1 } pk) return null;
            return (string.IsNullOrEmpty(qual) ? table : qual!, pk[0]);
        }
        var astOk = SqlAstRepairs.TryFixGroupByGrain(sql, IsLabel, ResolvePk, out var astOut);

        Assert.Equal(regexOk, astOk);
        Assert.True(astOk, "both should fix grain on these cases");
        // Same canonical SQL — flipping the flag is safe.
        Assert.Equal(Norm(regexOut), Norm(astOut));
    }

    // ── STATUS-STRIP equivalence ─────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> StripCases() => new List<object[]>
    {
        // sql, question (empty → trust grounding only), grounded values
        new object[] { "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId=s.Id WHERE s.Status = 'Paid'", "", new string[0] },
        new object[] { "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId=s.Id WHERE b.RegionId = 5 AND s.Status = 'Paid'", "", new string[0] },
        new object[] { "SELECT COUNT(*) FROM Customers c JOIN CustomerStates s ON c.StateId=s.Id WHERE s.State <> 'Churned'", "", new string[0] },
    };

    [Theory]
    [MemberData(nameof(StripCases))]
    public void Strip_AstEquivalentToRegex(string sql, string question, string[] grounded)
    {
        // Regex version (trust-grounding-only true, matching the production default).
        var regexOk = DirectAnalystPath.TryStripUnrequestedStatusFilter(
            sql, question, grounded, out var regexOut, trustGroundingOnly: true);

        // AST version with the SAME keep/strip policy.
        var groundedSet = new HashSet<string>(grounded, StringComparer.OrdinalIgnoreCase);
        bool ShouldStrip(string lit) => !groundedSet.Contains(lit);   // trust-grounding-only: question text ignored
        var astOk = SqlAstRepairs.TryStripUnrequestedStatusFilter(sql, IsStatusColumn, ShouldStrip, out var astOut);

        Assert.Equal(regexOk, astOk);
        Assert.True(astOk);
        // Same predicate VALUE set (the stripped literal is gone from both; the rest survive).
        Assert.Equal(LiteralSet(regexOut), LiteralSet(astOut));
        // Both re-parse clean.
        SqlAstService.Parse(regexOut, out var e1); Assert.Empty(e1);
        SqlAstService.Parse(astOut, out var e2); Assert.Empty(e2);
    }

    [Fact]
    public void Strip_AstEquivalentToRegex_KeepsGrounded()
    {
        // Grounded literal → NEITHER mechanism strips it (both return false, output unchanged).
        var sql = "SELECT COUNT(*) FROM Bills b JOIN BillStatuses s ON b.StatusId=s.Id WHERE s.Status = 'Overdue'";
        var regexOk = DirectAnalystPath.TryStripUnrequestedStatusFilter(
            sql, "", new[] { "Overdue" }, out var regexOut, trustGroundingOnly: true);
        var astOk = SqlAstRepairs.TryStripUnrequestedStatusFilter(
            sql, IsStatusColumn, lit => lit != "Overdue", out var astOut);
        Assert.False(regexOk);
        Assert.False(astOk);
        Assert.Equal(Norm(sql), Norm(regexOut));
        Assert.Equal(Norm(sql), Norm(astOut));
    }

    // ── MULTI-VALUE INJECTION equivalence ────────────────────────────────────────────────────────

    [Fact]
    public void MultiValueInject_AstEquivalentToRegex_PredicateSet()
    {
        // Two values on one column → both mechanisms produce a single col IN ('a','b') ANDed in.
        var sql = "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id WHERE t.IsDeleted = 0";
        var linked = new List<(string Table, string Column, string Value)>
        {
            ("Regions", "NameEn", "Damascus"),
            ("Regions", "NameEn", "Aleppo"),
        };

        var regexOk = DirectAnalystPath.TryInjectGroundedValueFilters(sql, linked, out var regexOut);

        var groups = new List<(string, string, IReadOnlyList<string>)>
        {
            ("Regions", "NameEn", new[] { "Damascus", "Aleppo" }),
        };
        var astOk = SqlAstRepairs.TryInjectGroundedValueFilters(
            sql, groups, table => table == "Regions" ? "r" : null, out var astOut);

        Assert.True(regexOk);
        Assert.True(astOk);
        // Same predicate VALUE set (both injected Damascus + Aleppo; IsDeleted=0 has no string literal).
        Assert.Equal(LiteralSet(regexOut), LiteralSet(astOut));
        // Both produce exactly ONE IN predicate (no AND-to-empty equality pair).
        Assert.Equal(InCount(regexOut), InCount(astOut));
        Assert.Equal(1, InCount(astOut));
        SqlAstService.Parse(regexOut, out var e1); Assert.Empty(e1);
        SqlAstService.Parse(astOut, out var e2); Assert.Empty(e2);
    }

    private static int InCount(string sql) =>
        System.Text.RegularExpressions.Regex.Matches(
            sql, @"\bIN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

    // ── flag-OFF safety pin ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnableAstRepairs_DefaultsOff()
    {
        // The C# default MUST be false so the regex path runs unchanged (byte-identical) until an operator
        // opts in. If this ever flips, every existing test's behavior could silently change.
        Assert.False(new AnalystAgent.Configuration.AnalystOptions().EnableAstRepairs);
    }

    // Minimal alias→table map mirroring DirectAnalystPath.BuildAliasToTableMap for the grain closure.
    private static Dictionary<string, string> BuildAliasToTableMapViaRegexParity(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rx = new System.Text.RegularExpressions.Regex(
            @"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(?!(?:INNER|LEFT|RIGHT|FULL|OUTER|CROSS|JOIN|ON|WHERE|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|PIVOT|UNPIVOT)\b)(\w+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(sql))
        {
            var table = m.Groups[1].Value;
            if (string.IsNullOrEmpty(table)) continue;
            map[table] = table;
            var alias = m.Groups[2].Value;
            if (!string.IsNullOrEmpty(alias)) map[alias] = table;
        }
        return map;
    }
}
