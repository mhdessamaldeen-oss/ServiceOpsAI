namespace SuperAdminCopilot.Schema;

using Microsoft.Extensions.Logging;

// Phase 6 Step 21 — schema diff service for the admin UI.
//
// Compares the LIVE database schema (via ISchemaIntrospector) against the cached
// schema-inferred.json snapshot (via ISchemaKnowledge). Surfaces three categories of
// drift the admin needs to resolve before re-generating the JSON:
//
//   • AddedTables       — tables exist in the DB but not in the JSON config
//   • RemovedTables     — tables exist in the JSON but not in the DB (likely renamed/dropped)
//   • AddedColumns      — columns added to an existing table since last sync
//   • RemovedColumns    — columns removed from an existing table since last sync
//   • AffectedVerifiedQueries — count of verified-query entries that reference the
//                               drifted tables (so the admin sees the blast radius)
//
// This is data-only — no UI logic, no formatting. The admin page consumes the result
// and renders. Per the project-wide principle: backend is authoritative.
public interface ISchemaDiffService
{
    SchemaDiffReport Compute();
}

public sealed record SchemaDiffReport(
    DateTimeOffset ComputedAt,
    IReadOnlyList<string> AddedTables,
    IReadOnlyList<string> RemovedTables,
    IReadOnlyList<ColumnRef> AddedColumns,
    IReadOnlyList<ColumnRef> RemovedColumns,
    int InferredTableCount,
    int LiveTableCount,
    bool InSync);

public sealed record ColumnRef(string Table, string Column);

internal sealed class SchemaDiffService : ISchemaDiffService
{
    private readonly ISchemaIntrospector _introspector;
    private readonly ISchemaKnowledge _knowledge;
    private readonly ILogger<SchemaDiffService> _logger;

    public SchemaDiffService(
        ISchemaIntrospector introspector,
        ISchemaKnowledge knowledge,
        ILogger<SchemaDiffService> logger)
    {
        _introspector = introspector;
        _knowledge = knowledge;
        _logger = logger;
    }

    public SchemaDiffReport Compute()
    {
        var live = _introspector.Introspect();
        var liveTables = new HashSet<string>(live.Tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var inferredTables = new HashSet<string>(_knowledge.AllTables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var addedTables = liveTables.Except(inferredTables, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        var removedTables = inferredTables.Except(liveTables, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

        // Column diff — only for tables present on BOTH sides. Removed/added tables already
        // covered above; flagging their columns separately would be noise.
        var addedCols = new List<ColumnRef>();
        var removedCols = new List<ColumnRef>();
        var liveColsByTable = live.Columns
            .GroupBy(c => c.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var t in _knowledge.AllTables)
        {
            if (!liveColsByTable.TryGetValue(t.Name, out var liveCols)) continue;
            var inferredCols = new HashSet<string>(t.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var c in liveCols.Except(inferredCols, StringComparer.OrdinalIgnoreCase))
                addedCols.Add(new ColumnRef(t.Name, c));
            foreach (var c in inferredCols.Except(liveCols, StringComparer.OrdinalIgnoreCase))
                removedCols.Add(new ColumnRef(t.Name, c));
        }

        addedCols.Sort((a, b) => string.Compare(a.Table + "." + a.Column, b.Table + "." + b.Column, StringComparison.OrdinalIgnoreCase));
        removedCols.Sort((a, b) => string.Compare(a.Table + "." + a.Column, b.Table + "." + b.Column, StringComparison.OrdinalIgnoreCase));

        var inSync = addedTables.Count == 0 && removedTables.Count == 0
                     && addedCols.Count == 0 && removedCols.Count == 0;

        _logger.LogInformation(
            "[SchemaDiffService] live={LiveTables} inferred={InferredTables} added-tables={AddedTables} removed-tables={RemovedTables} added-cols={AddedCols} removed-cols={RemovedCols} inSync={InSync}",
            liveTables.Count, inferredTables.Count, addedTables.Count, removedTables.Count, addedCols.Count, removedCols.Count, inSync);

        return new SchemaDiffReport(
            ComputedAt: DateTimeOffset.UtcNow,
            AddedTables: addedTables,
            RemovedTables: removedTables,
            AddedColumns: addedCols,
            RemovedColumns: removedCols,
            InferredTableCount: inferredTables.Count,
            LiveTableCount: liveTables.Count,
            InSync: inSync);
    }
}
