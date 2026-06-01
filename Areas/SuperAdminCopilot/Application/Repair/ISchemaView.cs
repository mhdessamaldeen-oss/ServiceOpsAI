namespace SuperAdminCopilot.Application.Repair.Schema;

using System.Collections.Generic;

/// <summary>
/// Read-only view of the catalog repair rules consult. Wraps the v2 <c>IEntityCatalog</c> and
/// the FK graph so rule code never reaches into infra directly.
/// </summary>
public interface ISchemaView
{
    bool TableExists(string table);
    bool ColumnExists(string table, string column);
    IReadOnlyList<string> ColumnsOf(string table);
    IReadOnlyList<ForeignKeyEdge> ForeignKeysFrom(string parentTable);
    IReadOnlyList<ForeignKeyEdge> ForeignKeysTo(string referencedTable);

    /// <summary>SQL data type for a (table, column) pair (e.g. "nvarchar(100)", "decimal(18,2)",
    /// "int", "datetime2"). Returns null when the column is unknown. Used by type-aware repair
    /// rules to reject e.g. AVG over an nvarchar identifier column.</summary>
    string? ColumnType(string table, string column);

    /// <summary>True when the column's underlying SQL type is numeric — int / decimal / float /
    /// money / numeric / smallint / tinyint / bigint / real. False for text, date, binary, etc.
    /// False when the column or table is unknown.</summary>
    bool IsNumericColumn(string table, string column);
}

public sealed record ForeignKeyEdge(
    string ParentTable,
    string ParentColumn,
    string ReferencedTable,
    string ReferencedColumn);
