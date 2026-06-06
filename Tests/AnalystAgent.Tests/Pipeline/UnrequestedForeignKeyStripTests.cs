namespace AnalystAgent.Tests.Pipeline;

using System;
using System.Collections.Generic;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline;
using AnalystAgent.Schema;
using AnalystAgent.Models;
using Xunit;

/// <summary>
/// Deterministic unrequested foreign-key-filter strip
/// (<see cref="DirectAnalystPath.TryStripUnrequestedForeignKeyFilter"/>): the integer-FK twin of the
/// status/flag strips. The 7B bolts an UNREQUESTED, UNGROUNDED bare-integer FK equality onto a query
/// because it associates a word with a category — "how many blackouts in total" came back as
/// <c>WHERE ServiceTypeId = 1</c> (electricity only, 8) instead of all outages (32). The status/flag
/// strips require a quoted/boolean literal and so deliberately skip a bare int; this closes that gap.
///
/// The rule keys off STRUCTURE (the column is an FK per schema) + GROUNDING, with NO hardcoded
/// table/column/value vocabulary — the only inputs are schema metadata (column Role / References) and
/// the grounding bindings. The tests below build a throwaway schema (Outages with an FK ServiceTypeId →
/// ServiceTypes, an FK RegionId → Regions, a PK Id, and a plain int SomeIntCol) to prove the KEEP/STRIP
/// branches generalize to any FK on any table.
/// </summary>
public sealed class UnrequestedForeignKeyStripTests
{
    // ── schema builders (no production vocabulary; arbitrary names prove generality) ──

    private static InferredColumn Pk(string name) =>
        new() { Name = name, Type = "int", Role = SpecConstants.ColumnRoles.PrimaryKey };

    private static InferredColumn Fk(string name, string references) =>
        new() { Name = name, Type = "int", Role = SpecConstants.ColumnRoles.ForeignKey, References = references };

    private static InferredColumn Plain(string name, string type = "int") =>
        new() { Name = name, Type = type };

    private static InferredTable Table(string name, string pk, params InferredColumn[] cols)
    {
        var t = new InferredTable { Name = name, Schema = "dbo" };
        t.PrimaryKey.Add(pk);
        foreach (var c in cols) t.Columns.Add(c);
        return t;
    }

