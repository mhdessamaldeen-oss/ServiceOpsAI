namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>
/// Phase 07.β — analyst-quality output enrichment.
///
/// <para>The analyst doesn't want raw FK IDs in the result. They want NAMES. When the
/// SELECT projects a column like <c>CategoryId</c> or <c>StatusId</c>, or when a join brings
/// in a foreign-keyed entity, this phase auto-projects the target entity's
/// <see cref="Semantic.EntityDefinition.LabelColumn"/> (e.g. <c>TicketCategories.Name</c>,
/// <c>TicketStatuses.Name</c>) so the user sees "Billing Dispute" not "5".</para>
///
/// <para>Behaviour:</para>
/// <list type="number">
///   <item>Scan the SELECT list (and Aggregations) for any FK column.</item>
///   <item>For each FK, look up the referenced table via the schema-knowledge FK graph.</item>
///   <item>Find the target entity's LabelColumn (from semantic layer, fallback to NaturalKey).</item>
///   <item>If not already projected, add it to the SELECT list AND add the join to spec.Joins.</item>
/// </list>
///
/// <para>Also handles the root-entity case: when spec.Root is set and SELECT projects only
/// the primary key (or nothing useful for display), inject the root's LabelColumn at the
/// front of SELECT so the analyst always sees a human-readable identifier.</para>
///
/// <para>Idempotent. Re-running adds nothing because the label column is already in SELECT.</para>
/// </summary>
internal sealed class EnrichSelectWithLabelsPhase : ISpecRepairPhase
{
    public string Name => "EnrichSelectWithLabels";
    public string Covers =>
        "Analyst-quality enrichment: when SELECT references an FK or projects only IDs, " +
        "auto-add the human-readable label column from the referenced/root entity.";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        // Scalar-aggregation guard: when the spec is in "single scalar" mode (one row with an
        // aggregate, no GroupBy), adding a root label would force per-row stats by reintroducing
        // a high-cardinality column that EnsureSelectInGroupBy then appends to GROUP BY.
        // Symptom: "total bill amount" became SELECT BillNumber, SUM(Total) GROUP BY BillNumber
        // — per-row instead of a single scalar. Skip both enrichment steps in that case.
        bool isScalarAggregation = spec.Aggregations is { Count: > 0 }
            && spec.GroupBy is { Count: 0 }
            && spec.Aggregations.All(a => a is not null
                && IsScalarAggregateFunction(a.Function));
        if (isScalarAggregation) return;

        // ─── Step 1: Root-entity label safety net ────────────────────────────────────
        // When SELECT is empty OR projects only the root PK (no human-friendly column),
        // inject the root's label column at the front. Solves "show me tickets" returning
        // [{ Id: 1 }, { Id: 2 }] instead of [{ Id: 1, TicketNumber: TKT-00001, Title: ... }].
        TryAddRootLabel(spec, ctx);

