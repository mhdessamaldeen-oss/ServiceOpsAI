namespace AnalystAgent.Schema;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Semantic;

public interface IAnalystSchemaAccessPolicy
{
    bool IsTableAllowed(string table);

    /// <summary>STRICTER than <see cref="IsTableAllowed"/>: a table the copilot may QUERY/ground on. Excludes
    /// the retriever-hidden set (RetrieverHiddenTables/Patterns) ON TOP OF the hard BlockedTables — so the
    /// copilot's own operational tables (Copilot*) and other hidden tables can never be probed for values or
    /// joined as a data source, even via FK-neighbor expansion. Use this anywhere the grounding/value layer
    /// reaches a table; <see cref="IsTableAllowed"/> stays the hard security gate for the validator/metadata.</summary>
    bool IsTableQueryable(string table);

    bool IsColumnAllowed(string table, string column);
    bool IsColumnSensitive(string table, string column);
    IReadOnlyList<TableInfo> FilterTables(IEnumerable<TableInfo> tables);
    IReadOnlyList<ColumnInfo> FilterColumns(IEnumerable<ColumnInfo> columns);
}

internal sealed class AnalystSchemaAccessPolicy : IAnalystSchemaAccessPolicy
{
    private readonly AnalystOptions _options;
    private readonly ISemanticLayer _semantic;

    public AnalystSchemaAccessPolicy(IOptions<AnalystOptions> options, ISemanticLayer semantic)
    {
        _options = options.Value;
        _semantic = semantic;
    }

    public bool IsTableAllowed(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) return false;
        var normalized = NormalizeObjectName(table);
        var bare = BareName(normalized);

        if (_options.TableExposureMode == TableExposureMode.ConfiguredOnly
            || _options.RestrictMetadataToConfiguredEntities)
        {
            if (_semantic.GetEntityForTable(bare) is null) return false;
        }

        foreach (var blocked in _options.BlockedTables ?? Enumerable.Empty<string>())
        {
            var b = NormalizeObjectName(blocked);
            if (string.Equals(normalized, b, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bare, BareName(b), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var pattern in _options.BlockedTablePatterns ?? Enumerable.Empty<string>())
        {
            if (WildcardMatches(pattern, bare) || WildcardMatches(pattern, normalized))
                return false;
        }

        return true;
    }

    public bool IsTableQueryable(string table)
    {
        if (!IsTableAllowed(table)) return false;                       // hard blocks first
        var normalized = NormalizeObjectName(table);
        var bare = BareName(normalized);

        foreach (var hidden in _options.RetrieverHiddenTables ?? Enumerable.Empty<string>())
        {
            var h = NormalizeObjectName(hidden);
            if (string.Equals(normalized, h, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bare, BareName(h), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var pattern in _options.RetrieverHiddenTablePatterns ?? Enumerable.Empty<string>())
        {
            if (WildcardMatches(pattern, bare) || WildcardMatches(pattern, normalized))
                return false;
        }

        return true;
    }

    public bool IsColumnAllowed(string table, string column)
    {
        if (!IsTableAllowed(table) || string.IsNullOrWhiteSpace(column)) return false;
        if (MatchesAnyColumnPattern(table, column, _options.BlockedColumns)) return false;
        if (_semantic.IsSensitiveColumn(BareName(table), column)) return false;
        return true;
    }

    public bool IsColumnSensitive(string table, string column)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column)) return false;
        return _semantic.IsSensitiveColumn(BareName(table), column)
            || MatchesAnyColumnPattern(table, column, _options.SensitiveColumns);
    }

    public IReadOnlyList<TableInfo> FilterTables(IEnumerable<TableInfo> tables) =>
        tables.Where(t => IsTableAllowed(t.Name) || IsTableAllowed(t.FullName)).ToList();

    public IReadOnlyList<ColumnInfo> FilterColumns(IEnumerable<ColumnInfo> columns) =>
        columns.Where(c => IsColumnAllowed(c.TableName, c.ColumnName)).ToList();

    private bool MatchesAnyColumnPattern(string table, string column, IEnumerable<string>? patterns)
    {
        var bareTable = BareName(table);
        var qualified = $"{bareTable}.{column}";
        foreach (var pattern in patterns ?? Enumerable.Empty<string>())
        {
            var p = NormalizeColumnPattern(pattern);
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (WildcardMatches(p, column) || WildcardMatches(p, qualified)) return true;
        }
        return false;
    }

    private static string NormalizeObjectName(string value) =>
        (value ?? string.Empty).Trim().Trim('[', ']').Replace("].[", ".", StringComparison.Ordinal);

    private static string NormalizeColumnPattern(string value) =>
        NormalizeObjectName(value).Replace(" ", "", StringComparison.Ordinal);

    private static string BareName(string value)
    {
        var clean = NormalizeObjectName(value);
        var dot = clean.LastIndexOf('.');
        return dot >= 0 ? clean[(dot + 1)..] : clean;
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(value)) return false;
        var regex = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
