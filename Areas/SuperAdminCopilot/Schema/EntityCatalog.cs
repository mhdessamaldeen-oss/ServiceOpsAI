namespace SuperAdminCopilot.Schema;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

public interface IEntityCatalog
{
    SchemaSnapshot Snapshot { get; }
    IForeignKeyGraph Graph { get; }
    bool TableExists(string name);
    bool ColumnExists(string table, string column);
    TableInfo? GetTable(string name);
    IReadOnlyList<ColumnInfo> GetColumns(string tableName);
    IReadOnlyList<TableInfo> AllTables();

    /// <summary>
    /// Returns up to 10 representative values for a small "label" column on the given table
    /// (Name / Title / Code / Label, in that priority order). Cached per table on first call.
    /// Returns an empty list when the table has no obvious label column or when sampling fails.
    /// Used by the retriever to ground the LLM's filter values: "Critical" is a real priority,
    /// not a column name.
    /// </summary>
    IReadOnlyList<string> GetSampleValues(string tableName);
}

internal sealed class EntityCatalog : IEntityCatalog
{
    private readonly Lazy<SchemaSnapshot> _snapshot;
    private readonly Lazy<IForeignKeyGraph> _graph;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<EntityCatalog> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _sampleValueCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] LabelColumnPriority = new[] { "Name", "Title", "Code", "Label" };

    public EntityCatalog(ISchemaIntrospector introspector, IDbConnectionFactory connectionFactory, ILogger<EntityCatalog> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _snapshot = new Lazy<SchemaSnapshot>(() =>
        {
            logger.LogInformation("[SuperAdminCopilot] Introspecting database schema…");
            var s = introspector.Introspect();
            logger.LogInformation(
                "[SuperAdminCopilot] Schema introspected: {TableCount} tables, {ColumnCount} columns, {FkCount} FKs.",
                s.Tables.Count, s.Columns.Count, s.ForeignKeys.Count);
            return s;
        });
        _graph = new Lazy<IForeignKeyGraph>(() => new ForeignKeyGraph(_snapshot.Value));
    }

    public SchemaSnapshot Snapshot => _snapshot.Value;
    public IForeignKeyGraph Graph => _graph.Value;

    public bool TableExists(string name) =>
        Snapshot.Tables.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool ColumnExists(string table, string column) =>
        Snapshot.Columns.Any(c =>
            string.Equals(c.TableName, table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase));

    public TableInfo? GetTable(string name) =>
        Snapshot.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ColumnInfo> GetColumns(string tableName) =>
        Snapshot.ColumnsOf(tableName).ToList();

    public IReadOnlyList<TableInfo> AllTables() => Snapshot.Tables;

    public IReadOnlyList<string> GetSampleValues(string tableName)
    {
        if (string.IsNullOrEmpty(tableName)) return Array.Empty<string>();
        return _sampleValueCache.GetOrAdd(tableName, FetchSampleValues);
    }

    private IReadOnlyList<string> FetchSampleValues(string tableName)
    {
        var labelColumn = LabelColumnPriority.FirstOrDefault(c => ColumnExists(tableName, c));
        if (labelColumn is null) return Array.Empty<string>();

        try
        {
            using var conn = _connectionFactory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT TOP 10 [{labelColumn}] FROM [{tableName}] WHERE [{labelColumn}] IS NOT NULL ORDER BY [{labelColumn}];";
            cmd.CommandTimeout = 5;

            var values = new List<string>(10);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var v = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
                if (!string.IsNullOrEmpty(v)) values.Add(v);
            }
            return values;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SuperAdminCopilot] Sample-value fetch failed for {Table}.", tableName);
            return Array.Empty<string>();
        }
    }
}
