namespace SuperAdminCopilot.Infrastructure.Schema;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Schema;                              // v2 IEntityCatalog
using SuperAdminCopilot.Application.Repair.Schema;        // v3 ISchemaView

/// <summary>
/// V3 <see cref="ISchemaView"/> implementation that wraps v2's <see cref="IEntityCatalog"/>.
/// Repair rules see a typed read view; underneath the data still comes from the live catalog.
/// </summary>
internal sealed class SchemaView : ISchemaView
{
    private readonly IEntityCatalog _catalog;

    public SchemaView(IEntityCatalog catalog) { _catalog = catalog; }

    public bool TableExists(string table) => _catalog.TableExists(table);

    public bool ColumnExists(string table, string column) => _catalog.ColumnExists(table, column);

    public IReadOnlyList<string> ColumnsOf(string table)
        => _catalog.GetColumns(table).Select(c => c.ColumnName).ToList();

    public IReadOnlyList<ForeignKeyEdge> ForeignKeysFrom(string parentTable)
        => _catalog.Snapshot.ForeignKeys
            .Where(fk => string.Equals(fk.ParentTable, parentTable, System.StringComparison.OrdinalIgnoreCase))
            .Select(fk => new ForeignKeyEdge(fk.ParentTable, fk.ParentColumn, fk.ReferencedTable, fk.ReferencedColumn))
            .ToList();

    public IReadOnlyList<ForeignKeyEdge> ForeignKeysTo(string referencedTable)
        => _catalog.Snapshot.ForeignKeys
            .Where(fk => string.Equals(fk.ReferencedTable, referencedTable, System.StringComparison.OrdinalIgnoreCase))
            .Select(fk => new ForeignKeyEdge(fk.ParentTable, fk.ParentColumn, fk.ReferencedTable, fk.ReferencedColumn))
            .ToList();

    public string? ColumnType(string table, string column)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) return null;
        var cols = _catalog.GetColumns(table);
        foreach (var c in cols)
        {
            if (string.Equals(c.ColumnName, column, System.StringComparison.OrdinalIgnoreCase))
                return c.DataType;
        }
        return null;
    }

    // Numeric SQL types — covers the SQL Server families. Returning false for unknown columns is
    // intentional: a missing column shouldn't be treated as numeric (would mask real bugs).
    private static readonly System.Collections.Generic.HashSet<string> NumericSqlTypes
        = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "int", "bigint", "smallint", "tinyint",
            "decimal", "numeric", "money", "smallmoney",
            "float", "real", "bit"
        };

    public bool IsNumericColumn(string table, string column)
    {
        var t = ColumnType(table, column);
        if (string.IsNullOrEmpty(t)) return false;
        // Strip parameterised suffix: "decimal(18,2)" → "decimal"
        var paren = t.IndexOf('(');
        var bare = paren > 0 ? t.Substring(0, paren) : t;
        return NumericSqlTypes.Contains(bare.Trim());
    }
}
