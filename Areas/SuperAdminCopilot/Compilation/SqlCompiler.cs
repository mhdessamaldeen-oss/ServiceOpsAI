namespace SuperAdminCopilot.Compilation;

using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
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
    // F1 — populated by BuildSelect, consumed by BuildGroupBy. Keys are the plain `[Table].[Column]`
    // form of a GROUP BY column that allows NULL; values are the matching `ISNULL(..., '(Unassigned)')`
    // expression. Both SELECT-side and GROUP BY-side must use the wrapped form for SQL Server to
    // accept the query. Reset on every Compile() — never persisted across calls.
    private Dictionary<string, string> _groupByDisplayMap = new(StringComparer.OrdinalIgnoreCase);

    public SqlCompiler(
        IEntityCatalog catalog,
        JoinResolver joinResolver,
        ISemanticLayer semanticLayer,
        IOptions<CopilotOptions> options)
    {
        _catalog = catalog;
        _joinResolver = joinResolver;
        _semanticLayer = semanticLayer;
        _options = options.Value;
    }

    public CompiledSql Compile(QuerySpec spec)
    {
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

        // Expand semantic-layer references (metric:* in aggregations, dimension:* in
        // select/groupBy/computed) into concrete columns/expressions/extra filters BEFORE we
        // walk references. This lets metric-baked filters add their tables to the join graph.
        ExpandSemanticReferences(spec);

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

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { spec.Root };
        foreach (var c in spec.Select) AddTable(c, referenced);
        foreach (var c in spec.GroupBy) AddTable(c, referenced);
        foreach (var o in spec.OrderBy) AddTable(o.Column, referenced);
        foreach (var f in spec.Filters) AddTable(f.Column, referenced);
        foreach (var a in spec.Aggregations)
        {
            if (a.Column == "*") continue;
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
        ApplyAutoGroupByInference(spec);

        BuildSelect(spec, sb);
        BuildFrom(spec, joinEdges, joinKindByTable, sb);
        BuildWhere(spec, sb, parameters, ref paramIndex, joinEdges, joinKindByTable);
        BuildGroupBy(spec, sb);
        BuildHaving(spec, sb, parameters, ref paramIndex);
        BuildOrderBy(spec, sb);

        return new CompiledSql(sb.ToString().TrimEnd() + ";", parameters);
    }

    // DISTINCT and GROUP BY express overlapping intent — when an LLM emits both, the result
    // is malformed SQL (SELECT-list columns that aren't in GROUP BY trigger error 8120).
    // Universal normalization: when distinct:true is set with no real aggregations, drop
    // any GROUP BY — DISTINCT subsumes it. With aggregations the GROUP BY is load-bearing,
    // so we leave it alone and let the SELECT speak through GROUP BY semantics.
    private void ApplyDistinctGroupByNormalization(QuerySpec spec)
    {
        if (spec.Distinct && spec.GroupBy.Count > 0 &&
            !spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null))
        {
            spec.GroupBy.Clear();
        }
    }

    // Auto-GROUP-BY inference: when the spec has aggregations + non-aggregate SELECT
    // columns but the planner forgot to populate GroupBy, infer it from those columns.
    // Without this fix, BuildSelect silently drops the non-aggregate columns (because
    // they're not in GROUP BY) and the user gets a scalar count instead of the per-group
    // breakdown they asked for.
    private void ApplyAutoGroupByInference(QuerySpec spec)
    {
        var hasAggsForInference = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null);
        if (!hasAggsForInference || spec.GroupBy.Count != 0 || spec.Select.Count == 0) return;

        // C13 — skip raw datetime columns. Inferring GROUP BY off a millisecond-precision
        // datetime produces one bucket per row, which is the opposite of what the user wants.
        // If they want a per-period count they need a Computed bucket alias (FORMAT/CAST AS DATE);
        // the LLM's worked examples teach that pattern. When no bucket exists, drop the date
        // column from auto-GROUP-BY rather than detonate the result set.
        foreach (var col in spec.Select)
        {
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
            BuildFrom(legSpec, joinEdges, joinKindByTable, sb);
            BuildWhere(legSpec, sb, parameters, ref paramIndex, joinEdges, joinKindByTable);
            BuildGroupBy(legSpec, sb);
            BuildHaving(legSpec, sb, parameters, ref paramIndex);
        }

        return new CompiledSql(sb.ToString().TrimEnd() + ";", parameters);
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

    /// <summary>
    /// Resolves every "metric:&lt;name&gt;" / "dimension:&lt;name&gt;" reference in the spec into
    /// concrete SQL fragments. Metric references in aggregations become inline expressions on
    /// a synthesized <see cref="ComputedSpec"/>; dimension references in select/groupBy/computed
    /// become real column refs or computed expressions.
    /// </summary>
    private void ExpandSemanticReferences(QuerySpec spec)
    {
        // 1) Aggregations: function = "metric:<name>" → expand to inline computed expression
        //    and merge metric.Filters into spec.Filters (deduped by column+op).
        for (int i = spec.Aggregations.Count - 1; i >= 0; i--)
        {
            var a = spec.Aggregations[i];
            if (!a.Function.StartsWith("metric:", StringComparison.OrdinalIgnoreCase)) continue;
            var metric = _semanticLayer.GetMetric(a.Function);
            if (metric is null) { spec.Aggregations.RemoveAt(i); continue; }

            var alias = string.IsNullOrWhiteSpace(a.Alias) ? metric.Name : a.Alias;
            // C17 — when the metric's canonical name carries a unit suffix (_mb / _kb / _hours
            // / _days / _percent / _pct), append the unit to the alias unless the alias already
            // mentions it. Stops users seeing a bare "Total" column when the value is actually
            // megabytes — they couldn't tell the bytes->MB conversion happened.
            alias = AppendUnitSuffixIfMissing(alias, metric.Name);
            spec.Computed.Add(new ComputedSpec { Alias = alias, Expression = metric.Expression });
            spec.Aggregations.RemoveAt(i);

            foreach (var mf in metric.Filters)
            {
                var dup = spec.Filters.Any(ef =>
                    string.Equals(ef.Column, mf.Column, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ef.Op, mf.Op, StringComparison.OrdinalIgnoreCase));
                if (!dup)
                    spec.Filters.Add(new FilterSpec { Column = mf.Column, Op = mf.Op, Value = mf.Value });
            }
        }

        // 2) Select entries: "dimension:<name>" → either a column ref (rewrite in place) or
        //    a computed expression (move to spec.Computed).
        for (int i = spec.Select.Count - 1; i >= 0; i--)
        {
            var s = spec.Select[i];
            if (!s.StartsWith("dimension:", StringComparison.OrdinalIgnoreCase)) continue;
            var dim = _semanticLayer.GetDimension(s);
            if (dim is null) { spec.Select.RemoveAt(i); continue; }
            if (!string.IsNullOrEmpty(dim.Column)) { spec.Select[i] = dim.Column; continue; }
            if (!string.IsNullOrEmpty(dim.Expression))
            {
                spec.Computed.Add(new ComputedSpec { Alias = dim.Name, Expression = dim.Expression });
                spec.Select.RemoveAt(i);
            }
        }

        // 3) GroupBy entries: same shape as Select.
        for (int i = spec.GroupBy.Count - 1; i >= 0; i--)
        {
            var g = spec.GroupBy[i];
            if (!g.StartsWith("dimension:", StringComparison.OrdinalIgnoreCase)) continue;
            var dim = _semanticLayer.GetDimension(g);
            if (dim is null) { spec.GroupBy.RemoveAt(i); continue; }
            if (!string.IsNullOrEmpty(dim.Column)) { spec.GroupBy[i] = dim.Column; continue; }
            // GROUP BY of a derived expression: leave the raw expression in place; BuildGroupBy
            // calls TryFormatColumn which will fail on an expression — so emit it verbatim.
            spec.GroupBy[i] = dim.Expression ?? "";
        }

        // 4) Computed entries: expression may itself reference a dimension token.
        for (int i = 0; i < spec.Computed.Count; i++)
        {
            var c = spec.Computed[i];
            if (!c.Expression.StartsWith("dimension:", StringComparison.OrdinalIgnoreCase)) continue;
            var dim = _semanticLayer.GetDimension(c.Expression);
            if (dim is null) continue;
            spec.Computed[i] = new ComputedSpec
            {
                Alias = string.IsNullOrEmpty(c.Alias) ? dim.Name : c.Alias,
                Expression = dim.Expression ?? dim.Column ?? "",
            };
        }
    }

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
    private static void RewriteInvalidStarAggregates(QuerySpec spec)
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
    /// Final-pass alias-dedup helper for BuildSelect. Walks the list of "expr AS [alias]"
    /// strings and renames duplicates to "[alias_2]", "[alias_3]", etc. Items without an explicit
    /// alias (a bare column ref like `[Tickets].[Title]`) are left alone — collision-handling for
    /// those happens in the bare-name pre-pass at the top of BuildSelect.
    /// </summary>
    private static void DedupeAliasesInPlace(List<string> items)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            // Match "... AS [<alias>]" at the very end. Anchor on " AS [" + closing "]".
            var asIdx = item.LastIndexOf(" AS [", StringComparison.OrdinalIgnoreCase);
            if (asIdx < 0 || !item.EndsWith("]")) continue;
            var aliasStart = asIdx + " AS [".Length;
            var alias = item.Substring(aliasStart, item.Length - aliasStart - 1);   // trim the "]"
            if (string.IsNullOrEmpty(alias)) continue;

            if (!seen.TryAdd(alias, 1)) // already-used alias
            {
                seen[alias]++;
                var newAlias = $"{alias}_{seen[alias]}";
                // Rebuild item: keep everything before " AS [", append new alias.
                items[i] = item.Substring(0, asIdx) + " AS [" + newAlias + "]";
                seen[newAlias] = 1;     // reserve the new alias too
            }
        }
    }

    /// <summary>Strip aggregate-prefix from an alias so "AvgAgeDays" matches Computed alias "AgeDays".</summary>
    private static string StripAggregatePrefix(string s)
    {
        foreach (var p in new[] { "Avg", "Sum", "Min", "Max", "Total", "Average" })
        {
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

    private void BuildSelect(QuerySpec spec, StringBuilder sb)
    {
        sb.Append("SELECT ");
        if (spec.Distinct) sb.Append("DISTINCT ");
        if (spec.Limit.HasValue && spec.Limit.Value > 0)
            sb.Append("TOP (").Append(spec.Limit.Value).Append(") ");

        // GROUP BY safety: when aggregations are present, every non-aggregate column in the
        // SELECT list must also appear in GROUP BY (SQL Server otherwise rejects the query
        // with "Column ... is invalid in the select list because it is not contained in
        // either an aggregate function or the GROUP BY clause"). Drop offenders rather than
        // emit invalid SQL.
        var hasAggregations = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null);
        var groupBySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // F1 — nullable GROUP BY columns get wrapped with ISNULL so the NULL bucket has a
        // human-readable label ("(Unassigned)") instead of a blank row. The map is shared
        // between BuildSelect (this method) and BuildGroupBy via _groupByDisplayMap so both
        // emit the same lexical expression — required by SQL Server's GROUP BY identity rule.
        _groupByDisplayMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasAggregations)
        {
            foreach (var g in spec.GroupBy)
            {
                if (!TryFormatColumn(g, out var f)) continue;
                groupBySet.Add(f);
                if (TryWrapNullableGroupKey(g, out var wrapped, out var wasWrapped) && wasWrapped)
                    _groupByDisplayMap[f] = wrapped;
            }
        }

        // Pre-pass: detect bare-column-name collisions across selected columns coming from
        // different tables (e.g. TicketStatuses.Name + TicketPriorities.Name). When two
        // selected columns share the bare name, the result-row Dictionary collapses them
        // under one key — second value wins, first column's data is silently lost.
        // Fix: alias colliding columns with their table prefix ("TicketStatuses_Name") so
        // the reader sees distinct keys and the LLM explainer sees unambiguous labels.
        var bareCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in spec.Select)
        {
            var (_, bare) = SplitQualified(col);
            if (string.IsNullOrEmpty(bare)) continue;
            bareCounts[bare] = bareCounts.GetValueOrDefault(bare) + 1;
        }

        var items = new List<string>();
        foreach (var col in spec.Select)
        {
            if (!TryFormatColumn(col, out var f)) continue;
            if (hasAggregations && !groupBySet.Contains(f))
                continue; // would violate aggregate/GROUP BY rule
            var (table, bare) = SplitQualified(col);
            // PII denylist (#55) — refuse to emit any column declared sensitive in the semantic
            // layer. Defence in depth: the executor also masks these post-fetch in case a query
            // bypasses the compiler (e.g. * expansion or future raw-SQL paths).
            if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(bare)
                && _semanticLayer.IsSensitiveColumn(table, bare))
            {
                continue;
            }
            // F1 — if this column is a nullable GROUP BY target, swap in the ISNULL-wrapped
            // expression with an explicit AS [bare] so the column reads naturally in the reply
            // table AND the GROUP BY emitter (BuildGroupBy) uses the lexically-identical form.
            if (_groupByDisplayMap.TryGetValue(f, out var wrappedExpr))
            {
                items.Add($"{wrappedExpr} AS [{bare}]");
            }
            else if (!string.IsNullOrEmpty(bare)
                && bareCounts.TryGetValue(bare, out var cnt) && cnt > 1
                && !string.IsNullOrEmpty(table))
            {
                // J2 — alias colliding columns. For the common case "Table.Name" + "Table2.Name"
                // (two lookup labels in one query), use the singularised table name as the alias
                // so the user sees "Status" + "Priority", not "TicketStatuses_Name" + "TicketPriorities_Name".
                // Falls back to "Table_Column" for any other column-name collision.
                var friendly = string.Equals(bare, "Name", StringComparison.OrdinalIgnoreCase)
                    ? SingulariseTableForAlias(table)
                    : $"{table}_{bare}";
                items.Add($"{f} AS [{friendly}]");
            }
            else
            {
                items.Add(f);
            }
        }

        foreach (var a in spec.Aggregations)
        {
            var fn = NormalizeAggFn(a.Function);
            if (fn is null) continue;

            // Reject AVG/SUM/MIN/MAX(*) — invalid T-SQL. RewriteInvalidStarAggregates folds these
            // into "expr:<expression>" when a Computed expression matches; if it didn't fold, we
            // skip rather than emit broken SQL. (COUNT(*) is the one legit star-aggregate.)
            if (a.Column == SpecConst.Aggregates.Star && fn != SpecConst.Aggregates.Count) continue;

            string colExpr;
            if (a.Column == "*")
            {
                colExpr = "*";
            }
            else if (a.Column.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
            {
                // RewriteInvalidStarAggregates folded a per-row expression into this aggregate's
                // column slot. Render it inline (with column qualification) instead of as a
                // Table.Column reference, since it's not a real catalog column.
                var raw = a.Column.Substring("expr:".Length);
                colExpr = QualifyColumnsInExpression(StripTrailingAlias(raw), spec.Root);
            }
            else if (!TryFormatColumn(a.Column, out var f)) continue;
            else colExpr = f;

            // Distinct aggregates (COUNT(DISTINCT col), SUM(DISTINCT col), AVG(DISTINCT col)) —
            // applies only when a real column is named, not "*". COUNT(DISTINCT *) isn't valid SQL.
            var inner = a.Distinct && colExpr != "*" ? $"DISTINCT {colExpr}" : colExpr;
            var alias = string.IsNullOrWhiteSpace(a.Alias) ? fn : a.Alias;
            items.Add($"{fn}({inner}) AS [{alias}]");
        }

        // Computed columns: rendered verbatim with an alias. The validator's AST pass catches
        // anything that names a non-existent table/column; we don't try to be the validator here.
        foreach (var comp in spec.Computed)
        {
            if (string.IsNullOrWhiteSpace(comp.Expression)) continue;
            // Strip a trailing " AS xxx" the planner sometimes emits inside the expression —
            // we always append our own "AS [<alias>]" below, and double-AS parses as a syntax
            // error in SQL Server ("Incorrect syntax near 'AS'").
            var rawExpr = StripTrailingAlias(comp.Expression);
            var expr = QualifyColumnsInExpression(rawExpr, spec.Root);
            var alias = string.IsNullOrWhiteSpace(comp.Alias) ? "Computed" : comp.Alias;
            items.Add($"{expr} AS [{alias}]");
        }

        // Default to "*" ONLY when the query has no aggregations and no GROUP BY. With either
        // present, SELECT * expands to non-aggregate columns that violate SQL Server's
        // aggregate/GROUP BY rule ("Column ... is invalid in the select list because it is not
        // contained in either an aggregate function or the GROUP BY clause"). In that case emit
        // the GROUP BY columns instead — they're guaranteed legal — or fall back to COUNT(*) if
        // there are aggregations but everything else got filtered out.
        if (items.Count == 0)
        {
            if (hasAggregations || spec.GroupBy.Count > 0)
            {
                foreach (var g in spec.GroupBy)
                    if (TryFormatColumn(g, out var f)) items.Add(f);
                if (items.Count == 0 && hasAggregations) items.Add("COUNT(*) AS [Count]");
            }
            if (items.Count == 0)
            {
                // Build a meaningful default SELECT rather than star-expand. Order matters:
                //   1. Root entity's DisplayColumns from the semantic layer (id + title + …).
                //   2. Every FILTER column that resolves to a joined table.
                //      ── This addresses the recurring complaint that questions like
                //         "Show open critical tickets" filter on TicketStatuses.Name='Open'
                //         but never project that column — the user can't see WHY rows matched.
                //   3. Fall back to "*" only when neither source yielded anything (no semantic
                //      layer entry AND no filters).
                var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in DefaultDisplayColumns(spec.Root))
                {
                    if (TryFormatColumn(d, out var df) && added.Add(df)) items.Add(df);
                }
                foreach (var fil in spec.Filters)
                {
                    if (string.IsNullOrWhiteSpace(fil.Column)) continue;
                    var (tbl, _) = SplitQualified(fil.Column);
                    if (string.IsNullOrEmpty(tbl)) continue;
                    if (string.Equals(tbl, spec.Root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryFormatColumn(fil.Column, out var ff) && added.Add(ff)) items.Add(ff);
                }
                if (items.Count == 0) items.Add("*");
            }
        }

        // Final-pass alias dedup (#41) — the three SELECT sources (real columns / aggregates /
        // computed) can independently produce the same alias (e.g., aggregate alias "Count" AND
        // a Computed alias "Count"). When that happens the result Dictionary collapses the
        // second under the first key, silently losing data. Walk the items list once, track
        // assigned aliases, and rename collisions to "<alias>_2", "<alias>_3", etc.
        DedupeAliasesInPlace(items);
        sb.AppendLine(string.Join(", ", items));
    }

    /// <summary>
    /// Pick a sensible default SELECT list for the root entity when the planner left
    /// <see cref="QuerySpec.Select"/> empty. Preference order:
    ///   1. Configured <c>EntityDefinition.DisplayColumns</c> (catalog-verified).
    ///   2. PK column "Id" + natural-key column + label column ("Name", "Title", "Code", etc.)
    ///   3. Empty list → caller falls back to "*" elsewhere.
    /// Mirrors <c>ShapeBase.PickDisplayColumns</c> but lives in the compiler so it runs even
    /// for planner-emitted specs that the shape engine didn't produce.
    /// </summary>
    private IEnumerable<string> DefaultDisplayColumns(string table)
    {
        if (string.IsNullOrEmpty(table) || !_catalog.TableExists(table)) yield break;
        var e = _semanticLayer.GetEntityForTable(table);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (e is not null && e.DisplayColumns is { Count: > 0 })
        {
            foreach (var c in e.DisplayColumns)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (!_catalog.ColumnExists(table, c)) continue;
                var q = $"{table}.{c}";
                if (emitted.Add(q)) yield return q;
            }
            if (emitted.Count > 0) yield break;
        }
        // Fallback chain — only fires when DisplayColumns is missing or every entry was stale.
        if (_catalog.ColumnExists(table, "Id"))
        {
            var q = $"{table}.Id";
            if (emitted.Add(q)) yield return q;
        }
        if (e is not null && !string.IsNullOrEmpty(e.NaturalKeyColumn)
            && _catalog.ColumnExists(table, e.NaturalKeyColumn!))
        {
            var q = $"{table}.{e.NaturalKeyColumn}";
            if (emitted.Add(q)) yield return q;
        }
        foreach (var name in new[] { "Name", "Title", "Code", "DisplayName", "Label", "UserName" })
        {
            if (!_catalog.ColumnExists(table, name)) continue;
            var q = $"{table}.{name}";
            if (emitted.Add(q)) { yield return q; break; }
        }
    }

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
                        && !DatePartKeywords.Contains(innerName)
                        && _catalog.ColumnExists(rootTable, innerName))
                    {
                        sb.Append('[').Append(rootTable).Append("].[").Append(innerName).Append(']');
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
                    && !DatePartKeywords.Contains(word)
                    && _catalog.ColumnExists(rootTable, word))
                {
                    sb.Append('[').Append(rootTable).Append("].[").Append(word).Append(']');
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
            sb.Append('[').Append(tableName).Append("].[").Append(columnName).Append(']');
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

    /// <summary>
    /// SQL Server date-part keywords used INSIDE function calls like <c>DATEDIFF(day, ...)</c>.
    /// These look like bare identifiers but must not be qualified as columns even when a table
    /// happens to have a column of the same name.
    /// </summary>
    private static readonly HashSet<string> DatePartKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "year", "yyyy", "yy", "quarter", "qq", "q",
        "month", "mm", "m", "dayofyear", "dy", "y",
        "day", "dd", "d", "week", "wk", "ww", "weekday", "dw",
        "hour", "hh", "minute", "mi", "n", "second", "ss", "s",
        "millisecond", "ms", "microsecond", "mcs", "nanosecond", "ns",
        "tzoffset", "tz", "iso_week", "isowk", "isoww",
    };

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
        // Strip a leading "Ticket" prefix when present so "TicketStatuses" reads as "Status".
        // Domain-shaped (any "<Department>Statuses" gets stripped to "Statuses"); kept conservative
        // because the alternative — TicketStatuses + TicketPriorities — would otherwise produce
        // "TicketStatuse" and "TicketPrioritie" via pure suffix stripping, which is worse.
        var bare = table;
        if (bare.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^3] + "y";              // Priorities → Priority, Categories → Category
        else if (bare.EndsWith("ses", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^2];                    // Statuses → Status, Classes → Class
        else if (bare.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                 && !bare.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^1];                    // Users → User, Sources → Source
        // Drop a "Ticket" / "User" entity prefix when its presence makes the alias verbose.
        foreach (var pre in new[] { "Ticket", "User", "Application" })
            if (bare.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && bare.Length > pre.Length)
            {
                bare = bare.Substring(pre.Length);
                break;
            }
        return string.IsNullOrEmpty(bare) ? table : bare;
    }

    // C17 — detect unit suffix on a metric name (_mb, _kb, _hours, _days, _pct, _percent)
    // and append it to the alias when the alias doesn't already mention the unit. Used by
    // metric expansion so a user-supplied alias like "Total" becomes "Total (MB)" when the
    // underlying metric is total_attachment_size_mb. Schema-driven via the metric's name
    // convention, not a column-name match.
    private static readonly string[] UnitSuffixes = new[] {
        "_mb", "_kb", "_gb", "_bytes",
        "_hours", "_minutes", "_seconds", "_days", "_weeks", "_months", "_years",
        "_pct", "_percent", "_rate", "_ratio",
    };
    private static string AppendUnitSuffixIfMissing(string alias, string metricName)
    {
        if (string.IsNullOrEmpty(metricName) || string.IsNullOrEmpty(alias)) return alias;
        var lowerName = metricName.ToLowerInvariant();
        var unit = UnitSuffixes.FirstOrDefault(u => lowerName.EndsWith(u, StringComparison.Ordinal));
        if (unit is null) return alias;
        var unitLabel = unit.TrimStart('_').ToUpperInvariant();
        if (alias.Contains(unitLabel, StringComparison.OrdinalIgnoreCase)) return alias;
        return $"{alias} ({unitLabel})";
    }

    private static void BuildFrom(
        QuerySpec spec,
        IReadOnlyList<FkEdge> joinEdges,
        IReadOnlyDictionary<string, string> joinKindByTable,
        StringBuilder sb)
    {
        sb.Append("FROM [").Append(spec.Root).Append("] AS [").Append(spec.Root).Append(']');

        foreach (var edge in joinEdges)
        {
            var fk = edge.Fk;
            // Default INNER JOIN; LEFT JOIN when the spec declares "left" or "anti" for this
            // target table (anti-joins also need an IS NULL filter, added in BuildWhere).
            var kind = joinKindByTable.TryGetValue(edge.TargetTable, out var k) ? k : "inner";
            var joinKeyword = kind is SpecConst.JoinKinds.Left or SpecConst.JoinKinds.Anti ? "LEFT JOIN" : "INNER JOIN";
            sb.AppendLine();
            sb.Append(joinKeyword).Append(" [").Append(edge.TargetTable).Append("] AS [").Append(edge.TargetTable).Append(']');
            sb.Append(" ON [").Append(fk.ParentTable).Append("].[").Append(fk.ParentColumn).Append(']');
            sb.Append(" = [").Append(fk.ReferencedTable).Append("].[").Append(fk.ReferencedColumn).Append(']');
        }
        sb.AppendLine();
    }

    private void BuildWhere(
        QuerySpec spec,
        StringBuilder sb,
        Dictionary<string, object?> parameters,
        ref int paramIndex,
        IReadOnlyList<FkEdge>? joinEdges = null,
        IReadOnlyDictionary<string, string>? joinKindByTable = null)
    {
        if ((spec.Filters is null || spec.Filters.Count == 0) &&
            (joinEdges is null || joinEdges.Count == 0)) return;

        // Collapse "same column, same op, multiple values" into a single OR group. Common case:
        // user says "title contains 'error' OR 'fail'" but the planner emits two LIKE filters.
        // Spec grammar has no OR — so without this, the compiler ANDs them and the query
        // returns 0 rows (no single title contains both).
        var groupedFilters = OrGroupFilters(spec.Filters ?? new List<FilterSpec>());

        var pieces = new List<string>();
        foreach (var group in groupedFilters)
        {
            // Single-filter group: original path.
            if (group.Count == 1)
            {
                var filter = group[0];

                // text_search — cross-column OR over the entity's SearchableColumns. The user
                // typed "tickets about login" and the planner emitted op:"text_search". We expand
                // it here into "(Title LIKE '%X%' OR Description LIKE '%X%' OR ...)" — which the
                // existing OrGroupFilters logic can't produce because it only groups same-column
                // same-op filters.
                if (string.Equals(filter.Op, SpecConst.FilterOps.TextSearch, StringComparison.OrdinalIgnoreCase))
                {
                    var clauseTs = BuildTextSearchClause(spec.Root, filter, parameters, ref paramIndex);
                    if (!string.IsNullOrEmpty(clauseTs)) pieces.Add(clauseTs);
                    continue;
                }

                // Rewrite BEFORE formatting so a lookup-name filter (StatusId='Closed' →
                // TicketStatuses.Name='Closed') gets formatted with the new column path.
                var rewritten = ApplyFilterRewrite(filter);
                if (!TryFormatColumn(rewritten.Column, out var col)) continue;
                var clause = BuildFilterClause(col, rewritten, parameters, ref paramIndex);
                if (!string.IsNullOrEmpty(clause)) pieces.Add(clause);
                continue;
            }

            // Multi-filter group on same column+op: emit "(c1 OR c2 OR c3)".
            // Apply the rewrite to the FIRST filter to derive the shared column path.
            var firstRewritten = ApplyFilterRewrite(group[0]);
            if (!TryFormatColumn(firstRewritten.Column, out var sharedCol)) continue;
            var sub = new List<string>();
            foreach (var filter in group)
            {
                var rewritten = ApplyFilterRewrite(filter);
                var clause = BuildFilterClause(sharedCol, rewritten, parameters, ref paramIndex);
                if (!string.IsNullOrEmpty(clause)) sub.Add(clause);
            }
            if (sub.Count == 1) pieces.Add(sub[0]);
            else if (sub.Count > 1) pieces.Add("(" + string.Join(" OR ", sub) + ")");
        }

        // Anti-join IS NULL clauses: for each LEFT JOIN target declared as "anti", add
        // "<target>.<referenced PK column> IS NULL" so the query returns root rows that have NO
        // matching related row.
        if (joinEdges is not null && joinKindByTable is not null)
        {
            foreach (var edge in joinEdges)
            {
                if (!joinKindByTable.TryGetValue(edge.TargetTable, out var k) || k != SpecConst.JoinKinds.Anti) continue;
                var fk = edge.Fk;
                // The "PK" side of the FK (the side that lives in the related table). For an
                // anti-join we test that side for NULL — when LEFT JOIN found no row, every
                // related-table column is NULL.
                var antiCol = string.Equals(fk.ParentTable, edge.TargetTable, StringComparison.OrdinalIgnoreCase)
                    ? $"[{fk.ParentTable}].[{fk.ParentColumn}]"
                    : $"[{fk.ReferencedTable}].[{fk.ReferencedColumn}]";
                pieces.Add($"{antiCol} IS NULL");
            }
        }

        if (pieces.Count == 0) return;
        sb.Append("WHERE ").AppendLine(string.Join(" AND ", pieces));
    }

    /// <summary>
    /// Apply value-synonym rewrite ("urgent" → "Critical") for column-context synonyms declared
    /// in the semantic layer. Returns either the original filter or a new one with the canonical
    /// value. No-op for null values, @-tokens, and arrays.
    /// </summary>
    private FilterSpec ApplySynonymRewrite(FilterSpec filter)
    {
        if (filter.Value is not string sv || sv.Length == 0 || sv[0] == '@') return filter;
        var canonical = _semanticLayer.ResolveSynonymValue(filter.Column, sv);
        if (ReferenceEquals(canonical, sv) || string.Equals(canonical, sv, StringComparison.Ordinal))
            return filter;
        return new FilterSpec { Column = filter.Column, Op = filter.Op, Value = canonical };
    }

    /// <summary>
    /// Combined filter rewrite passing through the value-synonym rewrite (above) AND two new
    /// fixes from the assessment:
    /// <list type="bullet">
    ///   <item>#5 — <b>Lookup-name filter</b>: when filter is <c>&lt;T&gt;.&lt;XId&gt; eq '&lt;string&gt;'</c>
    ///   and X is an FK to a table with a label column (Name / Title / Code / UserName), rewrite to
    ///   <c>&lt;RefTable&gt;.&lt;LabelCol&gt; eq '&lt;string&gt;'</c>. Fixes "Tickets without a closed
    ///   date" planner emitting <c>StatusId = 'Closed'</c> (int FK vs string).</item>
    ///   <item>#6 — <b>String-literal column reference</b>: filter value looks like
    ///   <c>Table.Column</c> AND that column exists — drop the filter (it's a join condition the
    ///   planner mistakenly stringified). Fixes "How many tickets have at least one attachment?"
    ///   planner emitting <c>WHERE TicketAttachments.TicketId = 'Tickets.Id'</c>.</item>
    /// </list>
    /// All callers of <see cref="ApplySynonymRewrite"/> should now use this instead — the
    /// value-synonym pass is preserved as the last step.
    /// </summary>
    private FilterSpec ApplyFilterRewrite(FilterSpec filter)
    {
        if (filter is null) return filter!;

        // #6 — string-literal column reference: drop the filter when value is "Table.Column" and
        // both halves resolve in the catalog. The compiler's join graph will already wire the
        // referenced relationship via FK; a string-equals comparison would always be false.
        if (filter.Value is string svRef)
        {
            if (Regex.IsMatch(svRef, @"^[A-Za-z_]\w*\.[A-Za-z_]\w*$"))
            {
                var (refTable, refCol) = SplitQualified(svRef);
                if (!string.IsNullOrEmpty(refTable) && !string.IsNullOrEmpty(refCol)
                    && _catalog.ColumnExists(refTable, refCol))
                {
                    // Drop the filter cleanly: the @-prefixed placeholder triggers
                    // <see cref="IsPlaceholderToken"/> in BuildFilterClause, which returns null
                    // (i.e. emits no predicate). The relationship is already wired by the join graph.
                    return new FilterSpec { Column = filter.Column, Op = SpecConst.FilterOps.Eq, Value = "@drop_columnref" };
                }
            }
        }

        // #5 — lookup-name filter rewrite. Match shape "<T>.<X>Id" where X is alphabetic.
        if (filter.Value is string svLookup && svLookup.Length > 0 && svLookup[0] != '@')
        {
            var rewritten = TryRewriteLookupNameFilter(filter, svLookup);
            if (rewritten is not null) return ApplySynonymRewrite(rewritten);
        }

        return ApplySynonymRewrite(filter);
    }

    /// <summary>
    /// Try to rewrite "<T>.<X>Id eq '<name>'" to "<RefTable>.<LabelCol> eq '<name>'" when the
    /// FK to <RefTable> exists and <RefTable> has a recognisable label column. Returns null
    /// when no rewrite applies (caller falls through).
    /// </summary>
    private FilterSpec? TryRewriteLookupNameFilter(FilterSpec filter, string stringValue)
    {
        var (table, col) = SplitQualified(filter.Column);
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(col)) return null;
        if (!col.EndsWith("Id", StringComparison.OrdinalIgnoreCase)) return null;
        if (!_catalog.TableExists(table)) return null;
        // Numeric value? Don't rewrite — user filtered by ID directly.
        if (long.TryParse(stringValue, out _)) return null;

        // Locate the FK with this column on the parent side.
        var fk = _catalog.Snapshot.ForeignKeys.FirstOrDefault(f =>
            string.Equals(f.ParentTable, table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.ParentColumn, col, StringComparison.OrdinalIgnoreCase));
        if (fk is null) return null;
        if (!_catalog.TableExists(fk.ReferencedTable)) return null;

        // Find a label column on the referenced table. Priority order matches the catalog's own.
        string? labelCol = null;
        foreach (var candidate in new[] { "Name", "UserName", "Title", "Code", "Label" })
        {
            if (_catalog.ColumnExists(fk.ReferencedTable, candidate)) { labelCol = candidate; break; }
        }
        if (labelCol is null) return null;

        return new FilterSpec
        {
            Column = $"{fk.ReferencedTable}.{labelCol}",
            Op = filter.Op,
            Value = stringValue,
        };
    }

    /// <summary>
    /// Group adjacent filters that share (Column, Op) so they can render as a single OR group.
    /// Only LIKE / eq / neq filters are grouped — operators where multiple values on the same
    /// column logically mean "any of these". Filters on different columns or different ops stay
    /// in their own single-element group (preserves AND semantics across groups).
    /// </summary>
    private static List<List<FilterSpec>> OrGroupFilters(List<FilterSpec> filters)
    {
        var groups = new List<List<FilterSpec>>();
        foreach (var f in filters)
        {
            var op = (f.Op ?? SpecConst.FilterOps.Eq).ToLowerInvariant();
            var groupable = op is SpecConst.FilterOps.Like or SpecConst.FilterOps.Eq or SpecConst.FilterOps.Neq or "ne";
            if (groupable && groups.Count > 0)
            {
                var last = groups[^1];
                var lastF = last[0];
                var lastOp = (lastF.Op ?? SpecConst.FilterOps.Eq).ToLowerInvariant();
                if (string.Equals(lastF.Column, f.Column, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(lastOp, op, StringComparison.OrdinalIgnoreCase))
                {
                    last.Add(f);
                    continue;
                }
            }
            groups.Add(new List<FilterSpec> { f });
        }
        return groups;
    }

    /// <summary>
    /// Expands a text_search filter into a cross-column OR group:
    /// <c>([Root].[Col1] LIKE @p AND ([Root].[Col2] LIKE @p OR ...))</c> across every column
    /// listed in the entity's <see cref="EntityDefinition.SearchableColumns"/>. Returns null
    /// when the entity has no searchable columns configured (skip the filter — let the planner
    /// fall back to a column-specific LIKE if it can name one).
    /// </summary>
    private string? BuildTextSearchClause(
        string rootTable, FilterSpec f, Dictionary<string, object?> parameters, ref int paramIndex)
    {
        if (string.IsNullOrEmpty(rootTable)) return null;
        var entity = _semanticLayer.GetEntityForTable(rootTable);
        if (entity is null || entity.SearchableColumns.Count == 0) return null;

        var raw = ExtractValues(f.Value).FirstOrDefault();
        if (raw is null) return null;
        var phrase = raw.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        if (IsPlaceholderToken(phrase)) return null;

        // Wrap with % wildcards if the planner didn't already.
        if (!phrase.Contains('%')) phrase = "%" + phrase + "%";

        // Single shared parameter — same value, multiple columns. Avoids parameter-name churn.
        var p = "@p" + paramIndex++;
        parameters[p] = phrase;

        var clauses = new List<string>();
        foreach (var colName in entity.SearchableColumns)
        {
            if (!_catalog.ColumnExists(rootTable, colName)) continue;
            clauses.Add($"[{rootTable}].[{colName}] LIKE {p}");
        }
        if (clauses.Count == 0) return null;
        return clauses.Count == 1 ? clauses[0] : "(" + string.Join(" OR ", clauses) + ")";
    }

    private static string? BuildFilterClause(
        string col, FilterSpec f, Dictionary<string, object?> parameters, ref int paramIndex)
    {
        var op = (f.Op ?? "eq").ToLowerInvariant();
        switch (op)
        {
            case "isnull":  return $"{col} IS NULL";
            case "notnull": return $"{col} IS NOT NULL";
            case "in":
            case "notin":
            {
                var values = ExtractValues(f.Value);
                if (values.Length == 0) return null;
                var paramNames = new List<string>(values.Length);
                foreach (var v in values)
                {
                    if (IsPlaceholderToken(v)) return null; // see IsPlaceholderToken comment
                    var p = "@p" + paramIndex++;
                    parameters[p] = v ?? DBNull.Value;
                    paramNames.Add(p);
                }
                return $"{col} {(op == "notin" ? "NOT IN" : "IN")} ({string.Join(", ", paramNames)})";
            }
            case SpecConst.FilterOps.Like:
            {
                var v = ExtractValues(f.Value).FirstOrDefault();
                if (IsPlaceholderToken(v)) return null;
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} LIKE {p}";
            }
            case SpecConst.FilterOps.NotLike:
            {
                // J7 — without this branch the default arm fell through to `=`, silently
                // converting `not_like '%error%'` into `= '%error%'` (i.e. equality against
                // a literal that contains percent signs — almost always zero matches).
                var v = ExtractValues(f.Value).FirstOrDefault();
                if (IsPlaceholderToken(v)) return null;
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} NOT LIKE {p}";
            }
            default:
            {
                var sqlOp = op switch
                {
                    SpecConst.FilterOps.Eq  => "=",
                    SpecConst.FilterOps.Neq => "<>",
                    "ne"                    => "<>",
                    SpecConst.FilterOps.Gt  => ">",
                    SpecConst.FilterOps.Lt  => "<",
                    SpecConst.FilterOps.Gte => ">=",
                    SpecConst.FilterOps.Lte => "<=",
                    _ => "="
                };
                var v = ExtractValues(f.Value).FirstOrDefault();
                // Temporal token: "@today" / "@yesterday" / "@days:-7" / "@hours:-24" /
                // "@weeks:-2" / "@months:-3" / "@month_start" / "@year_start" / "@week_start".
                // These expand to inline SQL date math — NOT parameterized — so SQL Server
                // sees real expressions instead of trying to compare against the literal token.
                if (v is string s && TryExpandTemporalToken(s, out var sqlExpr))
                    return $"{col} {sqlOp} {sqlExpr}";
                if (IsPlaceholderToken(v)) return null;
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} {sqlOp} {p}";
            }
        }
    }

    /// <summary>
    /// Returns true if the value is a planner-emitted placeholder string that should NEVER be
    /// passed as a real filter value — e.g. "@p0", "@p1", "@param", "?". qwen2.5:3b sometimes
    /// echoes the SQL parameter syntax it sees in retry prompts back into the spec as a literal
    /// value, which then gets parameterized as the STRING "@p0" — breaking SQL Server with a
    /// confusing nvarchar→int conversion error. Drop the filter rather than emit broken SQL.
    /// Temporal tokens (@today, @days:-7, etc.) are handled separately above and never reach here.
    /// </summary>
    private static bool IsPlaceholderToken(object? value)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return false;
        var t = s.Trim();
        if (t == "?") return true;
        if (t.Length < 2 || t[0] != '@') return false;
        // "@p0" / "@p1" / "@param" / "@arg" / "@val" — anything that looks like a SQL placeholder name
        // and isn't a temporal token. Temporal tokens are caught earlier; assume any remaining @-string
        // here is a placeholder echo.
        return true;
    }

    /// <summary>
    /// Expands a temporal token (planner emits a string starting with '@') into an inline
    /// T-SQL expression. Returning false means the token wasn't recognized; the caller falls
    /// back to parameterizing the value as a normal string.
    /// </summary>
    private static bool TryExpandTemporalToken(string token, out string sqlExpr)
    {
        sqlExpr = "";
        if (string.IsNullOrEmpty(token) || token[0] != '@') return false;
        var t = token.AsSpan(1).Trim().ToString().ToLowerInvariant();

        switch (t)
        {
            case "now":           sqlExpr = "GETDATE()"; return true;
            case "today":         sqlExpr = "CAST(GETDATE() AS DATE)"; return true;
            case "yesterday":     sqlExpr = "CAST(DATEADD(day, -1, GETDATE()) AS DATE)"; return true;
            case "tomorrow":      sqlExpr = "CAST(DATEADD(day, 1, GETDATE()) AS DATE)"; return true;
            case "today_start":
            case "day_start":     sqlExpr = "CAST(GETDATE() AS DATE)"; return true;

            // Current calendar boundaries.
            case "week_start":    sqlExpr = "DATEADD(week, DATEDIFF(week, 0, GETDATE()), 0)"; return true;
            case "month_start":   sqlExpr = "DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)"; return true;
            case "year_start":    sqlExpr = "DATEFROMPARTS(YEAR(GETDATE()), 1, 1)"; return true;
            case "quarter_start": sqlExpr = "DATEADD(quarter, DATEDIFF(quarter, 0, GETDATE()), 0)"; return true;

            // Previous calendar boundaries — pair *_start with the next-period start as upper
            // bound. Example for "last month":
            //   filters: [
            //     { col: "Tickets.CreatedAt", op: "gte", value: "@last_month_start" },
            //     { col: "Tickets.CreatedAt", op: "lt",  value: "@month_start" }
            //   ]
            case "last_week_start":    sqlExpr = "DATEADD(week, DATEDIFF(week, 0, GETDATE()) - 1, 0)"; return true;
            case "last_month_start":   sqlExpr = "DATEFROMPARTS(YEAR(DATEADD(month, -1, GETDATE())), MONTH(DATEADD(month, -1, GETDATE())), 1)"; return true;
            case "last_year_start":    sqlExpr = "DATEFROMPARTS(YEAR(GETDATE()) - 1, 1, 1)"; return true;
            case "last_quarter_start": sqlExpr = "DATEADD(quarter, DATEDIFF(quarter, 0, GETDATE()) - 1, 0)"; return true;
        }

        // "days:-7" / "hours:-24" / "weeks:-2" / "months:-3" / "years:-1"
        var colonIdx = t.IndexOf(':');
        if (colonIdx > 0 && colonIdx < t.Length - 1)
        {
            var unit = t[..colonIdx];
            if (int.TryParse(t[(colonIdx + 1)..], out var offset))
            {
                var sqlUnit = unit switch
                {
                    "second" or "seconds" or "sec" => "second",
                    "minute" or "minutes" or "min" => "minute",
                    "hour" or "hours" or "hr"      => "hour",
                    "day" or "days"                => "day",
                    "week" or "weeks" or "wk"      => "week",
                    "month" or "months" or "mo"    => "month",
                    "year" or "years" or "yr"      => "year",
                    _ => null
                };
                if (sqlUnit is not null)
                {
                    // Consistency with @today / @yesterday: day-or-coarser offsets are anchored to
                    // the start of today (midnight) so range comparisons against datetime columns
                    // include the full day. Without this, "@days:-7 at 14:00" excludes data from
                    // 7 days ago between 00:00-14:00 — silently wrong. Sub-day units keep the time
                    // component because the user is asking about a rolling window.
                    var baseExpr = sqlUnit is "second" or "minute" or "hour"
                        ? "GETDATE()"
                        : "CAST(GETDATE() AS DATE)";
                    sqlExpr = $"DATEADD({sqlUnit}, {offset}, {baseExpr})";
                    return true;
                }
            }
        }

        return false;
    }

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
            string colExpr;

            if (aggregateOnly)
            {
                // Aggregate-only — alias-only path. Real columns are skipped.
                var bare = o.Column.Replace("[", "").Replace("]", "").Trim();
                var aliasCandidate = bare.Contains('.') ? bare[(bare.LastIndexOf('.') + 1)..] : bare;
                if (validAliases.Contains(aliasCandidate))
                    colExpr = "[" + aliasCandidate + "]";
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
                    colExpr = "[" + aliasCandidate + "]";
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