    private static Func<string, InferredTable?> Lookup(params InferredTable[] tables)
    {
        var d = new Dictionary<string, InferredTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables) d[t.Name] = t;
        return n => d.TryGetValue(n, out var t) ? t : null;
    }

    // The fact table under test: Id (PK), ServiceTypeId (FK→ServiceTypes), RegionId (FK→Regions),
    // Severity (label), SomeIntCol (plain int, NOT an FK).
    private static Func<string, InferredTable?> OutagesSchema() => Lookup(
        Table("Outages", "Id",
            Pk("Id"),
            Fk("ServiceTypeId", "ServiceTypes.Id"),
            Fk("RegionId", "Regions.Id"),
            Plain("Severity", "nvarchar"),
            Plain("SomeIntCol")));

    private static readonly string[] OutagesOnly = { "Outages" };

    private static readonly (string Table, string Column)[] NoGroundedCols = Array.Empty<(string, string)>();
    private static readonly string[] NoGroundedTables = Array.Empty<string>();

    // ── 1. STRIP: the confirmed live bug ──
    [Fact]
    public void Strips_UngroundedUnnamedForeignKeyEquality()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1",
            "how many blackouts in total", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(*) FROM Outages", repaired);
    }

    // The COUNT(Id) form must be untouched in the SELECT yet still stripped in the WHERE.
    [Fact]
    public void Strips_AndLeavesCountIdProjectionIntact()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(Id) FROM Outages WHERE ServiceTypeId = 1",
            "how many blackouts in total", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(Id) FROM Outages", repaired);
    }

    // ── 2. KEEP via grounded column ──
    [Fact]
    public void Keeps_WhenGroundingBoundTheForeignKeyColumn()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1",
            "how many electricity outages", OutagesSchema(), OutagesOnly,
            new[] { ("Outages", "ServiceTypeId") }, NoGroundedTables, out _);

        Assert.False(changed);
    }

    // ── 3. KEEP via grounded REFERENCED table ──
    [Fact]
    public void Keeps_WhenGroundingBoundTheReferencedTable()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1",
            "how many outages for that service type", OutagesSchema(), OutagesOnly,
            NoGroundedCols, new[] { "ServiceTypes" }, out _);

        Assert.False(changed);
    }

    // ── 4. KEEP via digit named in the question ──
    [Fact]
    public void Keeps_WhenQuestionNamesTheIntegerToken()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE RegionId = 5",
            "outages in region 5", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out _);

        Assert.False(changed);
    }

    // ── 5. KEEP a non-FK integer column ──
    [Fact]
    public void Keeps_WhenColumnIsNotAForeignKey()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE SomeIntCol = 1",
            "how many blackouts in total", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out _);

        Assert.False(changed);
    }

    // ── 6. NEVER strip the primary key ──
    [Fact]
    public void Keeps_PrimaryKeyEqualityAlways()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT * FROM Outages WHERE Id = 5",
            "show me an outage", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out _);

        Assert.False(changed);
    }

    // ── 7. Compound WHERE: drop only the FK predicate, keep the rest, stay valid ──
    [Fact]
    public void Compound_DropsOnlyForeignKeyPredicate_KeepsOtherFilter()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE Severity = 'Critical' AND ServiceTypeId = 1",
            "how many critical blackouts", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(*) FROM Outages WHERE Severity = 'Critical'", repaired);
    }

    // FK predicate in the LEADING position (WHERE <fk> AND <other>) → keep WHERE, drop the FK, keep the other.
    [Fact]
    public void Compound_LeadingForeignKeyPredicate_IsDropped()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1 AND Severity = 'Critical'",
            "how many critical blackouts", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(*) FROM Outages WHERE Severity = 'Critical'", repaired);
    }

    // Qualified column form (alias) is still resolved by column name across the linked tables and stripped.
    [Fact]
    public void Strips_QualifiedForeignKeyEquality()
    {
        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages o WHERE o.ServiceTypeId = 1",
            "how many blackouts in total", OutagesSchema(), OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(*) FROM Outages o", repaired);
    }

    // FK detected via References alone (no explicit foreign_key role) — proves the role-OR-References rule.
    [Fact]
    public void Strips_WhenForeignKeyIdentifiedByReferencesOnly()
    {
        var t = new InferredTable { Name = "Outages", Schema = "dbo" };
        t.PrimaryKey.Add("Id");
        t.Columns.Add(new InferredColumn { Name = "Id", Type = "int", Role = SpecConstants.ColumnRoles.PrimaryKey });
        t.Columns.Add(new InferredColumn { Name = "ServiceTypeId", Type = "int", References = "ServiceTypes.Id" });
        var schema = Lookup(t);

        var changed = DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
            "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1",
            "how many blackouts in total", schema, OutagesOnly,
            NoGroundedCols, NoGroundedTables, out var repaired);

        Assert.True(changed);
        Assert.Equal("SELECT COUNT(*) FROM Outages", repaired);
    }

    // ── 8. Flag-off: the wired guard (StripUnrequestedForeignKeyFilter && TryStrip...) short-circuits, so the
    //    method is never invoked and the SQL is unchanged. We assert the default-ON posture and replay the
    //    exact wired boolean expression with the option OFF to prove the strip does not run. ──
    [Fact]
    public void Option_DefaultsToOn()
    {
        Assert.True(new AnalystOptions().StripUnrequestedForeignKeyFilter);
    }

    [Fact]
    public void WiredGuard_WhenFlagOff_DoesNotStrip()
    {
        var options = new AnalystOptions { StripUnrequestedForeignKeyFilter = false };
        const string sql = "SELECT COUNT(*) FROM Outages WHERE ServiceTypeId = 1";
        var repaired = sql;   // AcceptIfParses keeps `current` when `changed` is false

        // This is the exact wired expression from EmitRepairValidateExecuteOnce: the && short-circuits on the
        // flag, so TryStripUnrequestedForeignKeyFilter never runs.
        var changed = options.StripUnrequestedForeignKeyFilter &&
            DirectAnalystPath.TryStripUnrequestedForeignKeyFilter(
                sql, "how many blackouts in total", OutagesSchema(), OutagesOnly,
                NoGroundedCols, NoGroundedTables, out repaired);

        Assert.False(changed);
        Assert.Equal(sql, repaired);   // untouched
    }
}