        // ─── Step 2: For every FK column referenced in SELECT or filters, add the label ──
        // Walk SELECT first; then Aggregations; then GroupBy. For each column ending in "Id"
        // that maps to a known FK, resolve the target table, look up its label column, add
        // the join and project that label.
        EnrichFkReferences(spec, ctx);
    }

    /// <summary>
    /// If SELECT is empty / contains only the surrogate ID, add the root entity's label
    /// (LabelColumn or NaturalKey) at the front. Never replaces what's already there.
    /// </summary>
    private static void TryAddRootLabel(QuerySpec spec, SpecRepairContext ctx)
    {
        var rootEntity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
        if (rootEntity is null) return;

        // The label column to use — prefer LabelColumn, fall back to NaturalKey.
        var labelCol = !string.IsNullOrEmpty(rootEntity.LabelColumn)
            ? rootEntity.LabelColumn
            : rootEntity.NaturalKeyColumn;
        if (string.IsNullOrEmpty(labelCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, labelCol)) return;

        var qualified = $"{spec.Root}.{labelCol}";

        // Already selected? (qualified or bare)
        foreach (var s in spec.Select)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (string.Equals(s, qualified, System.StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(s, labelCol, System.StringComparison.OrdinalIgnoreCase)) return;
            if (s.EndsWith("." + labelCol, System.StringComparison.OrdinalIgnoreCase)) return;
        }

        // Only inject when SELECT is empty OR contains only ID-shaped columns. We don't want
        // to add a label to an analyst's deliberately-narrow projection ("just the IDs please").
        bool onlyIds = spec.Select.Count > 0
            && spec.Select.All(s => s is not null && (
                s.Equals("Id", System.StringComparison.OrdinalIgnoreCase)
                || s.EndsWith(".Id", System.StringComparison.OrdinalIgnoreCase)
                || s.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase)));

        if (spec.Select.Count == 0 || onlyIds)
        {
            // Insert at position 1 (right after Id if present, otherwise front).
            int insertAt = spec.Select.Count > 0 && spec.Select[0].EndsWith("Id", System.StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;
            spec.Select.Insert(insertAt, qualified);
            ctx.Diagnostics.Add(new(typeof(EnrichSelectWithLabelsPhase).Name,
                $"injected root label {qualified} into SELECT (was {(spec.Select.Count == 1 ? "empty" : "id-only")})"));
        }
    }

    /// <summary>
    /// For every FK-shaped column reference (column name ends with "Id" and maps to an
    /// outbound FK on the root or already-joined tables), bring in the target entity's
    /// label and add the join.
    /// </summary>
    private static void EnrichFkReferences(QuerySpec spec, SpecRepairContext ctx)
    {
        // Gather already-known column references from SELECT + Aggregations + GroupBy.
        var refs = new System.Collections.Generic.List<string>();
        refs.AddRange(spec.Select.Where(s => !string.IsNullOrEmpty(s))!);
        if (spec.Aggregations is { Count: > 0 })
            refs.AddRange(spec.Aggregations.Where(a => !string.IsNullOrEmpty(a?.Column)).Select(a => a.Column!));
        if (spec.GroupBy is { Count: > 0 })
            refs.AddRange(spec.GroupBy.Where(g => !string.IsNullOrEmpty(g))!);

        // Track which tables already participate in the spec so we don't double-join.
        var participatingTables = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table)) participatingTables.Add(j.Table);

        // Avoid re-adding the same label column twice for the same target table.
        var addedLabels = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var raw in refs)
        {
            var col = StripQualifier(raw);
            if (string.IsNullOrEmpty(col)) continue;
            if (!col.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (col.Equals("Id", System.StringComparison.OrdinalIgnoreCase)) continue;

            // Find the FK definition: walk Catalog.Snapshot.ForeignKeys for one whose
            // ParentTable matches the owner and ParentColumn matches the col.
            string? ownerTable = ExtractTablePart(raw) ?? spec.Root;
            var fk = ctx.Catalog.Snapshot.ForeignKeys.FirstOrDefault(f =>
                string.Equals(f.ParentTable, ownerTable, System.StringComparison.OrdinalIgnoreCase)
             && string.Equals(f.ParentColumn, col, System.StringComparison.OrdinalIgnoreCase));
            if (fk is null) continue;

            var targetTable = fk.ReferencedTable;
            var targetEntity = ctx.SemanticLayer.GetEntityForTable(targetTable);
            if (targetEntity is null) continue;

            var labelCol = !string.IsNullOrEmpty(targetEntity.LabelColumn)
                ? targetEntity.LabelColumn
                : targetEntity.NaturalKeyColumn;
            if (string.IsNullOrEmpty(labelCol)) continue;
            if (!ctx.Catalog.ColumnExists(targetTable, labelCol)) continue;

            var qualifiedLabel = $"{targetTable}.{labelCol}";
            if (addedLabels.Contains(qualifiedLabel)) continue;
            // Already projected by the user/LLM?
            bool already = spec.Select.Any(s => string.Equals(s, qualifiedLabel, System.StringComparison.OrdinalIgnoreCase));
            if (already) continue;

            // Project the label.
            spec.Select.Add(qualifiedLabel);
            addedLabels.Add(qualifiedLabel);

            // Ensure the join is present (left-join, so root rows without the related entity
            // are preserved). The compiler's JoinResolver picks the FK path automatically.
            if (!participatingTables.Contains(targetTable))
            {
                spec.Joins.Add(new JoinSpec { Table = targetTable, Kind = "left" });
                participatingTables.Add(targetTable);
            }

            ctx.Diagnostics.Add(new(typeof(EnrichSelectWithLabelsPhase).Name,
                $"enriched FK {col} → projected {qualifiedLabel} + left-joined {targetTable}"));
        }
    }

    private static string StripQualifier(string column)
    {
        if (string.IsNullOrEmpty(column)) return column;
        var dot = column.LastIndexOf('.');
        return dot >= 0 && dot < column.Length - 1 ? column[(dot + 1)..] : column;
    }

    private static string? ExtractTablePart(string columnRef)
    {
        if (string.IsNullOrEmpty(columnRef)) return null;
        var dot = columnRef.LastIndexOf('.');
        return dot > 0 ? columnRef[..dot] : null;
    }

    private static bool IsScalarAggregateFunction(string? fn)
    {
        if (string.IsNullOrEmpty(fn)) return false;
        return fn.Equals("SUM", System.StringComparison.OrdinalIgnoreCase)
            || fn.Equals("AVG", System.StringComparison.OrdinalIgnoreCase)
            || fn.Equals("MAX", System.StringComparison.OrdinalIgnoreCase)
            || fn.Equals("MIN", System.StringComparison.OrdinalIgnoreCase)
            || fn.Equals("COUNT", System.StringComparison.OrdinalIgnoreCase)
            || fn.Equals("COUNT_BIG", System.StringComparison.OrdinalIgnoreCase);
    }
}
