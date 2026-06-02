namespace SuperAdminCopilot.Compilation;

using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

// D.4 — partial-class split. Each partial file groups one cluster of methods so the file stays
// scannable. All partials share the same fields (declared here) and the constructor.
//   • SqlCompiler.cs            (this file): top-level Compile() + semantic-reference expansion.
//   • SqlCompiler.Helpers.cs    : column/table-ref parsing, JsonElement → CLR coercion.
internal sealed partial class SqlCompiler : ICompiler
{
    private readonly IEntityCatalog _catalog;
    private readonly JoinResolver _joinResolver;
    private readonly ISemanticLayer _semanticLayer;
    private readonly CopilotOptions _options;
    // PR 1 of dialect sweep — every dialect-touching emission (identifier quoting, NULL coalesce,
    // string cast, TOP/LIMIT, NOW/GETDATE, DateAdd/Diff/Trunc, etc.) routes through this
    // abstraction. Bound to MssqlDialect by default in DI; swapping the binding to PostgresDialect
    // is the only change required to retarget the database. See Areas/SuperAdminCopilot/Compilation/Dialects/MIGRATION.md.
    private readonly ISqlDialect _dialect;
    // 2026-06-01 — temporal-token grammar extracted to ITemporalTokenizer so WHERE-builder
    // and QualifyColumnsInExpression both delegate to the same single source of truth.
    // See Compilation/ITemporalTokenizer.cs.
    private readonly ITemporalTokenizer _temporalTokenizer;
    // 2026-06-01 — metric/dimension expansion extracted to ISemanticExpander so the compiler
    // doesn't own semantic-layer translation logic. See Compilation/ISemanticExpander.cs.
    private readonly ISemanticExpander _semanticExpander;
    // 2026-06-01 — filter-value rewriting (synonym + lookup-name + column-ref drop) extracted
    // to IFilterValueRewriter so the WHERE-builder doesn't own value-translation logic.
    // See Compilation/IFilterValueRewriter.cs.
    private readonly IFilterValueRewriter _filterValueRewriter;
    // F1 — populated by BuildSelect, consumed by BuildGroupBy. Keys are the plain `[Table].[Column]`
    // form of a GROUP BY column that allows NULL; values are the matching `ISNULL(..., '(Unassigned)')`
    // expression. Both SELECT-side and GROUP BY-side must use the wrapped form for SQL Server to
    // accept the query. Reset on every Compile() — never persisted across calls.
    private Dictionary<string, string> _groupByDisplayMap = new(StringComparer.OrdinalIgnoreCase);

    // Phase 5 — user-visible warnings collected during compilation. Populated by BuildWhere /
    // BuildSelect when an LLM-emitted column or filter value is silently dropped (unknown column,
    // placeholder-token value). Reset at the top of Compile(); surfaced on CompiledSql.Warnings;
    // the orchestrator forwards them onto CopilotResponse.Warnings so the user sees what didn't
    // make it into the SQL. Critical for weaker local models that drop columns/filters frequently.
    private List<Abstractions.CopilotWarning> _warnings = new();

    public SqlCompiler(
        IEntityCatalog catalog,
        JoinResolver joinResolver,
        ISemanticLayer semanticLayer,
        IOptions<CopilotOptions> options,
        ISqlDialect dialect,
        ITemporalTokenizer temporalTokenizer,
        ISemanticExpander semanticExpander,
        IFilterValueRewriter filterValueRewriter)
    {
        _catalog = catalog;
        _joinResolver = joinResolver;
        _semanticLayer = semanticLayer;
        _options = options.Value;
        _dialect = dialect;
        _temporalTokenizer = temporalTokenizer;
        _semanticExpander = semanticExpander;
        _filterValueRewriter = filterValueRewriter;
    }

