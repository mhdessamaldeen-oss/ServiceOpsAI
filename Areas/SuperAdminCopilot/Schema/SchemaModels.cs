namespace SuperAdminCopilot.Schema;

public sealed record TableInfo(string Schema, string Name)
{
    public string FullName => $"{Schema}.{Name}";
}

public sealed record ColumnInfo(
    string TableSchema,
    string TableName,
    string ColumnName,
    string DataType,
    bool IsNullable,
    int? MaxLength,
    int OrdinalPosition);

public sealed record ForeignKeyInfo(
    string ConstraintName,
    string ParentSchema,
    string ParentTable,
    string ParentColumn,
    string ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumn);

/// <summary>
/// One row of a PRIMARY KEY or UNIQUE constraint. Multi-column constraints emit multiple rows
/// (one per column) with shared <see cref="ConstraintName"/> and ascending <see cref="OrdinalPosition"/>.
/// </summary>
public sealed record KeyConstraintInfo(
    string ConstraintName,
    string ConstraintType,   // "PRIMARY KEY" or "UNIQUE"
    string TableSchema,
    string TableName,
    string ColumnName,
    int OrdinalPosition);

public sealed class SchemaSnapshot
{
    public required IReadOnlyList<TableInfo> Tables { get; init; }
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public required IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<KeyConstraintInfo> KeyConstraints { get; init; } = Array.Empty<KeyConstraintInfo>();

    public IEnumerable<ColumnInfo> ColumnsOf(string tableName) =>
        Columns.Where(c => string.Equals(c.TableName, tableName, StringComparison.OrdinalIgnoreCase))
               .OrderBy(c => c.OrdinalPosition);
}
