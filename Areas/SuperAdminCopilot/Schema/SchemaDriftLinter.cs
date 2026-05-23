namespace SuperAdminCopilot.Schema;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Retrieval;

// Phase 6 Step 20 — schema drift detector. Cross-checks every table/column reference in
// the per-DB JSON configs (schema-inferred + schema-overrides + verified-queries) against
// the live DB via IEntityCatalog. Surfaces warnings when a referenced table or column no
// longer exists — the engine would silently fail in subtle ways without this signal.
//
// Run modes:
//   • By default emits LogWarning for each drifted reference and returns the report.
//   • When CopilotOptions.FailFastOnSchemaDrift = true (off by default), throws
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

internal sealed class SchemaDriftLinter : ISchemaDriftLinter
{
    private readonly IEntityCatalog _catalog;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IVerifiedQueryStore _verifiedStore;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<SchemaDriftLinter> _logger;

    public SchemaDriftLinter(
        IEntityCatalog catalog,
        ISchemaKnowledge knowledge,
        IVerifiedQueryStore verifiedStore,
        IOptions<CopilotOptions> options,
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
                foreach (var token in ExtractTableTokens(vq.Sql))
                {
                    if (!_catalog.TableExists(token))
                        verifiedRefs.Add($"{vq.Id}: references unknown table '{token}'");
                }
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
                throw new InvalidOperationException(
                    $"Schema drift detected: {missingTables.Count} missing table(s), {missingColumns.Count} missing column(s). Re-run schema inference or fix the JSON config.");
        }

        return report;
    }

    // Lightweight token extraction — no SQL parser. Matches identifiers after FROM/JOIN
    // keywords, stripping schema prefix and brackets. Not exhaustive but catches the
    // common references that drift-detection cares about.
    private static IEnumerable<string> ExtractTableTokens(string sql)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(sql))
        {
            var name = m.Groups[1].Value;
            if (string.IsNullOrEmpty(name)) continue;
            // Skip common non-table keywords that follow JOIN (rare, but defensive).
            if (string.Equals(name, "ON", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) yield return name;
        }
    }
}
