namespace AnalystAgent.Grounding;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.EntityResolution;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// Default <see cref="IValueLinker"/>: scans question text for whole-word matches against the
/// actual values of FK-reachable lookup tables. Uses <see cref="IEntityCatalog.GetAllLookupValues"/>
/// to enumerate values (cached + capped at 500 per table).
///
/// <para>Search expands beyond the retriever's top-K: we also walk 1- and 2-hop FK neighbors of
/// every linked table, so a question about Tickets in Damascus finds Regions even if Regions
/// wasn't in the top-K retrieval result. This mirrors how CHESS / X-SQL expand the link set
/// before SQL generation.</para>
/// </summary>
internal sealed class ValueLinker : IValueLinker
{
    private readonly IEntityCatalog _catalog;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IFuzzyEntityResolver _fuzzyResolver;
    private readonly IOptions<AnalystOptions> _options;
    private readonly ILogger<ValueLinker> _logger;

    public ValueLinker(
        IEntityCatalog catalog,
        ISemanticLayer semanticLayer,
        IFuzzyEntityResolver fuzzyResolver,
        IOptions<AnalystOptions> options,
        ILogger<ValueLinker> logger)
    {
        _catalog = catalog;
        _semanticLayer = semanticLayer;
        _fuzzyResolver = fuzzyResolver;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ValueLinkBinding>> LinkAsync(
        string question,
        IReadOnlyList<InferredTable> linkedTables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || linkedTables.Count == 0)
            return System.Array.Empty<ValueLinkBinding>();

        // Build the whole-word search corpus from the question.
        var qLow = " " + question.ToLowerInvariant() + " ";
        var sb = new System.Text.StringBuilder(qLow.Length);
        foreach (var ch in qLow)
        {
            if (ch == ',' || ch == '.' || ch == '!' || ch == '?' || ch == ';' || ch == ':' || ch == '"' || ch == '\'')
                sb.Append(' ');
            else sb.Append(ch);
        }
        qLow = sb.ToString();

        // Expand the link set: each linked table + 1- and 2-hop FK neighbors that are lookup-shaped.
        var lookupCandidates = ExpandToLookupNeighbors(linkedTables.Select(t => t.Name).ToList());

        var results = new List<ValueLinkBinding>();
        var seenPerTableColumn = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var table in lookupCandidates)
        {
            var values = _catalog.GetAllLookupValues(table);
            if (values.Count == 0) continue;
            foreach (var (col, val) in values)
            {
                if (val.Length < 2) continue;
                var needle = " " + val.ToLowerInvariant() + " ";
                if (!qLow.Contains(needle, System.StringComparison.Ordinal)) continue;

                // De-dup at (table, column, VALUE) level — so two distinct values in the SAME column
                // (e.g. "tickets from Damascus AND Aleppo", both in Regions.NameEn) BOTH bind, while an
                // exact duplicate is still suppressed. Keying on (table,column) alone silently dropped
                // every value after the first. Values are bounded (≤500/table) so this stays cheap.
                var key = table + "." + col + "=" + val;
                if (!seenPerTableColumn.Add(key)) continue;

                results.Add(new ValueLinkBinding(
                    Table: table,
                    Column: col,
                    Value: val,
                    MatchedToken: val,
                    Confidence: 1.0f));
            }
        }

        // Fuzzy fallback (ADD-1): catch typo'd lookup values the exact whole-word pass missed
        // ("Dmascus" → "Damascus"). Scoped to the SAME FK-reachable lookup candidates so a fuzzy
        // value from an unrelated table never binds, and the match similarity becomes the binding
        // confidence (distinguishing it from an exact 1.0 bind). No-op when the index is empty.
        if (_options.Value.EnableFuzzyValueLinking)
        {
            var candidateSet = new HashSet<string>(lookupCandidates, System.StringComparer.OrdinalIgnoreCase);
            var fuzzy = await _fuzzyResolver.ResolveAsync(question, cancellationToken);
            foreach (var hit in fuzzy)
            {
                if (hit.Canonical.Length < 2 || !candidateSet.Contains(hit.Table)) continue;
                var key = hit.Table + "." + hit.Column + "=" + hit.Canonical;
                if (!seenPerTableColumn.Add(key)) continue;   // exact pass already bound this value
                results.Add(new ValueLinkBinding(
                    Table: hit.Table,
                    Column: hit.Column,
                    Value: hit.Canonical,
                    MatchedToken: hit.Surface,
                    Confidence: (float)hit.Similarity));
            }
        }

        if (results.Count > 0)
        {
            _logger.LogInformation("[ValueLinker] resolved {Count} value link(s): {Pairs}",
                results.Count,
                string.Join(", ", results.Select(r => $"{r.Table}.{r.Column}='{r.Value}'")));
        }

        return results;
    }

    /// <summary>
    /// Returns the union of: (a) <paramref name="seeds"/> themselves and (b) all tables reachable
    /// via 1- or 2-hop FK from any seed AND whose entity is marked IsLookup OR has a Label column
    /// (heuristic lookup-shaped). The expansion is bidirectional (parent ↔ referenced).
    /// </summary>
    private List<string> ExpandToLookupNeighbors(List<string> seeds)
    {
        var visited = new HashSet<string>(seeds, System.StringComparer.OrdinalIgnoreCase);
        var lookupTables = new List<string>();
        var allFks = _catalog.Snapshot.ForeignKeys;

        // BFS up to 2 hops.
        var frontier = new List<(string Table, int Hops)>();
        foreach (var s in seeds) frontier.Add((s, 0));
        // Seeds themselves are also candidates if they're lookup-shaped.
        foreach (var s in seeds)
            if (IsLookupShaped(s)) lookupTables.Add(s);

        while (frontier.Count > 0)
        {
            var next = new List<(string, int)>();
            foreach (var (current, hops) in frontier)
            {
                if (hops >= 2) continue;
                foreach (var fk in allFks)
                {
                    string? neighbor = null;
                    if (string.Equals(fk.ParentTable, current, System.StringComparison.OrdinalIgnoreCase))
                        neighbor = fk.ReferencedTable;
                    else if (string.Equals(fk.ReferencedTable, current, System.StringComparison.OrdinalIgnoreCase))
                        neighbor = fk.ParentTable;
                    if (string.IsNullOrEmpty(neighbor)) continue;
                    if (!visited.Add(neighbor)) continue;
                    if (!_catalog.TableExists(neighbor)) continue;

                    if (IsLookupShaped(neighbor)) lookupTables.Add(neighbor);
                    next.Add((neighbor, hops + 1));
                }
            }
            frontier = next;
        }
        return lookupTables;
    }

    private bool IsLookupShaped(string tableName)
    {
        var entity = _semanticLayer.GetEntityForTable(tableName);
        if (entity?.IsLookup == true) return true;
        // Heuristic fallback: has a Label-style column AND GetAllLookupValues returns something
        // (the catalog caps at 500 rows, so Tickets/Bills/Outages return empty).
        var cols = _catalog.GetColumns(tableName);
        var hasLabel = cols.Any(c =>
            string.Equals(c.ColumnName, "Name", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "NameEn", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Title", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Code", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Label", System.StringComparison.OrdinalIgnoreCase));
        if (!hasLabel) return false;
        return _catalog.GetAllLookupValues(tableName).Count > 0;
    }
}
