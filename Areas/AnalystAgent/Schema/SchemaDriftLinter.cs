namespace AnalystAgent.Schema;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Retrieval;

// Phase 6 Step 20 — schema drift detector. Cross-checks every table/column reference in
// the per-DB JSON configs (schema-inferred + schema-overrides + verified-queries) against
// the live DB via IEntityCatalog. Surfaces warnings when a referenced table or column no
// longer exists — the engine would silently fail in subtle ways without this signal.
//
// Run modes:
//   • By default emits LogWarning for each drifted reference and returns the report.
//   • When AnalystOptions.FailFastOnSchemaDrift = true (off by default), throws
//     InvalidOperationException so the host crashes at startup rather than serving
//     stale answers in production. Operators opt in per deployment.
public interface ISchemaDriftLinter
{
    SchemaDriftReport Lint();
}

public sealed record SchemaDriftReport(
    int TablesChecked,
    int ColumnsChecked,
    IReadOnlyList<string> MissingTables,
    IReadOnlyList<string> MissingColumns,
    IReadOnlyList<string> VerifiedQueryReferences);

/// <summary>Thrown by <see cref="ISchemaDriftLinter.Lint"/> when drift is found AND the operator
/// opted into <c>FailFastOnSchemaDrift</c>. A distinct type so warmup can let it crash the host
/// while still swallowing every other (non-fatal) priming error.</summary>
public sealed class SchemaDriftException : InvalidOperationException
{
    public SchemaDriftException(string message) : base(message) { }
}

internal sealed class SchemaDriftLinter : ISchemaDriftLinter
{
    private readonly IEntityCatalog _catalog;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IVerifiedQueryStore _verifiedStore;
    private readonly IOptions<AnalystOptions> _options;
    private readonly ILogger<SchemaDriftLinter> _logger;

    public SchemaDriftLinter(
        IEntityCatalog catalog,
        ISchemaKnowledge knowledge,
        IVerifiedQueryStore verifiedStore,
        IOptions<AnalystOptions> options,
        ILogger<SchemaDriftLinter> logger)
    {
        _catalog = catalog;
        _knowledge = knowledge;
        _verifiedStore = verifiedStore;
        _options = options;
        _logger = logger;
    }

    public SchemaDriftReport Lint()
    {
        var missingTables = new List<string>();
        var missingColumns = new List<string>();
        var verifiedRefs = new List<string>();

        // Schema-inferred + overrides: every (table, column) annotated must exist live.
        foreach (var t in _knowledge.AllTables)
        {
            if (!_catalog.TableExists(t.Name))
            {
                missingTables.Add(t.Name);
                continue;
            }
            foreach (var c in t.Columns)
            {
                if (!_catalog.ColumnExists(t.Name, c.Name))
                    missingColumns.Add($"{t.Name}.{c.Name}");
            }
        }

        // Verified queries: best-effort. We can't fully parse the SQL but we can look for
        // obvious table identifiers in the form "FROM [Table]" / "JOIN [Table]" / "FROM Table".
        // A reference that doesn't exist anymore means the verified entry is stale.
        if (_verifiedStore.IsAvailable)
        {
            foreach (var vq in _verifiedStore.All)
            {
                if (string.IsNullOrWhiteSpace(vq.Sql)) continue;
                var cteNames = ExtractCteNames(vq.Sql);   // WITH x AS (...) — not base tables
                foreach (var token in ExtractTableTokens(vq.Sql))
                {
                    if (cteNames.Contains(token)) continue;   // a CTE name, not a drifted table
                    if (!_catalog.TableExists(token))
                        verifiedRefs.Add($"{vq.Id}: references unknown table '{token}'");
                }
                // Qualified column references (Table.Column / alias.Column) that resolve to a real
                // base table get an existence check — this is what catches a column RENAME leaving
                // stale verified SQL (e.g. Customers.CustomerName → FullName). Unqualified columns and
                // CTE-bound references are skipped (can't be safely attributed without a full parser).
                foreach (var (table, column) in ExtractQualifiedColumns(vq.Sql, cteNames))
                    if (_catalog.TableExists(table) && !_catalog.ColumnExists(table, column))
                        verifiedRefs.Add($"{vq.Id}: references unknown column '{table}.{column}'");
            }
        }

        var report = new SchemaDriftReport(
            _knowledge.AllTables.Count,
            _knowledge.AllTables.Sum(t => t.Columns.Count),
            missingTables,
            missingColumns,
            verifiedRefs);

        if (missingTables.Count + missingColumns.Count + verifiedRefs.Count == 0)
        {
            _logger.LogInformation("[SchemaDriftLinter] OK — no drift across {Tables} tables / {Columns} columns.",
                report.TablesChecked, report.ColumnsChecked);
        }
        else
        {
            _logger.LogWarning(
                "[SchemaDriftLinter] DRIFT — {MissingTables} missing table(s), {MissingColumns} missing column(s), {VqRefs} stale verified-query reference(s). Tables: {TablesList} ; Columns: {ColumnsList}",
                missingTables.Count, missingColumns.Count, verifiedRefs.Count,
                string.Join(", ", missingTables.Take(10)),
                string.Join(", ", missingColumns.Take(10)));
            if (_options.Value.FailFastOnSchemaDrift)
                throw new SchemaDriftException(
                    $"Schema drift detected: {missingTables.Count} missing table(s), {missingColumns.Count} missing column(s), {verifiedRefs.Count} stale verified-query reference(s). Re-run schema inference or fix the JSON config.");
        }

        return report;
    }

