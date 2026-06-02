namespace SuperAdminCopilot.Tests.Compilation;

using System;
using System.Linq;
using Microsoft.Extensions.Options;
using Moq;
using SuperAdminCopilot.Compilation;
using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using Xunit;

/// <summary>
/// Pins the 2026-06-02 FK-join fix in <see cref="SqlCompiler"/>. The ON clause used to hardcode the
/// joined (aliased) table on the FK's REFERENCED side, which is correct only for child→parent lookup
/// joins (root=Tickets → referenced Regions). For a parent→child fan-out (root=Customers joins the
/// referencing Bills, FK Bills.CustomerId → Customers.Id) it produced the self-referential
/// `[Bills].[CustomerId] = [Bills].[Id]` instead of `= [Customers].[Id]` — silently wrong rows on
/// every aggregate/top-N over a one-to-many relationship. This was the dominant failure in the
/// 2026-06-02 local-model assessment.
/// </summary>
public sealed class SqlCompilerJoinTests
{
    // Two tables + one FK: Bills.CustomerId → Customers.Id (Bills is the referencing child).
    private static SqlCompiler Build()
    {
        var snapshot = new SchemaSnapshot
        {
            Tables = new[] { new TableInfo("dbo", "Customers"), new TableInfo("dbo", "Bills") },
            Columns = new[]
            {
                new ColumnInfo("dbo", "Customers", "Id",          "int",     false, null, 1),
                new ColumnInfo("dbo", "Customers", "FullNameEn",  "nvarchar", true, 200,  2),
                new ColumnInfo("dbo", "Bills",     "Id",          "int",     false, null, 1),
                new ColumnInfo("dbo", "Bills",     "CustomerId",  "int",     false, null, 2),
                new ColumnInfo("dbo", "Bills",     "TotalAmount", "decimal", false, null, 3),
            },
            ForeignKeys = new[]
            {
                new ForeignKeyInfo("FK_Bills_Customers", "dbo", "Bills", "CustomerId", "dbo", "Customers", "Id"),
            },
            CapturedAt = DateTimeOffset.UtcNow,
        };

        var catalog = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalog.SetupGet(c => c.Snapshot).Returns(snapshot);
        catalog.SetupGet(c => c.Graph).Returns(new ForeignKeyGraph(snapshot));
        catalog.Setup(c => c.TableExists(It.IsAny<string>()))
            .Returns<string>(t => snapshot.Tables.Any(ti => string.Equals(ti.Name, t, StringComparison.OrdinalIgnoreCase)));
        catalog.Setup(c => c.ColumnExists(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((t, c) => snapshot.Columns.Any(ci =>
                string.Equals(ci.TableName, t, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ci.ColumnName, c, StringComparison.OrdinalIgnoreCase)));
        catalog.Setup(c => c.GetColumns(It.IsAny<string>()))
            .Returns<string>(t => snapshot.Columns
                .Where(ci => string.Equals(ci.TableName, t, StringComparison.OrdinalIgnoreCase)).ToList());

        var semantic = new Mock<ISemanticLayer>(MockBehavior.Loose);
        semantic.SetupGet(s => s.Config).Returns(new SemanticLayerConfig());
        semantic.Setup(s => s.ResolveSynonymValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, v) => v);
        semantic.Setup(s => s.GetSensitiveColumns(It.IsAny<string>()))
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var options = Options.Create(new CopilotOptions { MaxRows = 1000 });
        return new SqlCompiler(catalog.Object, new JoinResolver(catalog.Object), semantic.Object, options,
            new MssqlDialect(), new TemporalTokenizer(), new SemanticExpander(semantic.Object),
            new FilterValueRewriter(catalog.Object, semantic.Object));
    }

    [Fact]
    public void ParentToChildJoin_RendersReferencedSideAsRoot_NotSelfReferential()
    {
        // "total billed per customer" — root=Customers (the FK's REFERENCED parent), join the
        // referencing child Bills. This is the direction that used to break.
        var spec = new QuerySpec
        {
            Root = "Customers",
            Select = { "Customers.FullNameEn" },
            Aggregations = { new AggregateSpec { Function = "SUM", Column = "Bills.TotalAmount", Alias = "Total" } },
            GroupBy = { "Customers.FullNameEn" },
            Joins = { new JoinSpec { Table = "Bills", Kind = "inner" } },
        };

        var sql = Build().Compile(spec).Sql;

        Assert.Contains("[Bills].[CustomerId] = [Customers].[Id]", sql);
        Assert.DoesNotContain("[Bills].[CustomerId] = [Bills].[Id]", sql); // the old self-referential bug
    }

    [Fact]
    public void ChildToParentJoin_StillRendersCorrectly_ByteIdenticalDirection()
    {
        // "bills with their customer name" — root=Bills (the referencing child), join referenced
        // Customers. This direction always worked; the fix must not change it.
        var spec = new QuerySpec
        {
            Root = "Bills",
            Select = { "Bills.Id", "Customers.FullNameEn" },
            Joins = { new JoinSpec { Table = "Customers", Kind = "inner" } },
        };

        var sql = Build().Compile(spec).Sql;

        Assert.Contains("[Bills].[CustomerId] = [Customers].[Id]", sql);
    }
}
