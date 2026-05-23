namespace SuperAdminCopilot.Pipeline.Stages;

using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

/// <summary>
/// Deterministic post-extraction enrichment of the LLM's <see cref="QuerySpec"/>. Encodes
/// UNIVERSAL invariants about what a sensible result should contain — none of these depend
/// on the question's wording, just on the spec's structure. This is the "smart compiler"
/// layer the architecture was missing: the LLM identifies INTENT, we apply schema-aware
/// rules to make the result useful.
///
/// <para>Rules (all idempotent, all spec-shape based, NOT question-shape based):</para>
/// <list type="bullet">
///   <item><b>R1</b> Default projection (label + natural key) when <c>SELECT</c> is empty and the spec is not a pure aggregation.</item>
///   <item><b>R2</b> Filtered columns get added to <c>SELECT</c> so the user can see the proof of the filter.</item>
///   <item><b>R3</b> Foreign-key columns in <c>SELECT</c> get augmented with their target table's label (e.g. <c>AssignedToUserId</c> → also include <c>AspNetUsers.UserName</c>).</item>
///   <item><b>R4</b> <c>ORDER BY</c> columns get added to <c>SELECT</c> so the user can see the proof of the sort.</item>
///   <item><b>R5</b> <c>GROUP BY</c> columns must appear in <c>SELECT</c>.</item>
///   <item><b>Dedup</b> Final SELECT list is deduplicated (case-insensitive).</item>
/// </list>
/// </summary>
public interface ISpecEnricher
{
    /// <summary>Apply R1–R5 + dedup. Mutates the spec in place. Safe to call multiple times
    /// (idempotent). Returns the same instance for fluent chaining.</summary>
    QuerySpec Enrich(QuerySpec spec);
}

internal sealed class SpecEnricher : ISpecEnricher
{
    private readonly ISchemaKnowledge _knowledge;
    private readonly ILogger<SpecEnricher> _logger;

    public SpecEnricher(ISchemaKnowledge knowledge, ILogger<SpecEnricher> logger)
    {
        _knowledge = knowledge;
        _logger = logger;
    }

    public QuerySpec Enrich(QuerySpec spec)
    {
        if (spec is null || !_knowledge.IsAvailable) return spec!;

        var rootTable = _knowledge.GetTable(spec.Root);
        if (rootTable is null) return spec;

        var hasAggregations = spec.Aggregations.Count > 0;

        // R8 — Rewrite filters on a foreign-key column carrying a non-numeric value (i.e. a
        // looks-like-a-lookup-name) into filters on the FK target's LabelColumn. Without this
        // the planner often emits `Tickets.StatusId = 'Open'` which compares an int FK to a
        // string — SQL Server either errors or returns zero rows. Universal & schema-driven:
        // works for any FK whose target is a lookup table with a LabelColumn role tag.
        RewriteFkFilterToLookupLabel(spec);

        // A1 — explicit "all columns" affordance. When the LLM emits select:["*"] (signaling
        // "show me everything", e.g. for "show me all the info about this ticket"), expand the
        // sentinel into the root's full content-column list before any other rule runs. Skipped
        // for aggregation specs (a wildcard SELECT under GROUP BY would explode the projection).
        if (!hasAggregations && spec.Select.Count == 1 &&
            (spec.Select[0] == "*" || spec.Select[0].EndsWith(".*", StringComparison.Ordinal)))
        {
            spec.Select.Clear();
            ExpandAllColumnsForRoot(spec, rootTable);
        }

        // R1 — default projection for empty SELECT on non-aggregation queries.
        // R1b — when the same condition triggers, also pull lookup-dimension labels (Status,
        // Priority, Category, etc.) so "show tickets" surfaces human-readable dimension names
        // alongside the root's natural key + label.
        if (!hasAggregations && spec.Select.Count == 0)
        {
            AddDefaultProjection(spec, rootTable);
            AddRootLookupLabels(spec, rootTable);
        }

        // R5 — GROUP BY columns must appear in SELECT (SQL-standard requirement).
        EnsureGroupByInSelect(spec);

        // R7 — for each HAVING predicate, ensure the same aggregate is in Aggregations so the
        // metric value lands in SELECT. "Categories with > 15 tickets" otherwise emits HAVING
        // COUNT(*) > 15 with no count column projected — the user sees category names with no
        // way to verify the threshold or read off the actual counts.
        EnsureHavingMetricInAggregations(spec);

        // R2 — filtered columns get added to SELECT so the result shows the filter's value.
        //      Skip for pure-count specs (their answer is a single number; adding columns
        //      would turn them into a degenerate row list).
        if (!IsPureCount(spec))
            AddFilteredColumnsToSelect(spec);

        // R4 — ORDER BY columns get added to SELECT so the sort key is visible in the result.
        //      Skip for pure-count specs (degenerate single-row).
        if (!IsPureCount(spec))
            AddOrderByColumnsToSelect(spec);

        // R3 — FK columns augmented with target's label column.
        //      Skip for pure-count (single-row) and for aggregations without group-by.
        if (!hasAggregations || spec.GroupBy.Count > 0)
            AugmentFkColumnsWithLabels(spec);

        // Final dedup pass — different rules might have added the same column.
        DedupSelect(spec);

        return spec;
    }

