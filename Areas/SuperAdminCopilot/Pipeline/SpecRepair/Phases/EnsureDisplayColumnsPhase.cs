namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>
/// Phase 07.ε — analyst-quality column completeness.
///
/// <para>The LLM consistently under-projects: <c>show me tickets</c> yields
/// <c>SELECT TicketNumber</c>; <c>tickets with their category and status names</c> yields
/// <c>SELECT TicketNumber, Status.Name</c> (category dropped). Users see useless results.</para>
///
/// <para>This phase fires for LIST-shape queries (no scalar aggregation) where the SELECT is
/// empty or only contains the root's natural key. It expands the projection to the entity's
/// configured <see cref="Semantic.EntityDefinition.DisplayColumns"/> AND auto-joins display-
/// worthy FKs (Status, Priority, Category, Department, Region, Customer, AssignedTo) to bring
/// in each target's LabelColumn. Universal: driven by FK naming conventions + semantic-layer
/// metadata, no entity names hardcoded.</para>
///
/// <para>Runs AFTER EnrichSelectWithLabelsPhase so any explicit FK enrichment the user
/// requested wins; this phase only fills the GAP for bare list queries.</para>
/// </summary>
internal sealed class EnsureDisplayColumnsPhase : ISpecRepairPhase
{
    public string Name => "EnsureDisplayColumns";
    public string Covers =>
        "Bare list queries on a root entity expand SELECT to the entity's displayColumns + " +
        "auto-projects FK label names for the common display dimensions.";

    // FK display-worthiness is now derived from the semantic layer: an FK is display-worthy
    // when its referenced entity has a LabelColumn declared. Adding a new lookup entity with
    // a LabelColumn in semantic-layer.json automatically enables auto-label-projection — no
    // hardcoded column-suffix list. See <see cref="TargetHasLabelColumn"/>.

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        // Trace EVERY entry so we can see exactly why the phase may skip a case.
        var selectDump = spec.Select is not null && spec.Select.Count > 0
            ? string.Join("|", spec.Select.Take(12))
            : "(empty)";
        ctx.Diagnostics.Add(new(typeof(EnsureDisplayColumnsPhase).Name,
            $"ENTRY: root='{spec.Root ?? "(null)"}' selectCount={spec.Select?.Count ?? -1} select=[{selectDump}] aggs={spec.Aggregations?.Count ?? 0} grp={spec.GroupBy?.Count ?? 0}"));

        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // List-shape gate: skip true aggregation queries (scalar counts / sums) and period-
        // comparison shapes. Allow GROUP BY-only queries through — the LLM frequently emits
        // a spurious GROUP BY for "X and the customer region" without any aggregation; in
        // that case the display columns still need to be projected.
        if (spec.Aggregations is { Count: > 0 }) return;
        if (spec.PeriodComparisons is { Count: > 0 }) return;

        var rootEntity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
        if (rootEntity is null) return;

        // Always-fire policy: for list-shape queries, ENSURE
        //   (a) the root's displayColumns + LabelColumn are present (Step 1), AND
        //   (b) every display-worthy FK label is auto-projected (Step 2).
        // Step 2 always runs — a 5-column SELECT might still be missing Category.Name even
        // though TicketNumber/Title/Status/Priority are present. Step 1 is gated by
        // narrow / root-label-missing so we don't double-add when the LLM already picked a
        // rich projection.
        bool isEmptyOrNarrow = IsEmptyOrNarrow(spec.Select);
        bool rootLabelMissing = IsRootLabelMissing(spec, rootEntity, ctx);
        bool runStep1 = isEmptyOrNarrow || rootLabelMissing;

        // ─── Step 1: expand to displayColumns ──────────────────────────────────────────
        // The semantic layer declares which root columns an analyst always wants. Project all
        // of them that physically exist (in declaration order) — earliest entry wins position.
        var alreadyInSelect = new System.Collections.Generic.HashSet<string>(
            spec.Select.Where(s => !string.IsNullOrEmpty(s))!,
            System.StringComparer.OrdinalIgnoreCase);

        int addedDisplay = 0;
        if (runStep1 && rootEntity.DisplayColumns is { Count: > 0 })
        {
            foreach (var dc in rootEntity.DisplayColumns)
            {
                if (string.IsNullOrEmpty(dc)) continue;
                if (!ctx.Catalog.ColumnExists(spec.Root, dc)) continue;
                var qualified = $"{spec.Root}.{dc}";
                if (alreadyInSelect.Contains(qualified) || alreadyInSelect.Contains(dc)) continue;
                spec.Select.Add(qualified);
                alreadyInSelect.Add(qualified);
                addedDisplay++;
            }
        }

