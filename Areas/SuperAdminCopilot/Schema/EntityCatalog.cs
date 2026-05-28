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

    /// <summary>
    /// Returns ALL distinct values (up to <paramref name="maxRows"/>) from the given table's
    /// label column. Returns <see cref="EntityCatalog.LabelColumnPriority"/> probe column on
    /// the LHS of the tuple and the value on the RHS. Cached per table on first call. Returns
    /// an empty list for non-lookup tables / large tables (those should use sampled values
    /// instead). Used by the spec-repair LookupValueInjection phase to map question tokens
    /// ("Damascus", "Water", "Critical") to the WHERE-clause filter on the lookup's label column.
    /// </summary>
    IReadOnlyList<(string LabelColumn, string Value)> GetAllLookupValues(string tableName, int maxRows = 500);
}

internal sealed class EntityCatalog : IEntityCatalog
{
    private readonly Lazy<SchemaSnapshot> _snapshot;
    private readonly Lazy<IForeignKeyGraph> _graph;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<EntityCatalog> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _sampleValueCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<(string, string)>> _allValuesCache = new(StringComparer.OrdinalIgnoreCase);

    // Extended priority: Name first (English/canonical label), then bilingual NameEn / NameAr,
    // then Title / Code / Label. Matches the LookupValueInjection use case where Regions has
    // NameEn but Tickets/Bills lookups use Name. Both English and Arabic label columns are
    // sampled (English first then Arabic) so Arabic-language questions also match.
    internal static readonly string[] LabelColumnPriority = new[] { "Name", "NameEn", "Title", "Code", "Label", "NameAr", "TitleAr" };

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

    public IReadOnlyList<(string LabelColumn, string Value)> GetAllLookupValues(string tableName, int maxRows = 500)
    {
        if (string.IsNullOrEmpty(tableName)) return Array.Empty<(string, string)>();
        // Cache key includes maxRows so a smaller default doesn't poison a later larger request.
        var key = tableName + "::" + maxRows;
        return _allValuesCache.GetOrAdd(key, _ => FetchAllLookupValues(tableName, maxRows));
    }

    private IReadOnlyList<(string, string)> FetchAllLookupValues(string tableName, int maxRows)
    {
        // Sample English-language label first, then Arabic. Bilingual lookups (Regions has
        // both NameEn and NameAr) get both sets of values into the index — the InjectLookupValue
        // phase scans question text in either language and matches whichever label column it
        // shares with the row.
        var labelColumns = LabelColumnPriority.Where(c => ColumnExists(tableName, c)).Distinct().ToList();
        if (labelColumns.Count == 0) return Array.Empty<(string, string)>();

        var results = new List<(string, string)>();
        try
        {
            using var conn = _connectionFactory.Open();
            // Quick row-count check — skip large tables; this method is for true lookups
            // (Statuses / Priorities / Regions etc.), not for Tickets / Bills / Outages.
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM [{tableName}];";
                countCmd.CommandTimeout = 5;
                var rowCount = Convert.ToInt64(countCmd.ExecuteScalar());
                if (rowCount > maxRows) return Array.Empty<(string, string)>();
            }
            foreach (var col in labelColumns)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT DISTINCT [{col}] FROM [{tableName}] WHERE [{col}] IS NOT NULL;";
                cmd.CommandTimeout = 5;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var v = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v) && v!.Length >= 2)
                        results.Add((col, v));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SuperAdminCopilot] All-values fetch failed for {Table}.", tableName);
            return Array.Empty<(string, string)>();
        }
    }
}
