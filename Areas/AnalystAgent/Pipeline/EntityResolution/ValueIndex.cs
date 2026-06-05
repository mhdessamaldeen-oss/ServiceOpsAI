namespace AnalystAgent.Pipeline.EntityResolution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// In-memory implementation of <see cref="IValueIndex"/>. Pulls distinct values from the
/// configured (table, column) whitelist via the existing <see cref="IDbConnectionFactory"/>,
/// pre-extracts trigrams once per entry, and serves Jaccard-similarity lookups against the
/// query phrase.
///
/// <para><b>Concurrency model:</b> the index is exposed as an atomic read-only snapshot.
/// A rebuild builds a NEW snapshot off to the side and swaps the reference at the end —
/// readers never observe partial data.</para>
///
/// <para><b>Size bound:</b> ~1-5k entries for a typical schema. At ~50 bytes per entry
/// plus the trigram HashSet (~500 bytes worst case), total RAM ≈ 3 MB. Fine for an in-
/// process singleton.</para>
/// </summary>
internal sealed class ValueIndex : IValueIndex
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IOptionsMonitor<ValueIndexOptions> _options;
    private readonly ISemanticLayer _semanticLayer;
    private readonly AnalystAgent.Schema.IAnalystSchemaAccessPolicy _accessPolicy;
    private readonly ILogger<ValueIndex> _logger;
    private readonly object _swapGate = new();
    private volatile IndexSnapshot _snapshot = IndexSnapshot.Empty;

    public ValueIndex(
        IDbConnectionFactory dbFactory,
        IOptionsMonitor<ValueIndexOptions> options,
        ISemanticLayer semanticLayer,
        AnalystAgent.Schema.IAnalystSchemaAccessPolicy accessPolicy,
        ILogger<ValueIndex> logger)
    {
        _dbFactory = dbFactory;
        _options = options;
        _semanticLayer = semanticLayer;
        _accessPolicy = accessPolicy;
        _logger = logger;
    }

    public IReadOnlyList<ValueIndexHit> Lookup(string surface, int topK, double minSimilarity)
    {
        if (string.IsNullOrWhiteSpace(surface)) return Array.Empty<ValueIndexHit>();
        var snapshot = _snapshot;
        if (snapshot.Entries.Count == 0) return Array.Empty<ValueIndexHit>();

        var surfaceTrigrams = TrigramSimilarity.ExtractTrigrams(surface);
        if (surfaceTrigrams.Count == 0) return Array.Empty<ValueIndexHit>();

        // Score every entry — N is bounded (~5k) so linear scan is fine and avoids inverted-
        // index complexity. If the index ever grows past ~50k entries, switch to a trigram
        // posting-list pre-filter (same shape as pg_trgm GIN index).
        var hits = new List<ValueIndexHit>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            var sim = JaccardOnPreExtracted(surfaceTrigrams, entry.Trigrams);
            if (sim >= minSimilarity)
            {
                hits.Add(new ValueIndexHit(entry.Value, entry.Table, entry.Column, sim));
            }
        }

        return hits
            .OrderByDescending(h => h.Similarity)
            .Take(Math.Max(1, topK))
            .ToList();
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var lookupColumns = opts.LookupColumns ?? new List<LookupColumn>();
        if (lookupColumns.Count == 0)
        {
            // Auto-discovery — no enumerated table names; uses universal schema heuristics:
            // (a) base table with a Name-like column (Name, NameEn, NameAr), and
            // (b) row count below a small threshold (lookup tables are by definition small).
            // FK-referencedness would be the third signal but parsing FK metadata cleanly
            // across SQL Server's sys.foreign_keys requires a few joins — skipped here, the
            // size + name-column filter is sufficient signal for the deployments we target.
            lookupColumns = await AutoDiscoverLookupColumnsAsync(cancellationToken);
            _logger.LogInformation(
                "[ValueIndex] auto-discovered {Count} lookup column(s) (no Ai:EntityResolution:LookupColumns configured).",
                lookupColumns.Count);
            if (lookupColumns.Count == 0)
            {
                _snapshot = IndexSnapshot.Empty;
                return;
            }
        }

        var newEntries = new List<IndexEntry>(2048);
        var indexedColumns = 0;

        foreach (var col in lookupColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(col.Table) || string.IsNullOrWhiteSpace(col.Column))
                continue;

            try
            {
                var added = await LoadColumnValuesAsync(col.Table, col.Column, newEntries, cancellationToken);
                if (added > 0) indexedColumns++;
                _logger.LogDebug("[ValueIndex] indexed {Count} values from {Table}.{Column}", added, col.Table, col.Column);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ValueIndex] failed to load {Table}.{Column} — skipping", col.Table, col.Column);
            }
        }

        var newSnapshot = new IndexSnapshot(newEntries, indexedColumns, DateTimeOffset.UtcNow);
        lock (_swapGate)
        {
            _snapshot = newSnapshot;
        }
        _logger.LogInformation(
            "[ValueIndex] rebuilt — {Entries} entries across {Columns} columns",
            newEntries.Count, indexedColumns);
    }

    public ValueIndexStats GetStats()
    {
        var s = _snapshot;
        return new ValueIndexStats(s.Entries.Count, s.IndexedColumns, s.RebuiltUtc);
    }

    // Auto-discovery from the SEMANTIC LAYER. Collects every column declared as a label,
    // natural-key, or display column across all configured entities (deduped), then asks
    // INFORMATION_SCHEMA for tables that actually have one of those columns. Any base table
    // with a matching column AND < 500 rows becomes a lookup target — generalises across the
    // whole schema (Tickets, Customers, Bills, Regions, ServiceTypes, Departments, … not just
    // tables that happen to use the 3 specific Name/NameEn/NameAr column names).
    //
    // Hard-fail-safely: if the semantic layer is empty, log a warning and return zero candidates
    // (no hardcoded fallback list — operators must populate semantic-layer.json).
    private async Task<List<LookupColumn>> AutoDiscoverLookupColumnsAsync(CancellationToken ct)
    {
        var indexableColumns = CollectIndexableColumnsFromSemanticLayer();
        if (indexableColumns.Count == 0)
        {
            _logger.LogWarning("[ValueIndex] semantic layer has no LabelColumn / NaturalKeyColumn / DisplayColumns declared on any entity — fuzzy entity resolution will be a no-op. Populate semantic-layer.json to enable.");
            return new List<LookupColumn>();
        }

        var discovered = new List<LookupColumn>();
        // Parameterless INFORMATION_SCHEMA query — column-name filter is built dynamically from
        // semantic-layer values (validated via IsSafeIdentifier so no injection vector).
        var inList = string.Join(",", indexableColumns
            .Where(IsSafeIdentifier)
            .Select(c => $"'{c.Replace("'", "''")}'"));
        if (string.IsNullOrEmpty(inList)) return discovered;

        var discoverySql = $@"
            SELECT t.TABLE_NAME, c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_SCHEMA = c.TABLE_SCHEMA
            WHERE t.TABLE_TYPE = 'BASE TABLE'
              AND c.COLUMN_NAME IN ({inList})
              AND c.DATA_TYPE IN ('nvarchar','varchar','char','nchar','text','ntext')
            ORDER BY t.TABLE_NAME, c.COLUMN_NAME";

        var candidates = new List<(string Table, string Column)>(64);
        try
        {
            using var conn = _dbFactory.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = discoverySql;
                cmd.CommandTimeout = 30;
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            // Filter by row count — accept tables with < 500 rows (lookup-table heuristic).
            foreach (var (tbl, col) in candidates)
            {
                if (!IsSafeIdentifier(tbl) || !IsSafeIdentifier(col)) continue;
                if (!_accessPolicy.IsTableQueryable(tbl)) continue;   // never index a hidden/operational table's values
                using var cnt = conn.CreateCommand();
                cnt.CommandText = $"SELECT COUNT(*) FROM [{tbl}]";
                cnt.CommandTimeout = 10;
                try
                {
                    var n = Convert.ToInt32(await cnt.ExecuteScalarAsync(ct));
                    if (n > 0 && n < 500)
                    {
                        discovered.Add(new LookupColumn { Table = tbl, Column = col });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[ValueIndex] count failed for {Table} — skipping", tbl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ValueIndex] auto-discovery query failed — index stays empty");
        }
        return discovered;
    }

    // Pull every distinct label/natural-key/display column name declared across all semantic-layer entities.
    private HashSet<string> CollectIndexableColumnsFromSemanticLayer()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (!string.IsNullOrWhiteSpace(entity.LabelColumn)) set.Add(entity.LabelColumn!);
            if (!string.IsNullOrWhiteSpace(entity.NaturalKeyColumn)) set.Add(entity.NaturalKeyColumn!);
            foreach (var c in entity.DisplayColumns)
                if (!string.IsNullOrWhiteSpace(c)) set.Add(c);
        }
        return set;
    }

    private async Task<int> LoadColumnValuesAsync(
        string table, string column, List<IndexEntry> sink, CancellationToken ct)
    {
        // SECURITY: table/column come from configuration (not user input), but we still
        // restrict to a strict identifier shape and quote with square brackets. If a
        // misconfigured value slips through, the SQL parse will fail rather than allow
        // any chance of injection.
        if (!IsSafeIdentifier(table) || !IsSafeIdentifier(column))
            throw new InvalidOperationException(
                $"Refusing to index {table}.{column} — identifier contains characters outside [A-Za-z0-9_].");

        var sql = $"SELECT DISTINCT [{column}] FROM [{table}] WHERE [{column}] IS NOT NULL";
        var added = 0;

        using var conn = _dbFactory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = reader.GetValue(0)?.ToString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            value = value.Trim();
            if (value.Length > 200) continue; // skip absurdly long values — almost certainly not a lookup

            sink.Add(new IndexEntry(
                Value: value,
                Table: table,
                Column: column,
                Trigrams: TrigramSimilarity.ExtractTrigrams(value)));
            added++;
        }
        return added;
    }

    private static bool IsSafeIdentifier(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    private static double JaccardOnPreExtracted(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var intersection = 0;
        foreach (var item in a)
        {
            if (b.Contains(item)) intersection++;
        }
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private sealed record IndexEntry(string Value, string Table, string Column, HashSet<string> Trigrams);

    private sealed record IndexSnapshot(IReadOnlyList<IndexEntry> Entries, int IndexedColumns, DateTimeOffset RebuiltUtc)
    {
        public static readonly IndexSnapshot Empty = new(Array.Empty<IndexEntry>(), 0, DateTimeOffset.MinValue);
    }
}