    // ── R1 ─────────────────────────────────────────────────────────────────────────────
    private static void AddDefaultProjection(QuerySpec spec, InferredTable rootTable)
    {
        // Order matters: natural key → label → primary key fallback.
        if (!string.IsNullOrEmpty(rootTable.Roles.NaturalKey))
            spec.Select.Add($"{rootTable.Name}.{rootTable.Roles.NaturalKey}");
        if (!string.IsNullOrEmpty(rootTable.Roles.LabelColumn))
            spec.Select.Add($"{rootTable.Name}.{rootTable.Roles.LabelColumn}");
        // If neither natural key nor label exist, fall back to the first PK column.
        if (spec.Select.Count == 0 && rootTable.PrimaryKey.Count > 0)
            spec.Select.Add($"{rootTable.Name}.{rootTable.PrimaryKey[0]}");
    }

    // ── A1: select-all expansion ───────────────────────────────────────────────────────
    // Expands a wildcard SELECT into the root table's full column list, dropping columns
    // the system shouldn't surface uncritically:
    //   - audit columns (UpdatedAt-class metadata that doesn't read as "content")
    //   - soft-delete columns (a binary flag the compiler auto-applies as a filter)
    // Foreign-key columns ARE kept; R3 then augments them with target labels. PII columns
    // are NOT filtered here because the compiler's downstream PII denylist drops them at
    // SQL emission time — keeping the column in the spec preserves the chance that R3
    // augments the FK side, while the PII column itself never reaches the result.
    private static void ExpandAllColumnsForRoot(QuerySpec spec, InferredTable rootTable)
    {
        foreach (var c in rootTable.Columns)
        {
            if (string.Equals(c.Role, SpecConstants.ColumnRoles.SoftDelete, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(c.Role, SpecConstants.ColumnRoles.Audit, StringComparison.OrdinalIgnoreCase))
                continue;
            spec.Select.Add($"{rootTable.Name}.{c.Name}");
        }
    }

    // ── R1b ────────────────────────────────────────────────────────────────────────────
    // For each FK on the root that points at a lookup-flagged dimension table with a label
    // column, add that target's label to SELECT. Lookup-only on purpose: person tables
    // (assignee, creator) and bridges would balloon the projection. R3 still augments any
    // FK that ends up in SELECT through other rules; this rule fills the "browse the root,
    // no other inputs" case that R3 can't reach because R1 produces no FK columns.
    private void AddRootLookupLabels(QuerySpec spec, InferredTable rootTable)
    {
        var present = spec.Select.Select(NormalizeRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var col in rootTable.Columns)
        {
            if (!string.Equals(col.Role, SpecConstants.ColumnRoles.ForeignKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(col.References)) continue;
            var (targetTable, _) = SplitColumnRef(col.References);
            if (string.IsNullOrEmpty(targetTable)) continue;
            var target = _knowledge.GetTable(targetTable);
            if (target is null) continue;
            // Restrict to lookup dimensions — person/bridge tables would bloat the projection.
            if (!target.Flags.IsLookup) continue;
            if (target.Roles.LabelColumn is not { Length: > 0 } labelCol) continue;

            var labelRef = $"{targetTable}.{labelCol}";
            if (present.Add(NormalizeRef(labelRef))) spec.Select.Add(labelRef);
        }
    }

    // ── R2 ─────────────────────────────────────────────────────────────────────────────
    private static void AddFilteredColumnsToSelect(QuerySpec spec)
    {
        var present = spec.Select.Select(NormalizeRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in spec.Filters)
        {
            var col = f.Column ?? "";
            if (string.IsNullOrWhiteSpace(col)) continue;
            // Soft-delete columns are auto-injected by the compiler and irrelevant to display.
            if (LooksLikeSoftDeleteColumn(col)) continue;
            // is_null / not_null are existence checks; the column itself is rarely informative.
            if (IsExistenceOp(f.Op)) continue;
            // text_search is a magic op pointing at an entity, not a real column reference.
            if (string.Equals(f.Op, SpecConstants.FilterOps.TextSearch, StringComparison.OrdinalIgnoreCase)) continue;
            // Skip raw-expression-shaped columns (CASE WHEN ..., function calls in column field).
            if (col.Contains('(') || col.Contains(' ')) continue;
            if (present.Add(NormalizeRef(col)))
                spec.Select.Add(col);
        }
    }

    // ── R4 ─────────────────────────────────────────────────────────────────────────────
    private static void AddOrderByColumnsToSelect(QuerySpec spec)
    {
        if (spec.OrderBy.Count == 0) return;
        var aggAliases = spec.Aggregations
            .Where(a => !string.IsNullOrWhiteSpace(a.Alias))
            .Select(a => a.Alias!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = spec.Select.Select(NormalizeRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var o in spec.OrderBy)
        {
            var col = o.Column ?? "";
            if (string.IsNullOrWhiteSpace(col)) continue;
            // Aggregate alias (e.g. "Cnt") — already projected by the aggregation, don't duplicate.
            if (aggAliases.Contains(col)) continue;
            // Raw expressions handled elsewhere — don't try to project them as columns.
            if (col.Contains('(') || col.Contains(' ')) continue;
            // Must look like a Table.Column reference (otherwise it's likely an alias to an
            // expression we can't reify — skip).
            if (!col.Contains('.')) continue;
            if (present.Add(NormalizeRef(col))) spec.Select.Add(col);
        }
    }

    // ── R3 ─────────────────────────────────────────────────────────────────────────────
    // Two branches, both yielding "add the dimension's LabelColumn so the result is human-
    // readable":
    //   (a) FK column in SELECT → walk FK to target → add target.LabelColumn
    //   (b) PK column of a LOOKUP table in SELECT → add same table's LabelColumn
    //       (covers "groupBy Entitys.Id" — the LLM grouped/projected the dimension's own PK
    //        rather than the root's FK; R3-a wouldn't fire and the user would see naked IDs)
    private void AugmentFkColumnsWithLabels(QuerySpec spec)
    {
        var present = spec.Select.Select(NormalizeRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<string>();
        foreach (var col in spec.Select)
        {
            var (table, colName) = SplitColumnRef(col);
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(colName)) continue;
            var t = _knowledge.GetTable(table);
            var column = t?.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;

            // (b) Lookup-table PK in SELECT — add its own LabelColumn from the same table.
            if (t is not null && t.Flags.IsLookup
                && string.Equals(column.Role, SpecConstants.ColumnRoles.PrimaryKey, StringComparison.OrdinalIgnoreCase)
                && t.Roles.LabelColumn is { Length: > 0 } selfLabel)
            {
                var selfLabelRef = $"{t.Name}.{selfLabel}";
                if (present.Add(NormalizeRef(selfLabelRef))) toAdd.Add(selfLabelRef);
                // A4 — also surface the dimension's NaturalKey if it's distinct from the label.
                // This is the schema-driven proxy for "Code" / "ShortCode" columns the user often
                // wants alongside the display name. No hardcoded column names — driven by the
                // inferred schema's role assignments.
                if (t.Roles.NaturalKey is { Length: > 0 } nk
                    && !string.Equals(nk, selfLabel, StringComparison.OrdinalIgnoreCase))
                {
                    var nkRef = $"{t.Name}.{nk}";
                    if (present.Add(NormalizeRef(nkRef))) toAdd.Add(nkRef);
                }
                continue;
            }

            // (a) FK column in SELECT — walk to the target's LabelColumn.
            if (!string.Equals(column.Role, SpecConstants.ColumnRoles.ForeignKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(column.References)) continue;

            var (targetTable, _) = SplitColumnRef(column.References);
            if (string.IsNullOrEmpty(targetTable)) continue;
            var target = _knowledge.GetTable(targetTable);
            if (target?.Roles.LabelColumn is not { Length: > 0 } labelCol) continue;

            var labelRef = $"{targetTable}.{labelCol}";
            if (present.Add(NormalizeRef(labelRef))) toAdd.Add(labelRef);
            // A4 — also pull the target's NaturalKey if distinct from LabelColumn. Schema-
            // driven, so any app whose schema-overrides marks a "Code" column as natural-key
            // gets that column surfaced alongside the display name.
            if (target.Roles.NaturalKey is { Length: > 0 } tnk
                && !string.Equals(tnk, labelCol, StringComparison.OrdinalIgnoreCase))
            {
                var nkRef = $"{targetTable}.{tnk}";
                if (present.Add(NormalizeRef(nkRef))) toAdd.Add(nkRef);
            }
        }
        spec.Select.AddRange(toAdd);
    }

    // ── R7 ─────────────────────────────────────────────────────────────────────────────
    // For each HAVING predicate, if no matching aggregation is declared (function+column),
    // synthesize one with a sensible alias so the metric value appears in SELECT alongside
    // the predicate. The compiler de-dupes aliases later; we don't try to be clever about
    // alias names, just give the user a column to read.
    private static void EnsureHavingMetricInAggregations(QuerySpec spec)
    {
        if (spec.Having is null || spec.Having.Count == 0) return;
        foreach (var h in spec.Having)
        {
            if (string.IsNullOrWhiteSpace(h.Function)) continue;
            var match = spec.Aggregations.Any(a =>
                string.Equals(a.Function, h.Function, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Column ?? "*", h.Column ?? "*", StringComparison.OrdinalIgnoreCase));
            if (match) continue;
            var alias = string.Equals(h.Function, SpecConstants.Aggregates.Count, StringComparison.OrdinalIgnoreCase)
                ? "Count"
                : h.Function;
            spec.Aggregations.Add(new AggregateSpec
            {
                Function = h.Function,
                Column = h.Column ?? "*",
                Alias = alias,
            });
        }
    }

    // ── R8 ─────────────────────────────────────────────────────────────────────────────
    // The planner sometimes filters on the FK column itself with the lookup VALUE, e.g.
    // `Tickets.StatusId = 'Open'`. SQL Server compares an int FK to a string and either errors
    // or returns zero rows — even though the user just wanted "tickets with status Open". This
    // post-process rewrites those filters onto the FK target's LabelColumn — universal: works
    // for any FK whose target is a lookup-flagged table with a LabelColumn role tag. No
    // hardcoded entity names; the entire decision is driven by inferred schema metadata.
    private void RewriteFkFilterToLookupLabel(QuerySpec spec)
    {
        if (spec.Filters.Count == 0) return;
        for (int i = 0; i < spec.Filters.Count; i++)
        {
            var f = spec.Filters[i];
            var (table, colName) = SplitColumnRef(f.Column ?? "");
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(colName)) continue;

            var t = _knowledge.GetTable(table);
            var column = t?.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;

            // Only rewrite FK columns; PK / label / date columns are filtered as-is.
            if (!string.Equals(column.Role, SpecConstants.ColumnRoles.ForeignKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(column.References)) continue;

            // Skip null-check ops — IS NULL / IS NOT NULL on FK columns is the canonical
            // "unassigned" / "has owner" pattern; not a value mismatch.
            if (IsExistenceOp(f.Op)) continue;

            // Only rewrite when the value looks like a lookup name (non-numeric, non-wildcarded).
            var valStr = f.Value?.ToString();
            if (string.IsNullOrEmpty(valStr)) continue;
            if (decimal.TryParse(valStr, out _)) continue;
            if (bool.TryParse(valStr, out _)) continue;
            if (valStr.Contains('%')) continue;

            // Walk to target — must be a lookup table with a usable LabelColumn.
            var (targetTable, _) = SplitColumnRef(column.References);
            if (string.IsNullOrEmpty(targetTable)) continue;
            var target = _knowledge.GetTable(targetTable);
            if (target is null || !target.Flags.IsLookup) continue;
            if (target.Roles.LabelColumn is not { Length: > 0 } labelCol) continue;

            spec.Filters[i] = new FilterSpec
            {
                Column = $"{targetTable}.{labelCol}",
                Op = f.Op,
                Value = f.Value,
            };
        }
    }

    // ── R5 ─────────────────────────────────────────────────────────────────────────────
    private static void EnsureGroupByInSelect(QuerySpec spec)
    {
        if (spec.GroupBy.Count == 0) return;
        var present = spec.Select.Select(NormalizeRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var g in spec.GroupBy)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            // Skip raw expressions (e.g. "FORMAT(CreatedAt, 'yyyy-MM')") — these are surfaced
            // via the Computed alias, which Compiler emits as a SELECT term.
            if (g.Contains('(') || g.Contains(' ')) continue;
            if (present.Add(NormalizeRef(g))) spec.Select.Insert(0, g);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────
    private static bool IsPureCount(QuerySpec spec) =>
        spec.Aggregations.Count > 0 && spec.GroupBy.Count == 0;

    private static bool LooksLikeSoftDeleteColumn(string col) =>
        col.Contains("IsDeleted", StringComparison.OrdinalIgnoreCase)
        || col.Contains("DeletedAt", StringComparison.OrdinalIgnoreCase)
        || col.Contains("IsArchived", StringComparison.OrdinalIgnoreCase);

    private static bool IsExistenceOp(string op) =>
        op.Equals(SpecConstants.FilterOps.IsNull, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.IsNullAlt, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.NotNull, StringComparison.OrdinalIgnoreCase)
        || op.Equals(SpecConstants.FilterOps.NotNullAlt, StringComparison.OrdinalIgnoreCase);

    private static (string Table, string Column) SplitColumnRef(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return ("", "");
        var clean = s.Replace("[", "").Replace("]", "");
        var dot = clean.IndexOf('.');
        return dot <= 0 ? ("", clean) : (clean[..dot], clean[(dot + 1)..]);
    }

    private static string NormalizeRef(string s) =>
        (s ?? "").Trim().Replace("[", "").Replace("]", "").ToLowerInvariant();

    private static void DedupSelect(QuerySpec spec)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>(spec.Select.Count);
        foreach (var s in spec.Select)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (seen.Add(NormalizeRef(s))) deduped.Add(s);
        }
        spec.Select.Clear();
        spec.Select.AddRange(deduped);
    }
}