    // Lightweight token extraction — no SQL parser. Matches identifiers after FROM/JOIN
    // keywords, stripping schema prefix and brackets. Not exhaustive but catches the
    // common references that drift-detection cares about.
    private static IEnumerable<string> ExtractTableTokens(string sql)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in FromJoinTable.Matches(sql))
        {
            var name = m.Groups[1].Value;
            if (string.IsNullOrEmpty(name)) continue;
            // Skip common non-table keywords that follow JOIN (rare, but defensive).
            if (string.Equals(name, "ON", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) yield return name;
        }
    }

    // CTE names declared via `WITH x AS (` / `, y AS (`. These are query-local, not base tables,
    // so they must be excluded from the table- and column-existence checks.
    private static HashSet<string> ExtractCteNames(string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in CteDecl.Matches(sql))
            names.Add(m.Groups[1].Value);
        return names;
    }

    // Maps FROM/JOIN aliases (and bare table names) to their base table, then yields every qualified
    // (qualifier.Column) reference resolved to (baseTable, column). Conservative: emits only when the
    // qualifier resolves to a real (non-CTE) base table; everything ambiguous is skipped.
    private IEnumerable<(string Table, string Column)> ExtractQualifiedColumns(string sql, HashSet<string> cteNames)
    {
        var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in FromJoinAlias.Matches(sql))
        {
            var table = m.Groups[1].Value;
            var alias = m.Groups[2].Value;
            if (cteNames.Contains(table)) continue;
            if (!string.IsNullOrEmpty(alias) && !SqlClauseKeywords.Contains(alias))
                aliasToTable[alias] = table;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in QualifiedColumn.Matches(sql))
        {
            var qualifier = m.Groups[1].Value;
            var column = m.Groups[2].Value;
            if (cteNames.Contains(qualifier)) continue;
            // Resolve qualifier → base table: an alias, or a table referenced by name directly.
            string? table = aliasToTable.TryGetValue(qualifier, out var mapped) ? mapped
                : _catalog.TableExists(qualifier) ? qualifier : null;
            if (table is null || cteNames.Contains(table)) continue;
            if (seen.Add(table + "." + column))
                yield return (table, column);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex FromJoinTable =
        new(@"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FromJoinAlias =
        new(@"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(\w+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CteDecl =
        new(@"(?:\bWITH\b|,)\s+(\w+)\s+AS\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex QualifiedColumn =
        new(@"\b(\w+)\.(\w+)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // SQL clause keywords that can immediately follow a table token and must NOT be mistaken for an
    // alias. Grammar, not domain vocabulary.
    private static readonly HashSet<string> SqlClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "GROUP",
        "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "WITH", "AS", "PIVOT", "UNPIVOT",
    };
}
