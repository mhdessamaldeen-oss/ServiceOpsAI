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
                // Length >= 3: a 2-char lookup value ("Q1", "L1") whole-word-matches far too easily and was a
                // documented source of spurious filters (a quarter token binding to a status value). Whole-word
                // + >=3 chars keeps real values ("Open", "Paid", "Damascus") while dropping incidental shorts.
                if (val.Length < 3) continue;
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

        // Inline-enum pass: bind question tokens to LOW-CARDINALITY string-column VALUES on the linked
        // tables THEMSELVES (Bills.Status='Overdue', Outages.Severity='Critical') — fact-table enums the
        // lookup pass above never sees (it only enumerates small lookup TABLES, bailing over 500 rows).
        // Same whole-word, >=3-char discipline. The binding flows through BuildGroundingHints + the
        // deterministic injector, so "overdue bills" gets WHERE Bills.Status='Overdue' enforced instead of
        // the model's invented date predicate. Schema-driven (cardinality-gated), portable — no value list.
        foreach (var t in linkedTables)
        {
            foreach (var (col, val) in _catalog.GetInlineEnumValues(t.Name))
            {
                if (val.Length < 3) continue;
                var needle = " " + val.ToLowerInvariant() + " ";
                if (!qLow.Contains(needle, System.StringComparison.Ordinal)) continue;
                // Skip when the enum word is used as a VERB (immediately followed by a preposition) rather
                // than an ADJECTIVE modifying the entity: "bills ISSUED in the last 30 days" / "bills PAID
                // by cash" are date/method filters, NOT a Status filter — while "overdue BILLS",
                // "active ACCOUNTS", "completed work ORDERS" are real status filters (followed by a noun).
                // Fact-table enum values double as common past-tense verbs (issued/paid/completed), so this
                // cuts the over-bind the fresh TEMPORAL suite exposed without touching the adjective bindings.
                if (IsVerbContext(qLow, val.ToLowerInvariant())) continue;
                var key = t.Name + "." + col + "=" + val;
                if (!seenPerTableColumn.Add(key)) continue;
                results.Add(new ValueLinkBinding(
                    Table: t.Name, Column: col, Value: val, MatchedToken: val, Confidence: 1.0f));
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
    /// via 1-hop FK from any seed AND whose entity is marked IsLookup OR has a Label column
    /// (heuristic lookup-shaped). The expansion is bidirectional (parent ↔ referenced).
    /// <para>1 hop, NOT 2: a 2-hop bidirectional reach pulled in DISTANT, unrelated lookups — e.g. an
    /// "outages" question reached TicketPriorities via Regions ← Tickets → TicketPriorities, so "critical"
    /// mis-bound TicketPriorities.Name instead of Outages.Severity and forced an invalid join. A direct
    /// (1-hop) FK neighbor is the lookup the question's tables actually reference.</para>
    /// </summary>
    // Prepositions that, immediately AFTER an enum word, mark it as a verb/temporal usage rather than an
    // adjective filter ("issued IN ...", "paid BY ...", "created ON ..."). Not 'of'/'to'/'at' (too weak).
    private static readonly HashSet<string> VerbContextPrepositions = new(System.StringComparer.OrdinalIgnoreCase)
    { "in", "by", "on", "during", "over", "since", "between", "within", "before", "after", "from" };

    /// <summary>True when the enum <paramref name="valLower"/> appears immediately followed by a
    /// verb-context preposition in the (space-padded, lowercased) question — i.e. it's the verb in
    /// "bills <b>issued in</b> the last 30 days", not the adjective in "overdue bills". Used only by the
    /// inline-enum pass, whose values double as common past-tense verbs.</summary>
    private static bool IsVerbContext(string qLowPadded, string valLower)
    {
        var needle = " " + valLower + " ";
        var idx = qLowPadded.IndexOf(needle, System.StringComparison.Ordinal);
        if (idx < 0) return false;
        var rest = qLowPadded.Substring(idx + needle.Length);
        var words = rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && VerbContextPrepositions.Contains(words[0]);
    }

    private List<string> ExpandToLookupNeighbors(List<string> seeds)
    {
        var visited = new HashSet<string>(seeds, System.StringComparer.OrdinalIgnoreCase);
        var lookupTables = new List<string>();
        var allFks = _catalog.Snapshot.ForeignKeys;

        // BFS, 1 hop only (see remarks — 2-hop reached unrelated lookups and caused mis-binds).
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
                if (hops >= 1) continue;
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
