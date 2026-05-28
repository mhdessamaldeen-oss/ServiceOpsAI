namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;

/// <summary>
/// Apply <see cref="ConceptPattern"/> entries from semantic-layer.json. When the question text
/// contains any of a pattern's triggers AND the spec's root (or any referenced table) matches
/// a table named in the pattern's filters, inject the pattern's filters into the spec.
///
/// <para>Example: "overdue bills" — trigger "overdue" matches; the Bills-overdue pattern
/// injects <c>WHERE Bills.Status = 'Overdue'</c>. Without this, the LLM might emit a bare
/// <c>SELECT COUNT(*) FROM Bills</c> with no overdue filter at all.</para>
///
/// <para>Schema-driven entirely: ZERO hardcoded entity names. Add new concepts by editing
/// <c>semantic-layer.json</c> alone.</para>
/// </summary>
internal sealed class ApplyConceptPatternsPhase : ISpecRepairPhase
{
    public string Name => "ApplyConceptPatterns";
    public string Covers => "Concept-pattern triggers in question text → inject filters from semantic-layer";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        if (string.IsNullOrEmpty(spec.Root)) return;

        var patterns = ctx.SemanticLayer.Config.ConceptPatterns;
        if (patterns is null || patterns.Count == 0) return;

        // Strip the structural-cue trailing hint so triggers only match user-written tokens.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);
        var qLower = q.ToLowerInvariant();

        // Build the set of tables already referenced by the spec — only apply concepts whose
        // filter columns target one of these tables, so the Tickets-overdue pattern doesn't
        // fire on a Bills question and vice versa.
        // EXTENSION: also include tables reachable via an FK from any referenced table. This
        // lets "backlog" (filter on TicketStatuses.Name) fire for "backlog tickets" — the spec
        // doesn't reference TicketStatuses yet, but Tickets has an FK to it so the join is
        // natural. Without this, concepts that introduce new lookup joins never fire.
        var referencedTables = CollectReferencedTables(spec);
        var fkReachable = CollectFkReachableTables(referencedTables, ctx.Catalog);
        var allowedTables = new HashSet<string>(referencedTables, System.StringComparer.OrdinalIgnoreCase);
        foreach (var t in fkReachable) allowedTables.Add(t);

        var appliedCount = 0;
        foreach (var pattern in patterns)
        {
            if (pattern.Triggers is null || pattern.Triggers.Count == 0) continue;
            if (pattern.Filters is null || pattern.Filters.Count == 0) continue;

            // Match any trigger — token-aware so "overdue" doesn't match "loverduepost".
            var matched = false;
            foreach (var trigger in pattern.Triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger)) continue;
                if (Regex.IsMatch(qLower, $@"\b{Regex.Escape(trigger.ToLowerInvariant())}\b"))
                { matched = true; break; }
            }
            if (!matched) continue;

            // Check the filters target a table the spec actually references.
            // (Otherwise applying a Tickets concept to a Bills query produces a join the user
            // didn't ask for.)
            var filterTables = pattern.Filters
                .Select(f => SplitTable(f.Column ?? ""))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            if (filterTables.Count == 0) continue;
            if (!filterTables.Overlaps(allowedTables)) continue;

            // Inject each filter (skip if an identical column-already exists to avoid duplicates).
            foreach (var cf in pattern.Filters)
            {
                if (string.IsNullOrWhiteSpace(cf.Column)) continue;
                if (spec.Filters.Any(existing =>
                    string.Equals(existing.Column, cf.Column, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Op, cf.Op, System.StringComparison.OrdinalIgnoreCase)))
                    continue;
                spec.Filters.Add(new FilterSpec
                {
                    Column = cf.Column,
                    Op = string.IsNullOrEmpty(cf.Op) ? "eq" : cf.Op,
                    Value = cf.Value,
                });
                appliedCount++;
            }
            ctx.Diagnostics.Add(new(Name, $"applied concept '{pattern.Meaning}' ({pattern.Filters.Count} filter(s))"));
        }

        if (appliedCount > 0)
            ctx.Diagnostics.Add(new(Name, $"injected {appliedCount} concept-driven filter(s) total"));
    }

    // Walks the live FK graph one hop from each referenced table. One hop is enough — concepts
    // typically filter on a directly-related lookup (e.g. Ticket→TicketStatus). Multi-hop walks
    // would over-fire by linking concepts across loosely-related tables.
    private static HashSet<string> CollectFkReachableTables(
        HashSet<string> referencedTables, SuperAdminCopilot.Schema.IEntityCatalog catalog)
    {
        var reachable = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var fks = catalog.Snapshot?.ForeignKeys;
        if (fks is null) return reachable;
        foreach (var fk in fks)
        {
            if (referencedTables.Contains(fk.ParentTable) && !string.IsNullOrEmpty(fk.ReferencedTable))
                reachable.Add(fk.ReferencedTable);
            if (referencedTables.Contains(fk.ReferencedTable) && !string.IsNullOrEmpty(fk.ParentTable))
                reachable.Add(fk.ParentTable);
        }
        return reachable;
    }

    private static HashSet<string> CollectReferencedTables(QuerySpec spec)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(spec.Root)) set.Add(spec.Root);
        foreach (var c in spec.Select) Add(set, c);
        foreach (var c in spec.GroupBy) Add(set, c);
        foreach (var f in spec.Filters) Add(set, f.Column ?? "");
        foreach (var o in spec.OrderBy) Add(set, o.Column ?? "");
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table)) set.Add(j.Table);
        return set;
    }

    private static void Add(HashSet<string> set, string columnRef)
    {
        var t = SplitTable(columnRef);
        if (!string.IsNullOrEmpty(t)) set.Add(t);
    }

    private static string SplitTable(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var clean = s.Replace("[", "").Replace("]", "");
        var dot = clean.IndexOf('.');
        return dot <= 0 ? "" : clean[..dot];
    }
}