        // ─── Step 2: auto-project FK labels for display-worthy dimensions ──────────────
        // For each FK on the root whose column matches a display pattern (Status/Priority/
        // Category/etc.), add the referenced entity's LabelColumn and a LEFT JOIN. Skip when
        // a join to that table already exists.
        var participatingTables = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table)) participatingTables.Add(j.Table);

        // Pre-compute how many root FKs point to each target table. When multiple FKs from
        // the root reference the SAME target (e.g. Tickets has CreatedByUserId +
        // AssignedToUserId + UpdatedByUserId all → AspNetUsers), we CANNOT pick one to
        // auto-project without ambiguity — the join key is unknown. Skip those targets and
        // let the LLM / operator be explicit. Single-FK targets stay safe to enrich.
        var fkTargetCount = new System.Collections.Generic.Dictionary<string, int>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var fk in ctx.Catalog.Snapshot.ForeignKeys)
        {
            if (!string.Equals(fk.ParentTable, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(fk.ReferencedTable)) continue;
            fkTargetCount.TryGetValue(fk.ReferencedTable, out var c);
            fkTargetCount[fk.ReferencedTable] = c + 1;
        }

        int addedFkLabels = 0;
        foreach (var fk in ctx.Catalog.Snapshot.ForeignKeys)
        {
            if (!string.Equals(fk.ParentTable, spec.Root, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(fk.ParentColumn) || string.IsNullOrEmpty(fk.ReferencedTable))
                continue;
            // Skip when this target has multiple incoming FKs from the root — the join key
            // is ambiguous (which FK column do we join on?). Compiler emits one join with
            // an arbitrary predicate, producing wrong-table semantics.
            if (fkTargetCount.TryGetValue(fk.ReferencedTable, out var n) && n > 1) continue;
            if (!TargetHasLabelColumn(fk, ctx)) continue;

            var targetEntity = ctx.SemanticLayer.GetEntityForTable(fk.ReferencedTable);
            if (targetEntity is null) continue;
            var labelCol = !string.IsNullOrEmpty(targetEntity.LabelColumn)
                ? targetEntity.LabelColumn
                : targetEntity.NaturalKeyColumn;
            if (string.IsNullOrEmpty(labelCol)) continue;
            if (!ctx.Catalog.ColumnExists(fk.ReferencedTable, labelCol)) continue;

            var qualifiedLabel = $"{fk.ReferencedTable}.{labelCol}";
            if (alreadyInSelect.Contains(qualifiedLabel)) continue;

            spec.Select.Add(qualifiedLabel);
            alreadyInSelect.Add(qualifiedLabel);
            addedFkLabels++;

            if (!participatingTables.Contains(fk.ReferencedTable))
            {
                spec.Joins.Add(new JoinSpec { Table = fk.ReferencedTable, Kind = "left" });
                participatingTables.Add(fk.ReferencedTable);
            }
        }

        if (addedDisplay > 0 || addedFkLabels > 0)
        {
            ctx.Diagnostics.Add(new(typeof(EnsureDisplayColumnsPhase).Name,
                $"expanded SELECT for list-shape: +{addedDisplay} displayColumns, +{addedFkLabels} FK-label projections on {spec.Root}"));
        }
    }

    private static bool IsEmptyOrNarrow(System.Collections.Generic.List<string> select)
    {
        if (select is null || select.Count == 0) return true;
        // "Narrow" = 1-3 columns (often LLM emits TicketNumber + one or two FK labels but
        // forgets Title and the rest of the display set).
        if (select.Count > 3) return false;
        return true;
    }

    private static bool IsRootLabelMissing(QuerySpec spec, Semantic.EntityDefinition rootEntity, SpecRepairContext ctx)
    {
        var labelCol = !string.IsNullOrEmpty(rootEntity.LabelColumn)
            ? rootEntity.LabelColumn
            : rootEntity.NaturalKeyColumn;
        if (string.IsNullOrEmpty(labelCol)) return false;
        if (!ctx.Catalog.ColumnExists(spec.Root, labelCol)) return false;

        var qualified = $"{spec.Root}.{labelCol}";
        foreach (var s in spec.Select)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (string.Equals(s, qualified, System.StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(s, labelCol, System.StringComparison.OrdinalIgnoreCase)) return false;
            if (s.EndsWith("." + labelCol, System.StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// Display-worthy when the FK's referenced entity has a non-empty <c>LabelColumn</c>
    /// declared in semantic-layer.json. Universal: any operator-added lookup with a label
    /// becomes display-worthy automatically — no per-suffix list to keep in sync.
    /// </summary>
    private static bool TargetHasLabelColumn(SuperAdminCopilot.Schema.ForeignKeyInfo fk, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(fk?.ReferencedTable)) return false;
        var target = ctx.SemanticLayer.GetEntityForTable(fk.ReferencedTable);
        if (target is null) return false;
        return !string.IsNullOrEmpty(target.LabelColumn);
    }
}
