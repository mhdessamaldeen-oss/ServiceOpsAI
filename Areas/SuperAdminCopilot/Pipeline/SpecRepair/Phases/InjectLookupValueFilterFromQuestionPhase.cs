namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

/// <summary>
/// Defensive phase: scan the question text for whole-word matches against the actual values of
/// "lookup-style" tables (Regions, ServiceTypes, TicketPriorities, TicketStatuses, etc.). When
/// a match is found AND the lookup table is FK-reachable from <see cref="QuerySpec.Root"/>
/// (1- or 2-hop), inject a <c>WHERE LookupTable.LabelCol = '&lt;match&gt;'</c> filter — unless
/// such a filter already exists.
///
/// <para>Catches the common silent-failure where the LLM drops the categorical constraint: e.g.
/// "how many critical open tickets in Damascus" → emitted <c>WHERE IsDeleted = 0</c> with NO
/// region / priority / status filter. The question's "Damascus" / "critical" / "open" tokens map
/// directly to known dimension values; we can recover those constraints deterministically.</para>
///
/// <para>Risk mitigations:
/// <list type="bullet">
///   <item>Whole-word match only (no substring; "Open" won't fire on "operation").</item>
///   <item>Skip if a filter already exists on the same label column.</item>
///   <item>Lookups limited to FK-reachable tables (1-hop or 2-hop via bridge).</item>
///   <item>Value strings must be ≥2 chars; the catalog also filters whitespace-only.</item>
///   <item>FK-reachability driven entirely by the schema graph — no hardcoded entity names.</item>
/// </list></para>
/// </summary>
internal sealed class InjectLookupValueFilterFromQuestionPhase : ISpecRepairPhase
{
    public string Name => "InjectLookupValueFilterFromQuestion";
    public string Covers => "Lookup value (Damascus / water / urgent / Open) in question + FK-reachable lookup → inject WHERE filter";

    // Tier window override: weak-model crutch.
    // Lookup-value scan (Damascus / Open / Critical). Strong NLU resolves these; Medium benefits from the safety net.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Medium;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip the "-- requested columns: ..." trailing hint the structural-cue parser injects.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Lowercase + whitespace/punct-padded question for whole-word substring matching.
        // Keep original casing of the value when constructing the filter — it's persisted to
        // catalog state.
        var qLow = " " + q.ToLowerInvariant() + " ";
        var sb = new System.Text.StringBuilder(qLow.Length);
        foreach (var ch in qLow)
        {
            if (ch == ',' || ch == '.' || ch == '!' || ch == '?' || ch == ';' || ch == ':' || ch == '"' || ch == '\'')
                sb.Append(' ');
            else sb.Append(ch);
        }
        qLow = sb.ToString();

        // Candidate lookup tables: anything declared IsLookup in semantic-layer that is
        // FK-reachable from spec.Root (one or two hops). Also include lookup-shaped tables
        // discovered via heuristic: small table the FK graph points TO, with a Label column.
        var rootEntity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
        if (rootEntity is null) return;

        var reachableLookups = GetReachableLookupTables(spec.Root, ctx);
        if (reachableLookups.Count == 0) return;

        var existingFilterCols = new HashSet<string>(
            spec.Filters.Select(f => (f.Column ?? "").ToLowerInvariant()),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (var (lookupTable, _) in reachableLookups)
        {
            var values = ctx.Catalog.GetAllLookupValues(lookupTable);
            if (values.Count == 0) continue;

            // Collect ALL value hits per (table, labelColumn). When the question contains an
            // OR-list ("Damascus or Aleppo", "Critical or High", "EUR, USD, or AED") the user
            // wants ALL of them — single-value match would drop the rest. Universal: works
            // for any field, any number of values, any entity reachable from root via FK.
            // Hits are grouped by labelColumn; we emit one filter per group (op=eq for one
            // hit, op=in with array for two-or-more).
            var hitsByLabel = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var (labelCol, value) in values)
            {
                if (value.Length < 2) continue;
                var needle = " " + value.ToLowerInvariant() + " ";
                if (!qLow.Contains(needle, System.StringComparison.Ordinal)) continue;

                if (!hitsByLabel.TryGetValue(labelCol, out var bucket))
                {
                    bucket = new System.Collections.Generic.List<string>();
                    hitsByLabel[labelCol] = bucket;
                }
                // Dedup — the same value can appear twice if catalog has duplicates.
                if (!bucket.Contains(value, System.StringComparer.OrdinalIgnoreCase))
                    bucket.Add(value);
            }

            foreach (var (labelCol, hits) in hitsByLabel)
            {
                var qualified = $"{lookupTable}.{labelCol}";
                if (existingFilterCols.Contains(qualified.ToLowerInvariant())) continue;

                if (hits.Count == 1)
                {
                    spec.Filters.Add(new FilterSpec { Column = qualified, Op = "eq", Value = hits[0] });
                    ctx.Diagnostics.Add(new(Name, $"injected {qualified} = '{hits[0]}' (matched in question)"));
                }
                else
                {
                    // OR-list shape — emit op=in with the array of values. Compiler renders
                    // this as `Table.Column IN (@p0, @p1, ...)`.
                    spec.Filters.Add(new FilterSpec
                    {
                        Column = qualified,
                        Op = "in",
                        Value = hits.ToArray()
                    });
                    ctx.Diagnostics.Add(new(Name, $"injected {qualified} IN ({string.Join(",", hits)}) — multi-value match"));
                }
                existingFilterCols.Add(qualified.ToLowerInvariant());
            }
        }
    }

    /// <summary>
    /// Returns lookup-style tables reachable from <paramref name="root"/> via 1- or 2-hop FK
    /// traversal. "Lookup-style" = the semantic-layer marks <c>IsLookup=true</c>, OR the table
    /// is small (≤500 rows; this cap is enforced inside <see cref="EntityCatalog.GetAllLookupValues"/>)
    /// and has a Label column the catalog can sample.
    /// </summary>
    private static List<(string Table, int Hops)> GetReachableLookupTables(string root, SpecRepairContext ctx)
    {
        var visited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { root };
        var frontier = new List<(string Table, int Hops)> { (root, 0) };
        var found = new List<(string, int)>();
        var allFks = ctx.Catalog.Snapshot.ForeignKeys;

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
                    if (!ctx.Catalog.TableExists(neighbor)) continue;

                    var entity = ctx.SemanticLayer.GetEntityForTable(neighbor);
                    var isLookupStyle = entity?.IsLookup == true
                        || (entity is null && IsLookupShaped(neighbor, ctx));
                    if (isLookupStyle) found.Add((neighbor, hops + 1));

                    next.Add((neighbor, hops + 1));
                }
            }
            frontier = next;
        }
        return found;
    }

    /// <summary>
    /// Heuristic for lookup-shaped tables that aren't declared in semantic-layer: small row
    /// count (capped at 500 inside the catalog) + has at least one of the standard label
    /// columns. Used as a fallback so a freshly-added lookup is picked up without editing
    /// semantic-layer.json.
    /// </summary>
    private static bool IsLookupShaped(string tableName, SpecRepairContext ctx)
    {
        if (!ctx.Catalog.TableExists(tableName)) return false;
        // Cheap check: presence of a label column. The actual row-count limit is enforced
        // by GetAllLookupValues — we'll get an empty list back for big tables.
        var cols = ctx.Catalog.GetColumns(tableName);
        return cols.Any(c =>
            string.Equals(c.ColumnName, "Name", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "NameEn", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Title", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Code", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Label", System.StringComparison.OrdinalIgnoreCase));
    }
}