    public CompiledSql Compile(QuerySpec spec)
    {
        // Reset per-compilation collectors. Same lifecycle as _groupByDisplayMap.
        _warnings = new List<Abstractions.CopilotWarning>();

        // Normalize root: the LLM often echoes the schema-qualified form from the prompt
        // (e.g. "dbo.Tickets"). Strip any schema prefix before catalog lookup. Also resolve
        // entity synonyms ("Issue" → "Tickets") via the semantic layer when the planner names
        // the canonical entity instead of the table.
        spec.Root = StripSchema(spec.Root);
        if (!string.IsNullOrEmpty(spec.Root) && !_catalog.TableExists(spec.Root))
        {
            var entity = _semanticLayer.GetEntityByNameOrSynonym(spec.Root);
            if (entity is not null && _catalog.TableExists(entity.Table)) spec.Root = entity.Table;
        }

        if (string.IsNullOrWhiteSpace(spec.Root) || !_catalog.TableExists(spec.Root))
            throw new InvalidOperationException($"Compiler: unknown root table '{spec.Root}'.");

        // I1 — apply the same synonym resolution to every "Table.Column" reference in the
        // spec, not just the root. Without this, a typo on the LLM's part (e.g. "Entities.Name"
        // when the real table is "Entitys", or a semantic-layer synonym used outside the root
        // slot) silently fails: TryFormatColumn returns false and the column is dropped from
        // SELECT / GROUP BY / filters without a user-visible signal.
        ResolveSynonymsOnColumnRefs(spec);

        // ── LLM-output mutation now owned by SpecRepair pipeline (see
        // Areas/SuperAdminCopilot/Pipeline/SpecRepair/README.md). It runs in SpecExtractor
        // before the spec reaches the compiler. The compiler trusts the spec it receives
        // for column qualification, filter shape, GROUP BY consistency, etc.
        // Previously inline: AutoQualifyUnqualifiedColumns, DropFilterContradictingGroupBy,
        // StripQuotedFilterValues. All migrated to SpecRepair phases of the same name.

        // Expand semantic-layer references (metric:* in aggregations, dimension:* in
        // select/groupBy/computed) into concrete columns/expressions/extra filters BEFORE we
        // walk references. This lets metric-baked filters add their tables to the join graph.
        _semanticExpander.Expand(spec);

        // Recover from a common LLM mistake: AVG/SUM/MIN/MAX(*) is invalid T-SQL (only COUNT
        // accepts *). When the planner emits this AND there's a matching Computed expression
        // (the per-row math the user actually wants aggregated), fold the expression into the
        // aggregate's column slot. Heuristic matches by alias-prefix or single-computed.
        RewriteInvalidStarAggregates(spec);

        // Auto-inject soft-delete filter for the root table when the entity declares one and
        // the planner didn't already add a filter on the same column. The compiler — not the
        // LLM — owns this so it never gets forgotten.
        InjectSoftDeleteFilter(spec);

        // Clamp limit to MaxRows: the executor caps row reads, but we also want the SQL itself
        // to reflect the real cap so SQL Server doesn't plan for the absurd "TOP 999999" the
        // user asked for. If the user (or planner) said no limit, leave it null — the row
        // reader still stops at MaxRows, but the SELECT remains plain.
        if (spec.Limit.HasValue && spec.Limit.Value > _options.MaxRows)
            spec.Limit = _options.MaxRows;

        if (spec.Offset.HasValue && spec.Offset.Value < 0) spec.Offset = null;
        // Default a page size when offset is given alone — keeps the result bounded.
        if (spec.Offset.HasValue && spec.Offset.Value > 0 && !spec.Limit.HasValue)
            spec.Limit = _options.MaxRows;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var c in spec.Select)
        {
            if (LooksLikeColumnExpression(c)) AddTablesFromExpression(c, referenced);
            else AddTable(c, referenced);
        }
        foreach (var c in spec.GroupBy)
        {
            if (LooksLikeColumnExpression(c)) AddTablesFromExpression(c, referenced);
            else AddTable(c, referenced);
        }
        foreach (var o in spec.OrderBy) AddTable(o.Column, referenced);
        foreach (var f in spec.Filters) AddTable(f.Column, referenced);
        foreach (var a in spec.Aggregations)
        {
            if (a.Column == "*") continue;
            // Inline expressions (CASE WHEN, FORMAT, DATEDIFF, etc.) reference tables that the
            // bare-AddTable path can't extract. Scan the expression text so the join graph
            // discovers e.g. TicketStatuses in a SUM(CASE WHEN TicketStatuses.Name=... ).
            if (LooksLikeColumnExpression(a.Column))
            {
                AddTablesFromExpression(a.Column, referenced);
                continue;
            }
            if (a.Column.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
            {
                // Folded inline expression (RewriteInvalidStarAggregates) — scan the expression
                // text for table refs so the join graph still picks them up.
                AddTablesFromExpression(a.Column.Substring("expr:".Length), referenced);
            }
            else
            {
                AddTable(a.Column, referenced);
            }
        }
        // Computed expressions can reference any table — scan their text for known table names.
        foreach (var comp in spec.Computed) AddTablesFromExpression(comp.Expression, referenced);

        // Explicit joins (Tier 2: LEFT/anti-join support). Each declared table also needs to
        // participate in join resolution even if no SELECT/Filter mentions it.
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table)) referenced.Add(j.Table);

        var joinEdges = _joinResolver.Resolve(
            spec.Root,
            referenced.Where(t => !string.Equals(t, spec.Root, StringComparison.OrdinalIgnoreCase)));

        // B12 — disambiguate multi-FK joins. When two tables are connected by several FKs
        // (e.g. Tickets.CreatedByUserId AND Tickets.AssignedToUserId both → AspNetUsers),
        // the resolver picks one arbitrarily via Dijkstra. If the user's spec mentions a
        // specific FK column, swap the chosen edge to match. Without this, "distinct users
        // who CREATED tickets" silently joins through AssignedToUserId and returns the wrong
        // count.
        joinEdges = DisambiguateMultiFkJoins(joinEdges, spec).ToList();

        // Build join-kind lookup so BuildFrom can emit INNER vs LEFT vs anti.
        var joinKindByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table))
                joinKindByTable[j.Table] = (j.Kind ?? "inner").ToLowerInvariant();

        // 2026-05-30 — JoinSpec.Alias support for self-joins (the iter 2 self-join hierarchical
        // pattern that DropUnresolvedSelectColumnsRule previously dropped). When an alias is
        // declared AND it differs from the table name, BuildFrom emits `JOIN [Table] AS [Alias]`
        // and the ON clause's right-hand side references the alias instead of the table. Empty
        // alias keeps the existing `AS [Table]` emission byte-identical.
        var aliasByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table) && !string.IsNullOrEmpty(j.Alias)
                && !string.Equals(j.Table, j.Alias, StringComparison.OrdinalIgnoreCase))
                aliasByTable[j.Table] = j.Alias;

        // B8 — auto-promote INNER → <configured projection kind> when joining for display only
        // through a nullable FK. "Tickets per assignee" silently drops unassigned tickets because
        // the default INNER JOIN filters them out. We only promote when the LLM didn't already
        // pick a kind AND no filter references the target table (a filter on the joined side is
        // a "must match" signal — keep INNER). Anti-joins are untouched, they have their own
        // semantics. The promotion target is operator-configurable via
        // <see cref="CopilotOptions.DefaultProjectionJoinKind"/> ("left" by default; "inner"
        // disables auto-promotion).
        var projectionKind = NormalizeProjectionKind(_options.DefaultProjectionJoinKind);
        if (!string.Equals(projectionKind, SpecConst.JoinKinds.Inner, StringComparison.OrdinalIgnoreCase))
        {
            var filteredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in spec.Filters)
            {
                var (ft, _) = SplitQualified(f.Column ?? "");
                if (!string.IsNullOrEmpty(ft)) filteredTables.Add(ft);
            }
            foreach (var edge in joinEdges)
            {
                if (joinKindByTable.ContainsKey(edge.TargetTable)) continue;        // LLM said something explicit
                if (filteredTables.Contains(edge.TargetTable)) continue;            // filter on target = "must match"
                // Look up the FK column's nullability via the catalog (use the Fk metadata on the edge).
                var fk = edge.Fk;
                if (fk is null) continue;
                var col = _catalog.GetColumns(fk.ParentTable).FirstOrDefault(ci =>
                    string.Equals(ci.ColumnName, fk.ParentColumn, StringComparison.OrdinalIgnoreCase));
                if (col is null || !col.IsNullable) continue;
                joinKindByTable[edge.TargetTable] = projectionKind;
            }
        }

        var sb = new StringBuilder();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        int paramIndex = 0;

        // Period-comparison shape: when the planner populates PeriodComparisons[], emit one
        // SELECT per leg union-all'd together with the leg's Label projected as a literal
        // "Period" column. Each leg layers its own filters over the base spec's filters.
        // This produces N rows (one per period), each carrying the period label and the
        // aggregated value — different from the conditional-aggregation shape (one row, N
        // columns) the verified-query store uses for the same intent. Both are valid; this
        // is the path for novel period questions the LLM emits when verified-store doesn't
        // match.
        if (spec.PeriodComparisons.Count > 0)
            return EmitPeriodComparison(spec, joinEdges, joinKindByTable);

        ApplyDistinctGroupByNormalization(spec);
        PromoteGroupByPkToLabel(spec);
        // DropAggregatedColumnsFromSelect is now a SpecRepair phase (runs upstream).
        ApplyAutoGroupByInference(spec);
        // Aggregate-only queries return one row; drop any offset rather than synthesize an illegal ORDER BY.
        if (spec.Offset.HasValue && spec.Offset.Value > 0
            && spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null)
            && spec.GroupBy.Count == 0)
        {
            spec.Offset = null;
        }
        EnsureOrderByForPagination(spec);

        BuildSelect(spec, sb);
        BuildFrom(spec, joinEdges, joinKindByTable, aliasByTable, sb);
        BuildWhere(spec, sb, parameters, ref paramIndex, joinEdges, joinKindByTable);
        BuildGroupBy(spec, sb);
        BuildHaving(spec, sb, parameters, ref paramIndex);
        BuildOrderBy(spec, sb);
        BuildOffsetFetch(spec, sb);

        return new CompiledSql(
            sb.ToString().TrimEnd() + ";",
            parameters,
            Warnings: _warnings.Count > 0 ? _warnings : null);
    }

    // T-SQL OFFSET/FETCH requires ORDER BY. Pick deterministically without hardcoded column names:
    //   1. first GROUP BY column (always legal under aggregations) →
    //   2. semantic-layer NaturalKeyColumn (stable, human-meaningful) →
    //   3. live-schema PK (always present on a real table) →
    //   4. abort (don't synthesize garbage)
    private void EnsureOrderByForPagination(QuerySpec spec)
    {
        if (!(spec.Offset is > 0)) return;
        if (spec.OrderBy.Count > 0) return;
        if (string.IsNullOrEmpty(spec.Root)) return;

        if (spec.GroupBy.Count > 0)
        {
            spec.OrderBy.Add(new OrderBySpec { Column = spec.GroupBy[0], Direction = "asc" });
            return;
        }

        var entity = _semanticLayer.GetEntityForTable(spec.Root);
        string? column = null;
        if (entity is not null
            && !string.IsNullOrWhiteSpace(entity.NaturalKeyColumn)
            && _catalog.ColumnExists(spec.Root, entity.NaturalKeyColumn!))
        {
            column = entity.NaturalKeyColumn;
        }
        column ??= ResolvePrimaryKey(spec.Root);
        if (string.IsNullOrEmpty(column)) return;
        spec.OrderBy.Add(new OrderBySpec { Column = $"{spec.Root}.{column}", Direction = "asc" });
    }

    private void BuildOffsetFetch(QuerySpec spec, StringBuilder sb)
    {
        // For MSSQL the limit travels via TOP in BuildSelect (LimitGoesBeforeColumns=true); this
        // clause only fires when offset > 0 and emits OFFSET-FETCH. For dialects with a trailing
        // LIMIT (Postgres / SQLite / MySQL), the dialect emits "LIMIT N [OFFSET M]" here for any
        // non-zero bound and BuildSelect's TopClause was a no-op.
        var clause = _dialect.LimitOffsetClause(spec.Limit, spec.Offset);
        if (string.IsNullOrEmpty(clause)) return;
        sb.AppendLine(clause);
    }

    // GROUP BY without aggregations is almost always wrong — it forces SQL Server to reject any non-grouped SELECT column (error 8120). The LLM occasionally emits a stray GROUP BY on a question that should be a plain list. Drop it.
    // Also covers the previous DISTINCT+GROUP BY case: DISTINCT subsumes GROUP BY when there are no aggregations.
    private void ApplyDistinctGroupByNormalization(QuerySpec spec)
    {
        if (spec.GroupBy.Count == 0) return;
        var hasAggs = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null)
                      || spec.Select.Any(LooksLikeAggregateExpression)
                      || spec.Computed.Any(c => LooksLikeAggregateExpression(c.Expression));
        if (!hasAggs) spec.GroupBy.Clear();
    }

    // Auto-GROUP-BY inference: when the spec has aggregations + non-aggregate SELECT
    // columns but the planner forgot to populate GroupBy, infer it from those columns.
    // Without this fix, BuildSelect silently drops the non-aggregate columns (because
    // they're not in GROUP BY) and the user gets a scalar count instead of the per-group
    // breakdown they asked for.
    // GROUP BY on a PK column with aggregations produces one-row-per-row counts (COUNT(*)=1 always) — useless.
    // Promote `GROUP BY <T>.<PK>` → `GROUP BY <T>.<LabelColumn>` using the SEMANTIC LAYER (no hardcoded column-name list).
    private void PromoteGroupByPkToLabel(QuerySpec spec)
    {
        if (spec.GroupBy.Count == 0) return;
        var hasAggs = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null)
                      || spec.Select.Any(LooksLikeAggregateExpression);
        if (!hasAggs) return;
        for (int i = 0; i < spec.GroupBy.Count; i++)
        {
            var (table, col) = SplitQualified(spec.GroupBy[i]);
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(col)) continue;
            var pkCol = ResolvePrimaryKey(table);
            if (string.IsNullOrEmpty(pkCol) || !string.Equals(col, pkCol, StringComparison.OrdinalIgnoreCase)) continue;
            var label = ResolveLabelColumn(table);
            if (string.IsNullOrEmpty(label)) continue;
            var newRef = $"{table}.{label}";
            spec.GroupBy[i] = newRef;
            // Mirror in SELECT so the projection follows the GROUP BY swap.
            for (int j = 0; j < spec.Select.Count; j++)
            {
                var (st, sc) = SplitQualified(spec.Select[j]);
                if (string.Equals(st, table, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sc, pkCol, StringComparison.OrdinalIgnoreCase))
                {
                    spec.Select[j] = newRef;
                }
            }
        }
    }

    // First PK column from live introspection — null if the table has no PK declared.
    private string? ResolvePrimaryKey(string table) =>
        _catalog.Snapshot.KeyConstraints
            .Where(k => string.Equals(k.TableName, table, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(k.ConstraintType, "PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.OrdinalPosition).Select(k => k.ColumnName).FirstOrDefault();

    // Semantic-layer-driven label resolution. Priority: EntityDefinition.LabelColumn → first DisplayColumns entry → null.
    // No hardcoded column-name list; if the semantic layer doesn't know a label for the table, we don't guess.
    private string? ResolveLabelColumn(string table)
    {
        var entity = _semanticLayer.GetEntityForTable(table);
        if (entity is null) return null;
        if (!string.IsNullOrWhiteSpace(entity.LabelColumn) && _catalog.ColumnExists(table, entity.LabelColumn!))
            return entity.LabelColumn;
        foreach (var c in entity.DisplayColumns)
            if (!string.IsNullOrWhiteSpace(c) && _catalog.ColumnExists(table, c))
                return c;
        return null;
    }

    private void ApplyAutoGroupByInference(QuerySpec spec)
    {
        // Detect aggregations from Aggregations[] OR from inline AVG()/SUM()/etc. expressions in Select[].
        // Inline form is common when the LLM emits `select:["AVG(X) AS y","Customers.Id AS c"]` instead of populating Aggregations[].
        var hasAggsForInference = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null)
                                  || spec.Select.Any(LooksLikeAggregateExpression);
        if (!hasAggsForInference || spec.GroupBy.Count != 0 || spec.Select.Count == 0) return;

        // C13 — skip raw datetime columns. Inferring GROUP BY off a millisecond-precision datetime produces one bucket per row.
        // Also skip the inline-aggregate expressions themselves (they're the "aggregation side", not the group side).
        foreach (var col in spec.Select)
        {
            if (LooksLikeAggregateExpression(col)) continue;
            if (!TryFormatColumn(col, out _)) continue;
            if (IsRawDateTimeColumn(col)) continue;
            spec.GroupBy.Add(col);
        }
    }

    // Emit one SELECT per period leg, glued with UNION ALL. Each leg's Label is projected
    // as a literal "Period" column so the result identifies which period each row belongs
    // to. Leg filters are layered on top of the base spec's filters. The base spec's
    // ORDER BY and LIMIT are dropped (UNION ALL semantics make per-leg ORDER BY illegal and
    // a global ORDER BY would need an outer-query wrap — out of scope for the initial
    // emitter).
    private CompiledSql EmitPeriodComparison(
        QuerySpec spec,
        IReadOnlyList<FkEdge> joinEdges,
        IReadOnlyDictionary<string, string> joinKindByTable)
    {
        var sb = new StringBuilder();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        int paramIndex = 0;
        bool first = true;

        foreach (var leg in spec.PeriodComparisons)
        {
            if (!first) { sb.AppendLine().AppendLine("UNION ALL"); }
            first = false;

            var legSpec = CloneSpecForPeriodLeg(spec, leg);
            ApplyDistinctGroupByNormalization(legSpec);
            ApplyAutoGroupByInference(legSpec);

            BuildSelect(legSpec, sb);
            // Aliases for self-joins from spec.Joins[].Alias on the leg's own spec.
            var legAliasByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in legSpec.Joins)
                if (!string.IsNullOrEmpty(j.Table) && !string.IsNullOrEmpty(j.Alias)
                    && !string.Equals(j.Table, j.Alias, StringComparison.OrdinalIgnoreCase))
                    legAliasByTable[j.Table] = j.Alias;
            BuildFrom(legSpec, joinEdges, joinKindByTable, legAliasByTable, sb);
            BuildWhere(legSpec, sb, parameters, ref paramIndex, joinEdges, joinKindByTable);
            BuildGroupBy(legSpec, sb);
            BuildHaving(legSpec, sb, parameters, ref paramIndex);
        }

        return new CompiledSql(
            sb.ToString().TrimEnd() + ";",
            parameters,
            Warnings: _warnings.Count > 0 ? _warnings : null);
    }

    // Shallow-clone the spec with leg-specific overlays:
    //   • Filters = base.Filters concat leg.Filters (leg layers on top)
    //   • Computed += { Alias = "Period", Expression = "'<label>'" } so every leg's SELECT
    //     carries the label as a literal column (column shape stays identical across legs,
    //     which UNION ALL requires).
    //   • PeriodComparisons cleared on the clone so EmitPeriodComparison doesn't recurse.
    private static QuerySpec CloneSpecForPeriodLeg(QuerySpec spec, PeriodSpec leg)
    {
        var clone = new QuerySpec
        {
            Root = spec.Root,
            Intent = spec.Intent,
            Select = new List<string>(spec.Select),
            Filters = new List<FilterSpec>(spec.Filters),
            GroupBy = new List<string>(spec.GroupBy),
            OrderBy = new List<OrderBySpec>(spec.OrderBy),
            Aggregations = new List<AggregateSpec>(spec.Aggregations),
            Computed = new List<ComputedSpec>(spec.Computed),
            Having = new List<HavingSpec>(spec.Having),
            Joins = new List<JoinSpec>(spec.Joins),
            Limit = spec.Limit,
            // UNION ALL legs can't carry independent OFFSET; would need outer-SELECT wrap.
            Offset = null,
            Distinct = spec.Distinct,
            ClarificationQuestion = spec.ClarificationQuestion,
            // PeriodComparisons intentionally empty.
        };
        if (leg.Filters is { Count: > 0 })
        {
            foreach (var f in leg.Filters)
                clone.Filters.Add(new FilterSpec { Column = f.Column, Op = f.Op, Value = f.Value });
        }
        var safeLabel = (leg.Label ?? string.Empty).Replace("'", "''");
        clone.Computed.Add(new ComputedSpec { Alias = "Period", Expression = $"'{safeLabel}'" });
        return clone;
    }

    // ExpandSemanticReferences moved to ISemanticExpander.Expand (2026-06-01). Compiler calls
    // _semanticExpander.Expand(spec) — see Compilation/ISemanticExpander.cs.

    /// <summary>
    /// Recovers from <c>AVG/SUM/MIN/MAX(*)</c> which the LLM planner emits sometimes — invalid
    /// T-SQL because only <c>COUNT</c> accepts <c>*</c>. When we can identify the per-row
    /// expression the user wanted aggregated (it lives in <see cref="QuerySpec.Computed"/>), fold
    /// the expression into the aggregation's <see cref="AggregateSpec.Column"/> slot using the
    /// <c>expr:</c> sentinel prefix so <see cref="BuildSelect"/> emits <c>AVG(&lt;expr&gt;)</c>
    /// inline. The matching computed entry is removed so it isn't double-emitted as a separate
    /// SELECT column.
    /// <para>Two heuristics for matching aggregate to computed (in priority order):
    /// <list type="number">
    ///   <item>Exactly one Computed in the spec → that's the one the planner intended.</item>
    ///   <item>Alias-prefix match: aggregate alias <c>"AvgAgeDays"</c> → computed alias <c>"AgeDays"</c>.</item>
    /// </list></para>
    /// <para>Conservative on no-match: leaves the bad aggregation alone so downstream stages
    /// (<see cref="BuildSelect"/> already <c>continue</c>s on unresolvable columns) drop it.</para>
    /// </summary>
    private void RewriteInvalidStarAggregates(QuerySpec spec)
    {
        // Iterate by index but use a stable copy of Computed so multiple aggregates can fold
        // different Computed entries in the same pass without index drift.
        for (int i = 0; i < spec.Aggregations.Count; i++)
        {
            var a = spec.Aggregations[i];
            var fn = NormalizeAggFn(a.Function);
            if (fn is null || fn == SpecConst.Aggregates.Count) continue;     // COUNT(*) is fine; only the others
            if (a.Column != "*") continue;

            ComputedSpec? folded = null;
            if (spec.Computed.Count == 1)
            {
                folded = spec.Computed[0];
            }
            else if (spec.Computed.Count > 1)
            {
                // Prefer alias-prefix match: aggregate alias "AvgAgeDays" → computed alias "AgeDays".
                if (!string.IsNullOrEmpty(a.Alias))
                {
                    var aggBare = StripAggregatePrefix(a.Alias);
                    folded = spec.Computed.FirstOrDefault(c =>
                        string.Equals(c.Alias, aggBare, StringComparison.OrdinalIgnoreCase));
                }
                // E — Multi-Computed fallback: if alias-prefix didn't match, try aliases that
                // CONTAIN the bare-aggregate name ("ResolutionDays" matched by aggregate "AvgRes").
                // Last resort: pick the first Computed whose expression looks numeric/date math
                // (DATEDIFF / CAST / + - * /). Better to fold something than emit AVG(*).
                if (folded is null && !string.IsNullOrEmpty(a.Alias))
                {
                    var aggBare = StripAggregatePrefix(a.Alias);
                    folded = spec.Computed.FirstOrDefault(c =>
                        c.Alias.IndexOf(aggBare, StringComparison.OrdinalIgnoreCase) >= 0
                        || aggBare.IndexOf(c.Alias, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                if (folded is null)
                {
                    folded = spec.Computed.FirstOrDefault(c =>
                        LooksNumericExpression(c.Expression));
                }
                // Absolute fallback — first computed entry. Better than silently dropping the AVG.
                if (folded is null) folded = spec.Computed[0];
            }
            if (folded is null) continue;

            // Use a sentinel prefix so BuildSelect emits the expression verbatim instead of
            // calling TryFormatColumn (which would reject a non-Table.Column string).
            a.Column = "expr:" + folded.Expression;
            spec.Computed.Remove(folded);
        }
    }

    /// <summary>Conservative "this expression aggregates a number" heuristic for the multi-Computed
    /// fallback in <see cref="RewriteInvalidStarAggregates"/>. Matches DATEDIFF / CAST / arithmetic
    /// patterns commonly used for per-row math. False positives are fine — at worst we fold the
    /// wrong Computed and the LLM retry catches it.</summary>
    private static bool LooksNumericExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var t = expr.TrimStart();
        return t.StartsWith("DATEDIFF", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("CAST", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("CONVERT", StringComparison.OrdinalIgnoreCase)
            || t.Contains('+') || t.Contains('-') || t.Contains('*') || t.Contains('/');
    }

    /// <summary>
    /// Final-pass alias-dedup helper for BuildSelect. Walks the list of <c>expr AS &lt;quotedAlias&gt;</c>
    /// strings and renames duplicates to <c>alias_2</c>, <c>alias_3</c>, etc. Items without an explicit
    /// alias (a bare column ref) are left alone — collision-handling for those happens in the bare-
    /// name pre-pass at the top of BuildSelect. The parser uses the dialect's identifier-quote chars,
    /// so the dedup works for any dialect (T-SQL <c>[…]</c>, Postgres <c>"…"</c>).
    /// </summary>
    private void DedupeAliasesInPlace(List<string> items)
    {
        var qOpen = _dialect.IdentifierQuoteOpen;
        var qClose = _dialect.IdentifierQuoteClose;
        var asMarker = " AS " + qOpen;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            // Match "... AS <quotedAlias>" at the very end. Anchor on " AS " + qOpen + ... + qClose.
            var asIdx = item.LastIndexOf(asMarker, StringComparison.OrdinalIgnoreCase);
            if (asIdx < 0 || item.Length == 0 || item[^1] != qClose) continue;
            var aliasStart = asIdx + asMarker.Length;
            var alias = item.Substring(aliasStart, item.Length - aliasStart - 1);   // trim trailing qClose
            if (string.IsNullOrEmpty(alias)) continue;

            if (!seen.TryAdd(alias, 1)) // already-used alias
            {
                seen[alias]++;
                var newAlias = $"{alias}_{seen[alias]}";
                // Rebuild item: keep everything before " AS ", append re-quoted new alias.
                items[i] = item.Substring(0, asIdx) + " AS " + _dialect.QuoteIdentifier(newAlias);
                seen[newAlias] = 1;     // reserve the new alias too
            }
        }
    }

    /// <summary>
    /// Strip aggregate-prefix from an alias so "AvgAgeDays" matches Computed alias "AgeDays".
    /// Prefix list comes from <c>CopilotOptions.AggregateAliasPrefixes</c> so multilingual
    /// deployments can add Arabic / French variants without recompiling.
    /// </summary>
    private string StripAggregatePrefix(string s)
    {
        var prefixes = _options.AggregateAliasPrefixes;
        if (prefixes is null || prefixes.Count == 0) return s;
        foreach (var p in prefixes)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (s.Length > p.Length && s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return s.Substring(p.Length);
        }
        return s;
    }

    /// <summary>
    /// Auto-injects "WHERE &lt;Root.SoftDeleteColumn&gt; = &lt;value&gt;" when the semantic layer
    /// declares one for the root entity and the planner didn't already filter on that column.
    /// </summary>
    private void InjectSoftDeleteFilter(QuerySpec spec)
    {
        var sd = _semanticLayer.GetSoftDeleteFilter(spec.Root);
        if (sd is null) return;
        var (col, val) = sd.Value;
        if (!_catalog.ColumnExists(spec.Root, col.Substring(col.IndexOf('.') + 1))) return;
        var alreadyFiltered = spec.Filters.Any(f =>
            string.Equals(f.Column, col, StringComparison.OrdinalIgnoreCase));
        if (alreadyFiltered) return;
        spec.Filters.Add(new FilterSpec { Column = col, Op = SpecConst.FilterOps.Eq, Value = val });
    }

    private void AddTablesFromExpression(string expression, HashSet<string> set)
    {
        if (string.IsNullOrEmpty(expression)) return;
        // Cheap heuristic: scan for "<Word>." patterns and add any that match a real table.
        // ScriptDom-level parsing is overkill here — the validator runs after compile and
        // catches any genuinely-invalid references.
        var span = expression.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            if (!char.IsLetter(span[i]) && span[i] != '_') continue;
            int start = i;
            while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_')) i++;
            if (i < span.Length && span[i] == '.')
            {
                var word = span[start..i].ToString();
                if (_catalog.TableExists(word)) set.Add(word);
            }
        }
    }

    // BuildSelect + DefaultDisplayColumns moved to SqlCompiler.Select.cs.

    /// <summary>
    /// Quote/bracket-aware rewrite of column tokens inside a free-form expression.
    ///
    /// Recognises and qualifies these forms (when the catalog confirms the names):
    /// <list type="bullet">
    ///   <item><c>Table.Column</c> → <c>[Table].[Column]</c></item>
    ///   <item><c>[Table].[Column]</c> → already-qualified; copied verbatim</item>
    ///   <item><c>[Table].Column</c> or <c>Table.[Column]</c> → upgraded to fully-bracketed form</item>
    ///   <item>Bare <c>Column</c> (no dot, not a function call, not a reserved date-part keyword)
    ///         when <paramref name="rootTable"/> is provided and the column exists on it →
    ///         <c>[&lt;rootTable&gt;].[Column]</c></item>
    /// </list>
    ///
    /// Skipped (copied verbatim with no auto-qualification — important to avoid corrupting them):
    /// <list type="bullet">
    ///   <item>Single-quoted string literals: <c>'...'</c> with SQL Server's <c>''</c> escape</item>
    ///   <item>Double-quoted identifiers: <c>"..."</c></item>
    /// </list>
    ///
    /// The previous version was a single-pass identifier scanner that didn't track string-literal
    /// state; it would WRONGLY qualify a bare column name appearing inside a string literal, e.g.
    /// the <c>'CreatedAt is null'</c> in <c>CASE WHEN ... THEN 'CreatedAt is null' END</c> would
    /// have <c>CreatedAt</c> mangled into <c>[Tickets].[CreatedAt]</c> inside the literal — broken
    /// SQL. Now string literals and quoted identifiers are skipped entirely.
    /// </summary>
    // PII denylist (#55) — defense in depth. BuildSelect already drops sensitive bare columns
    // from spec.Select. This helper extends the same check to ANY inline expression: computed
    // expressions, aggregation expressions, ORDER BY columns. Returns true if the expression
    // contains a qualified reference to a sensitive column (per ISemanticLayer).
    //
    // Reuses QualifiedRefPattern from QuerySpecAccessPolicyValidator's regex idiom — same shape,
    // ensuring "what counts as a qualified ref" cannot drift between the safety validator and
    // this PII check.
    private static readonly System.Text.RegularExpressions.Regex PiiQualifiedRefPattern =
        new(@"(?<![\w])\[?(?<table>[A-Za-z_][A-Za-z0-9_]*)\]?\s*\.\s*\[?(?<column>[A-Za-z_][A-Za-z0-9_]*)\]?",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private bool ExpressionReferencesSensitiveColumn(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        foreach (System.Text.RegularExpressions.Match m in PiiQualifiedRefPattern.Matches(expression))
        {
            var table = m.Groups["table"].Value;
            var column = m.Groups["column"].Value;
            if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(column)
                && _semanticLayer.IsSensitiveColumn(table, column))
                return true;
        }
        return false;
    }

    private string QualifyColumnsInExpression(string expression, string? rootTable = null)
    {
        if (string.IsNullOrEmpty(expression)) return expression;
        var sb = new StringBuilder(expression.Length + 16);
        int i = 0;
        int n = expression.Length;
        while (i < n)
        {
            var ch = expression[i];

            // ── String literal '...': skip until closing quote, honoring '' escape ──
            if (ch == '\'')
            {
                sb.Append(ch);
                i++;
                while (i < n)
                {
                    var c = expression[i];
                    sb.Append(c);
                    i++;
                    if (c == '\'')
                    {
                        // SQL Server: '' inside a string is an escaped single quote, not a closer.
                        if (i < n && expression[i] == '\'') { sb.Append(expression[i]); i++; continue; }
                        break;
                    }
                }
                continue;
            }

            // ── Double-quoted identifier "...": skip until closing " ──
            if (ch == '"')
            {
                sb.Append(ch);
                i++;
                while (i < n)
                {
                    var c = expression[i];
                    sb.Append(c);
                    i++;
                    if (c == '"') break;
                }
                continue;
            }

            // ── Bracketed identifier [Foo Bar]: extract the inner name, then check for a
            //    following '.<col>' or '.[col]' to upgrade Table.Column. Bare bracketed
            //    identifiers (no following dot) we treat as already-qualified and copy verbatim.
            if (ch == '[')
            {
                var (innerName, consumed) = ReadBracketed(expression, i);
                if (innerName is not null)
                {
                    int afterFirst = i + consumed;
                    // Check for ".<word>" or ".[word]" continuation → Table.Column form.
                    if (afterFirst < n && expression[afterFirst] == '.')
                    {
                        var (colName, colConsumed) = ReadIdentifierAfterDot(expression, afterFirst);
                        if (colName is not null)
                        {
                            EmitTableColumn(sb, innerName, colName, rootTable);
                            i = afterFirst + colConsumed;
                            continue;
                        }
                    }
                    // Bare bracketed identifier — could be a bare column on root. Try to upgrade.
                    if (!string.IsNullOrEmpty(rootTable)
                        && !_dialect.DatePartKeywords.Contains(innerName)
                        && _catalog.ColumnExists(rootTable, innerName))
                    {
                        sb.Append(_dialect.QuoteQualified(rootTable, innerName));
                    }
                    else
                    {
                        // Not a known column or no root context — copy bracket-form verbatim.
                        sb.Append(expression, i, consumed);
                    }
                    i += consumed;
                    continue;
                }
                // Malformed [ with no closing ] — defensive: just emit and move on.
                sb.Append(ch);
                i++;
                continue;
            }

            // ── Temporal/parameter placeholder @token ──────────────────────────────────
            //    Examples consumed by TryExpandTemporalToken: @today, @yesterday,
            //    @week_start, @last_week_start, @month_start, @weeks:-3, @days:7,
            //    @yearmonth:2027:1, @q1_start. Without this branch the tokens pass through
            //    verbatim into SELECT / CASE WHEN / aggregation expressions (the WHERE-builder
            //    expands them correctly, but inline expressions never went through that path).
            //    Trace 5 (2026-05-30 "Outages this week compared to last week") failed at
            //    SQL execution because SUM(CASE WHEN ... >= @week_start ...) reached SQL Server
            //    with the literal "@week_start" still in place — invalid syntax.
            //
            //    Token grammar matches the producer in TryExpandTemporalToken:
            //      @<name>            — letters/underscores
            //      @<name>:<int>      — colon + signed integer offset (e.g. @weeks:-3)
            //      @<name>:<int>:<int> — used by @yearmonth:Y:M
            //    Unknown tokens are copied verbatim — same fail-safe as the WHERE-builder.
            if (ch == '@')
            {
                int start = i;
                i++; // consume '@'
                while (i < n && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                // Optional ":<signed-int>" once or twice.
                for (int k = 0; k < 2; k++)
                {
                    if (i < n && expression[i] == ':')
                    {
                        i++;
                        if (i < n && (expression[i] == '-' || expression[i] == '+')) i++;
                        while (i < n && char.IsDigit(expression[i])) i++;
                    }
                    else break;
                }
                var token = expression.Substring(start, i - start);
                if (_temporalTokenizer.TryExpand(token, _dialect, out var sqlExpr))
                    sb.Append(sqlExpr);
                else
                    sb.Append(token);    // unknown token — fail-safe, leave verbatim
                continue;
            }

            // ── Bare identifier (Letter/Underscore start) ──
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                var word = expression[start..i];

                // Table.Column or Table.[Column]
                if (i < n && expression[i] == '.')
                {
                    var (colName, colConsumed) = ReadIdentifierAfterDot(expression, i);
                    if (colName is not null)
                    {
                        EmitTableColumn(sb, word, colName, rootTable);
                        i += colConsumed;
                        continue;
                    }
                }
                // Function call: identifier followed by '(' — leave verbatim (DATEDIFF, GETDATE, ...).
                if (i < n && expression[i] == '(')
                {
                    sb.Append(word);
                    continue;
                }
                // Bare column on root entity?
                if (!string.IsNullOrEmpty(rootTable)
                    && !_dialect.DatePartKeywords.Contains(word)
                    && _catalog.ColumnExists(rootTable, word))
                {
                    sb.Append(_dialect.QuoteQualified(rootTable, word));
                }
                else
                {
                    sb.Append(word);
                }
                continue;
            }

            // ── Anything else (operators, whitespace, punctuation): copy verbatim ──
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emit "[Table].[Column]" when both names exist in the catalog; otherwise emit
    /// "&lt;Table&gt;.&lt;Column&gt;" verbatim (cleaning brackets either way for canonical output).
    /// </summary>
    private void EmitTableColumn(StringBuilder sb, string tableName, string columnName, string? rootTable)
    {
        if (_catalog.TableExists(tableName) && _catalog.ColumnExists(tableName, columnName))
            sb.Append(_dialect.QuoteQualified(tableName, columnName));
        else
            sb.Append(tableName).Append('.').Append(columnName);
    }

    /// <summary>
    /// Reads a bracketed identifier <c>[Foo Bar]</c> starting at <paramref name="start"/> (which
    /// must be the opening <c>[</c>). Returns (innerName, charsConsumedIncludingBrackets) on
    /// success or (null, 0) when no closing <c>]</c> is found within the expression.
    /// </summary>
    private static (string? Name, int Consumed) ReadBracketed(string s, int start)
    {
        if (start >= s.Length || s[start] != '[') return (null, 0);
        int end = s.IndexOf(']', start + 1);
        if (end < 0) return (null, 0);
        var inner = s.Substring(start + 1, end - start - 1);
        return (inner, end - start + 1);
    }

    /// <summary>
    /// Reads a "<c>.</c>&lt;ident&gt;" or "<c>.</c>[ident]" suffix. Caller passes the position of
    /// the dot. Returns (columnName, charsConsumedIncludingDot) on success or (null, 0) when
    /// no valid identifier follows.
    /// </summary>
    private static (string? Name, int Consumed) ReadIdentifierAfterDot(string s, int dotIndex)
    {
        if (dotIndex >= s.Length || s[dotIndex] != '.') return (null, 0);
        int p = dotIndex + 1;
        if (p >= s.Length) return (null, 0);
        if (s[p] == '[')
        {
            var (br, used) = ReadBracketed(s, p);
            if (br is null) return (null, 0);
            return (br, 1 + used); // dot + bracketed
        }
        // Bare identifier
        int colStart = p;
        while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
        if (p == colStart) return (null, 0);
        return (s.Substring(colStart, p - colStart), p - dotIndex);
    }

    /// <summary>
    /// Strips a trailing alias clause ("AS [Foo]" / "AS Foo" / " Foo" at top level) from a
    /// computed expression so the compiler's own "AS [<alias>]" suffix doesn't collide with one
    /// the planner already wrote. Conservative: only strips when the trailing " AS <ident>"
    /// pattern appears OUTSIDE any open paren (so we don't touch DATEDIFF-internal aliases).
    /// </summary>
    private static string StripTrailingAlias(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return expression;
        var trimmed = expression.TrimEnd();

        // Find the position outside any paren depth where " AS " (case-insensitive) appears.
        int depth = 0;
        int asAt = -1;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (depth == 0 && i + 4 <= trimmed.Length
                     && (ch == ' ' || ch == '\t')
                     && (trimmed[i + 1] == 'A' || trimmed[i + 1] == 'a')
                     && (trimmed[i + 2] == 'S' || trimmed[i + 2] == 's')
                     && (trimmed[i + 3] == ' ' || trimmed[i + 3] == '\t' || trimmed[i + 3] == '['))
            {
                asAt = i;  // record latest top-level AS — strip from here
            }
        }
        if (asAt > 0) return trimmed.Substring(0, asAt).TrimEnd();
        return trimmed;
    }

    // 2026-06-01 — DatePartKeywords moved to ISqlDialect.DatePartKeywords so porting to a new
    // SQL engine (PG, MySQL, SQLite) doesn't require touching the compiler. Callers use
    // _dialect.DatePartKeywords.

    private static string? NormalizeAggFn(string fn) => fn?.ToUpperInvariant() switch
    {
        SpecConst.Aggregates.Count => SpecConst.Aggregates.Count,
        SpecConst.Aggregates.Sum   => SpecConst.Aggregates.Sum,
        SpecConst.Aggregates.Avg   => SpecConst.Aggregates.Avg,
        SpecConst.Aggregates.Min   => SpecConst.Aggregates.Min,
        SpecConst.Aggregates.Max   => SpecConst.Aggregates.Max,
        _ => null,
    };

    /// <summary>
    /// Heuristic: returns true if the expression starts with a SQL aggregate function call.
    /// Used by ORDER-BY-safety to know whether a computed-column alias was an aggregate metric.
    /// </summary>
    private static bool LooksLikeAggregateExpression(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return false;
        var trimmed = expression.TrimStart();
        return trimmed.StartsWith("COUNT(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("COUNT_BIG(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SUM(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("AVG(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MIN(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MAX(", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when the string looks like a non-trivial column expression rather than
    /// a bare <c>Table.Column</c> reference — i.e. a CASE WHEN, a function call (CAST, FORMAT,
    /// DATEDIFF, ISNULL, COALESCE, etc.), or anything else containing parens / whitespace.
    /// Used by BuildSelect to route aggregation columns and select items down the inline-
    /// expression rendering path instead of letting TryFormatColumn drop them silently.
    /// </summary>
    private static bool LooksLikeColumnExpression(string columnRef)
    {
        if (string.IsNullOrWhiteSpace(columnRef)) return false;
        // Parens, brackets-with-spaces, or any embedded whitespace = expression, not a ref.
        if (columnRef.Contains('(') || columnRef.Contains(')')) return true;
        // A "Table.Column" is the only valid bare form; anything with internal whitespace is an expr.
        var trimmed = columnRef.Trim();
        return trimmed.Contains(' ') || trimmed.Contains('\t');
    }

    /// <summary>
    /// B12 — when an FkEdge connects two tables that have multiple FK constraints between them,
    /// prefer the FK whose ParentColumn (the column on the FK side) is mentioned somewhere in
    /// the spec's context (Select / Aggregations / GroupBy / OrderBy / Filters). This is how we
    /// distinguish "join on CreatedByUserId" from "join on AssignedToUserId" when both go to
    /// AspNetUsers — the resolver's path is correct topologically but arbitrary among parallel
    /// FKs. Falls back to the resolver's choice when no context column matches any FK.
    /// </summary>
    private IEnumerable<FkEdge> DisambiguateMultiFkJoins(IReadOnlyList<FkEdge> edges, QuerySpec spec)
    {
        var ctxCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCtx(spec.Select);
        AddCtx(spec.GroupBy);
        foreach (var a in spec.Aggregations)
            if (!string.IsNullOrEmpty(a.Column) && a.Column != "*") ctxCols.Add(NormalizeColRef(a.Column));
        foreach (var o in spec.OrderBy)
            if (!string.IsNullOrEmpty(o.Column)) ctxCols.Add(NormalizeColRef(o.Column));
        foreach (var f in spec.Filters)
            if (!string.IsNullOrEmpty(f.Column)) ctxCols.Add(NormalizeColRef(f.Column));

        void AddCtx(IEnumerable<string> refs)
        {
            foreach (var r in refs) if (!string.IsNullOrEmpty(r)) ctxCols.Add(NormalizeColRef(r));
        }

        if (ctxCols.Count == 0) { foreach (var e in edges) yield return e; yield break; }

        var allFks = _catalog.Snapshot.ForeignKeys;
        foreach (var edge in edges)
        {
            // Look for parallel FK constraints between the same source/target pair (in either
            // direction — the bidirectional graph means edge.SourceTable might be the
            // referenced side).
            var siblings = allFks.Where(fk =>
                (string.Equals(fk.ParentTable, edge.Fk.ParentTable, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(fk.ReferencedTable, edge.Fk.ReferencedTable, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(fk.ParentTable, edge.Fk.ReferencedTable, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(fk.ReferencedTable, edge.Fk.ParentTable, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (siblings.Count <= 1) { yield return edge; continue; }

            var preferred = siblings.FirstOrDefault(fk =>
                ctxCols.Contains($"{fk.ParentTable}.{fk.ParentColumn}".ToLowerInvariant())
                || ctxCols.Contains($"{fk.ReferencedTable}.{fk.ReferencedColumn}".ToLowerInvariant()));
            if (preferred is null || ReferenceEquals(preferred, edge.Fk))
            {
                yield return edge;
                continue;
            }
            yield return new FkEdge(edge.SourceTable, edge.TargetTable, preferred);
        }
    }

    private static string NormalizeColRef(string s) =>
        (s ?? "").Trim().Replace("[", "").Replace("]", "").ToLowerInvariant();

    /// <summary>
    /// I1 — walk every Table.Column reference in the spec and rewrite the Table portion via
    /// the semantic layer's entity synonym map when the table name doesn't exist in the
    /// catalog. The root receives the same treatment higher up in Compile(); this method
    /// handles every other slot the LLM might emit a table name into.
    /// </summary>
    private void ResolveSynonymsOnColumnRefs(QuerySpec spec)
    {
        for (int i = 0; i < spec.Select.Count; i++)
            spec.Select[i] = RemapTableName(spec.Select[i]);
        for (int i = 0; i < spec.GroupBy.Count; i++)
            spec.GroupBy[i] = RemapTableName(spec.GroupBy[i]);
        foreach (var o in spec.OrderBy)
            if (!string.IsNullOrEmpty(o.Column)) o.Column = RemapTableName(o.Column);
        foreach (var f in spec.Filters)
            if (!string.IsNullOrEmpty(f.Column)) f.Column = RemapTableName(f.Column);
        foreach (var a in spec.Aggregations)
            if (!string.IsNullOrEmpty(a.Column) && a.Column != "*")
                a.Column = RemapTableName(a.Column);
        foreach (var j in spec.Joins)
            if (!string.IsNullOrEmpty(j.Table) && !_catalog.TableExists(j.Table))
            {
                var entity = _semanticLayer.GetEntityByNameOrSynonym(j.Table);
                if (entity is not null && _catalog.TableExists(entity.Table)) j.Table = entity.Table;
            }
    }

    private string RemapTableName(string columnRef)
    {
        if (string.IsNullOrEmpty(columnRef)) return columnRef;
        // Skip raw expressions and unqualified column refs — synonym mapping only applies
        // when the LLM wrote "Table.Column".
        if (columnRef.Contains('(') || columnRef.Contains(' ')) return columnRef;
        var (t, c) = SplitQualified(columnRef);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c)) return columnRef;
        if (_catalog.TableExists(t)) return columnRef;
        var entity = _semanticLayer.GetEntityByNameOrSynonym(t);
        if (entity is null || !_catalog.TableExists(entity.Table)) return columnRef;
        return $"{entity.Table}.{c}";
    }

    /// <summary>
    /// J2 — convert a plural SQL-Server table name into a readable singular alias for
    /// collision aliasing of "Name" columns. "TicketStatuses" → "Status",
    /// "TicketPriorities" → "Priority", "Categories" → "Category". Conservative —
    /// only strips simple plural suffixes; gives up to the safe Table_Column fallback
    /// when the table name doesn't fit a known pluralisation pattern.
    /// </summary>
    private static string SingulariseTableForAlias(string table)
    {
        if (string.IsNullOrEmpty(table)) return table;
        var bare = table;
        if (bare.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^3] + "y";              // Priorities → Priority, Categories → Category
        else if (bare.EndsWith("ses", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^2];                    // Statuses → Status, Classes → Class
        else if (bare.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                 && !bare.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^1];                    // Users → User, Sources → Source
        return string.IsNullOrEmpty(bare) ? table : bare;
    }

    // UnitSuffixes + AppendUnitSuffixIfMissing moved to ISemanticExpander (2026-06-01).

    private void BuildFrom(
        QuerySpec spec,
        IReadOnlyList<FkEdge> joinEdges,
        IReadOnlyDictionary<string, string> joinKindByTable,
        IReadOnlyDictionary<string, string> aliasByTable,
        StringBuilder sb)
    {
        var rootQ = _dialect.QuoteIdentifier(spec.Root);
        sb.Append("FROM ").Append(rootQ).Append(" AS ").Append(rootQ);

        foreach (var edge in joinEdges)
        {
            var fk = edge.Fk;
            // Default INNER JOIN; LEFT JOIN when the spec declares "left" or "anti" for this
            // target table (anti-joins also need an IS NULL filter, added in BuildWhere).
            var kind = joinKindByTable.TryGetValue(edge.TargetTable, out var k) ? k : "inner";
            var joinKeyword = kind is SpecConst.JoinKinds.Left or SpecConst.JoinKinds.Anti ? "LEFT JOIN" : "INNER JOIN";
            var targetQ = _dialect.QuoteIdentifier(edge.TargetTable);
            // 2026-05-30 — when an alias is declared on the JoinSpec, emit `JOIN [Table] AS
            // [Alias]` and use the alias on the ON clause's referenced side. Default (no alias)
            // keeps emission byte-identical to the pre-change behaviour.
            var aliasName = aliasByTable.TryGetValue(edge.TargetTable, out var a) ? a : edge.TargetTable;
            var aliasQ = _dialect.QuoteIdentifier(aliasName);
            sb.AppendLine();
            sb.Append(joinKeyword).Append(' ').Append(targetQ).Append(" AS ").Append(aliasQ);
            // The joined (aliased) table can be EITHER side of the FK: a child→parent lookup
            // (root=Tickets joins referenced Regions) OR a parent→child fan-out (root=Customers
            // joins referencing Bills, FK Bills.CustomerId → Customers.Id). Qualify EACH side by
            // its own table's alias so the latter renders `Bills.CustomerId = Customers.Id`, not
            // the self-referential `Bills.CustomerId = Bills.Id` (2026-06-02 fix). Byte-identical
            // for the child→parent direction, where ReferencedTable == TargetTable == aliasName.
            var parentSide = aliasByTable.TryGetValue(fk.ParentTable, out var pAlias) ? pAlias : fk.ParentTable;
            var referencedSide = aliasByTable.TryGetValue(fk.ReferencedTable, out var rAlias) ? rAlias : fk.ReferencedTable;
            sb.Append(" ON ").Append(_dialect.QuoteQualified(parentSide, fk.ParentColumn));
            sb.Append(" = ").Append(_dialect.QuoteQualified(referencedSide, fk.ReferencedColumn));
        }
        sb.AppendLine();
    }

    // BuildWhere + filter rewrites + OR grouping + text-search + placeholder/temporal helpers moved to SqlCompiler.Where.cs.


    // GROUP BY / HAVING / ORDER BY clause builders moved to SqlCompiler.Aggregation.cs.
    // BuildOrderBy stays here (still in this file below) until a deeper extraction; this is the
    // first hop of the partial split.

    private void BuildOrderBy(QuerySpec spec, StringBuilder sb)
    {
        if (spec.OrderBy is null || spec.OrderBy.Count == 0) return;

        // SQL Server's ORDER BY rules:
        //   - Aggregate-only query (aggregations present, NO GROUP BY): ORDER BY can ONLY
        //     reference an aggregate alias. Real columns are illegal.
        //   - Otherwise: ORDER BY can reference real catalog columns OR any projected alias.
        // The previous version was too permissive: it added every spec.Select bare name to
        // validAliases even when the SELECT builder had rejected it as a phantom — that's
        // how `ORDER BY [Id]` and `ORDER BY [AvgStatusId]` reached SQL Server in the
        // 2026-05-09 assessment dump and crashed at execution.
        // Metric expansion may have moved aggregate expressions into Computed (alias = metric
        // name, expression = "COUNT(*)" / "AVG(...)"). Treat those as aggregates for
        // ORDER-BY-safety purposes too.
        var hasComputedAggregate = spec.Computed.Any(c => LooksLikeAggregateExpression(c.Expression));
        var hasAggregations = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null) || hasComputedAggregate;
        var hasGroupBy = spec.GroupBy.Count > 0;
        var aggregateOnly = hasAggregations && !hasGroupBy;

        var validAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in spec.Aggregations)
        {
            if (NormalizeAggFn(a.Function) is null) continue;
            if (!string.IsNullOrWhiteSpace(a.Alias)) validAliases.Add(a.Alias);
            else if (!string.IsNullOrWhiteSpace(a.Function)) validAliases.Add(a.Function);
        }
        // Computed-column aliases are also sortable. Treat them like aggregate aliases.
        foreach (var c in spec.Computed)
        {
            if (string.IsNullOrWhiteSpace(c.Alias) || string.IsNullOrWhiteSpace(c.Expression)) continue;
            validAliases.Add(c.Alias);
        }

        // Build the set of columns that the GROUP BY actually emits — used both as a SELECT
        // alias source and as the allowlist for real columns in ORDER BY when aggregations exist.
        var groupBySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasGroupBy)
        {
            foreach (var g in spec.GroupBy)
                if (TryFormatColumn(g, out var gf)) groupBySet.Add(gf);
        }

        // Only count select items that ACTUALLY land in the SELECT clause as valid aliases —
        // never the phantom ones that TryFormatColumn rejected (and that GROUP BY safety
        // would also drop in the aggregate case).
        if (!aggregateOnly)
        {
            foreach (var s in spec.Select)
            {
                if (!TryFormatColumn(s, out var f)) continue;
                if (hasAggregations && hasGroupBy && !groupBySet.Contains(f)) continue;
                var (_, c) = SplitQualified(s);
                if (!string.IsNullOrEmpty(c)) validAliases.Add(c);
            }
        }

        var items = new List<string>();
        foreach (var o in spec.OrderBy)
        {
            // PII guard — refuse to sort by a sensitive column, even indirectly via expression.
            // Otherwise an LLM could emit "ORDER BY Users.PasswordHash" and it would execute.
            if (ExpressionReferencesSensitiveColumn(o.Column)) continue;

            string colExpr;

            if (aggregateOnly)
            {
                // Aggregate-only — alias-only path. Real columns are skipped.
                var bare = o.Column.Replace("[", "").Replace("]", "").Trim();
                var aliasCandidate = bare.Contains('.') ? bare[(bare.LastIndexOf('.') + 1)..] : bare;
                if (validAliases.Contains(aliasCandidate))
                    colExpr = _dialect.QuoteIdentifier(aliasCandidate);
                else
                    continue;
            }
            else if (TryFormatColumn(o.Column, out var formatted))
            {
                // GROUP BY safety: when the query has both aggregations AND GROUP BY, real
                // columns in ORDER BY are only legal if they appear in GROUP BY. SQL Server
                // otherwise rejects with "Column ... is invalid in the ORDER BY clause".
                // Drop offenders rather than emit invalid SQL — same pattern as BuildSelect.
                if (hasAggregations && hasGroupBy && !groupBySet.Contains(formatted))
                {
                    // Allow if the bare column name is also an aggregate alias (rare but legal).
                    var (_, bareCol) = SplitQualified(o.Column);
                    if (string.IsNullOrEmpty(bareCol) || !validAliases.Contains(bareCol)) continue;
                }
                colExpr = formatted;
            }
            else
            {
                var bare = o.Column.Replace("[", "").Replace("]", "").Trim();
                var aliasCandidate = bare.Contains('.') ? bare[(bare.LastIndexOf('.') + 1)..] : bare;
                if (validAliases.Contains(aliasCandidate))
                    colExpr = _dialect.QuoteIdentifier(aliasCandidate);
                else
                    continue; // phantom — skip rather than emit invalid SQL
            }

            var dir = string.Equals(o.Direction, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            items.Add(colExpr + " " + dir);
        }

        if (items.Count == 0) return;
        sb.Append("ORDER BY ").AppendLine(string.Join(", ", items));
    }

    // Helpers (column/table-ref parsing, JsonElement → CLR coercion) live in
    // SqlCompiler.Helpers.cs. Same partial class — fields above are shared.
}
