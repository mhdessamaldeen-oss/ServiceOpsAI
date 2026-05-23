namespace SuperAdminCopilot.Schema;

using SuperAdminCopilot.Semantic;

public interface ISchemaMetadataMap
{
    IReadOnlyList<SchemaTableMetadata> Tables { get; }
    SchemaTableMetadata? GetTable(string table);
    IReadOnlyList<SchemaColumnMetadata> GetColumns(string table);
    IReadOnlyList<SchemaRelationshipMetadata> GetRelationships(string table);
}

public sealed record SchemaTableMetadata(
    string Schema,
    string Name,
    string FullName,
    EntityDefinition? Department,
    IReadOnlyList<string> Labels);

public sealed record SchemaColumnMetadata(
    string Table,
    string Name,
    string DataType,
    bool IsNullable,
    IReadOnlyList<string> Labels);

public sealed record SchemaRelationshipMetadata(
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    string ConstraintName);

internal sealed class SchemaMetadataMap : ISchemaMetadataMap
{
    private readonly Lazy<IReadOnlyList<SchemaTableMetadata>> _tables;
    private readonly Lazy<IReadOnlyList<SchemaColumnMetadata>> _columns;
    private readonly Lazy<IReadOnlyList<SchemaRelationshipMetadata>> _relationships;

    public SchemaMetadataMap(
        IEntityCatalog catalog,
        ISemanticLayer semantic,
        ICopilotSchemaAccessPolicy accessPolicy)
    {
        _tables = new Lazy<IReadOnlyList<SchemaTableMetadata>>(() =>
            catalog.Snapshot.Tables
                .Where(t => accessPolicy.IsTableAllowed(t.Name) || accessPolicy.IsTableAllowed(t.FullName))
                .Select(t =>
                {
                    var entity = semantic.GetEntityForTable(t.Name);
                    return new SchemaTableMetadata(
                        t.Schema,
                        t.Name,
                        t.FullName,
                        entity,
                        BuildTableLabels(t, entity));
                })
                .ToList());

        _columns = new Lazy<IReadOnlyList<SchemaColumnMetadata>>(() =>
            catalog.Snapshot.Columns
                .Where(c => accessPolicy.IsColumnAllowed(c.TableName, c.ColumnName))
                .Select(c => new SchemaColumnMetadata(
                    c.TableName,
                    c.ColumnName,
                    c.DataType,
                    c.IsNullable,
                    BuildColumnLabels(c)))
                .ToList());

        _relationships = new Lazy<IReadOnlyList<SchemaRelationshipMetadata>>(() =>
            catalog.Snapshot.ForeignKeys
                .Where(fk => accessPolicy.IsTableAllowed(fk.ParentTable)
                    && accessPolicy.IsTableAllowed(fk.ReferencedTable)
                    && accessPolicy.IsColumnAllowed(fk.ParentTable, fk.ParentColumn)
                    && accessPolicy.IsColumnAllowed(fk.ReferencedTable, fk.ReferencedColumn))
                .SelectMany(fk => new[]
                {
                    new SchemaRelationshipMetadata(fk.ParentTable, fk.ParentColumn, fk.ReferencedTable, fk.ReferencedColumn, fk.ConstraintName),
                    new SchemaRelationshipMetadata(fk.ReferencedTable, fk.ReferencedColumn, fk.ParentTable, fk.ParentColumn, fk.ConstraintName),
                })
                .ToList());
    }

    public IReadOnlyList<SchemaTableMetadata> Tables => _tables.Value;

    public SchemaTableMetadata? GetTable(string table) =>
        string.IsNullOrWhiteSpace(table)
            ? null
            : Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(t.FullName, table, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SchemaColumnMetadata> GetColumns(string table) =>
        string.IsNullOrWhiteSpace(table)
            ? Array.Empty<SchemaColumnMetadata>()
            : _columns.Value.Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<SchemaRelationshipMetadata> GetRelationships(string table) =>
        string.IsNullOrWhiteSpace(table)
            ? Array.Empty<SchemaRelationshipMetadata>()
            : _relationships.Value.Where(r => string.Equals(r.FromTable, table, StringComparison.OrdinalIgnoreCase)).ToList();

    private static IReadOnlyList<string> BuildTableLabels(TableInfo table, EntityDefinition? entity)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            table.Name,
            table.FullName,
            SplitIdentifier(table.Name),
        };

        if (entity is not null)
        {
            Add(labels, entity.Name);
            Add(labels, entity.Table);
            Add(labels, entity.Description);
            foreach (var synonym in entity.Synonyms) Add(labels, synonym);
        }

        return labels.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static IReadOnlyList<string> BuildColumnLabels(ColumnInfo column)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            column.ColumnName,
            $"{column.TableName}.{column.ColumnName}",
            SplitIdentifier(column.ColumnName),
        };
        return labels.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static void Add(HashSet<string> labels, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) labels.Add(value.Trim());
    }

    private static string SplitIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return identifier;
        var chars = new List<char>(identifier.Length + 8);
        for (var i = 0; i < identifier.Length; i++)
        {
            var ch = identifier[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(identifier[i - 1]))
                chars.Add(' ');
            chars.Add(ch);
        }
        return new string(chars.ToArray()).Replace('_', ' ').Trim();
    }
}
