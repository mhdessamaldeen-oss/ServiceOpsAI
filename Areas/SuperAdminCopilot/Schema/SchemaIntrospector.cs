namespace SuperAdminCopilot.Schema;

using System.Data;

public interface ISchemaIntrospector
{
    SchemaSnapshot Introspect();
}

internal sealed class SchemaIntrospector : ISchemaIntrospector
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SchemaIntrospector(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public SchemaSnapshot Introspect()
    {
        using var conn = _connectionFactory.Open();
        return new SchemaSnapshot
        {
            Tables = ReadTables(conn).ToList(),
            Columns = ReadColumns(conn).ToList(),
            ForeignKeys = ReadForeignKeys(conn).ToList(),
            KeyConstraints = ReadKeyConstraints(conn).Concat(ReadUniqueIndexes(conn)).ToList(),
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private static IEnumerable<KeyConstraintInfo> ReadKeyConstraints(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE, tc.TABLE_SCHEMA, tc.TABLE_NAME,
       kcu.COLUMN_NAME, kcu.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
 AND tc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA
WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new KeyConstraintInfo(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetInt32(5));
        }
    }

    // EF Core typically creates UNIQUE INDEXES, not UNIQUE CONSTRAINTS — so a column like
    // Tickets.TicketNumber or AspNetUsers.NormalizedEmail isn't visible via
    // INFORMATION_SCHEMA.TABLE_CONSTRAINTS. We hit sys.indexes to catch them. Reported with
    // ConstraintType="UNIQUE" so downstream natural-key detection treats them uniformly.
    private static IEnumerable<KeyConstraintInfo> ReadUniqueIndexes(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT i.name, 'UNIQUE',
       SCHEMA_NAME(t.schema_id), t.name,
       c.name, ic.key_ordinal
FROM sys.indexes i
JOIN sys.tables t          ON i.object_id = t.object_id
JOIN sys.index_columns ic  ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c         ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.is_unique = 1
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND ic.is_included_column = 0
  -- Note: filtered unique indexes (has_filter=1) are kept. EF Identity creates
  -- 'WHERE [NormalizedName] IS NOT NULL' filters on its uniqueness indexes and we
  -- want those to count as natural keys.
ORDER BY SCHEMA_NAME(t.schema_id), t.name, i.name, ic.key_ordinal;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new KeyConstraintInfo(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetByte(5));
        }
    }

    private static IEnumerable<TableInfo> ReadTables(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME";
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return new TableInfo(r.GetString(0), r.GetString(1));
    }

    private static IEnumerable<ColumnInfo> ReadColumns(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, " +
            "       CHARACTER_MAXIMUM_LENGTH, ORDINAL_POSITION " +
            "FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new ColumnInfo(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.GetInt32(6));
        }
    }

    private static IEnumerable<ForeignKeyInfo> ReadForeignKeys(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT fk.name, sp.name, tp.name, cp.name, sr.name, tr.name, cr.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables    tp ON fkc.parent_object_id = tp.object_id
JOIN sys.schemas   sp ON tp.schema_id = sp.schema_id
JOIN sys.columns   cp ON fkc.parent_object_id = cp.object_id  AND fkc.parent_column_id = cp.column_id
JOIN sys.tables    tr ON fkc.referenced_object_id = tr.object_id
JOIN sys.schemas   sr ON tr.schema_id = sr.schema_id
JOIN sys.columns   cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
ORDER BY sp.name, tp.name, fk.name, fkc.constraint_column_id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new ForeignKeyInfo(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6));
        }
    }
}
