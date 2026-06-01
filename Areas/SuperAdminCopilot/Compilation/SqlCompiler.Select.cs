namespace SuperAdminCopilot.Compilation;

using System.Text;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

// SELECT-clause emitter + default-display-column picker. Splits ~225 lines out of the main file.
internal sealed partial class SqlCompiler
{
    private void BuildSelect(QuerySpec spec, StringBuilder sb)
    {
        sb.Append("SELECT ");
        if (spec.Distinct) sb.Append("DISTINCT ");
        // TOP vs OFFSET/FETCH are mutually exclusive in T-SQL. Postgres puts the limit at the
        // tail (LIMIT N) instead, so dialects whose LimitGoesBeforeColumns=false emit "" here
        // and the limit is rendered later in BuildOffsetFetch via dialect.LimitOffsetClause.
        var usingOffsetFetch = spec.Offset.HasValue && spec.Offset.Value > 0;
        if (!usingOffsetFetch && spec.Limit.HasValue && spec.Limit.Value > 0)
            sb.Append(_dialect.TopClause(spec.Limit.Value));

        // GROUP BY safety: when aggregations are present, every non-aggregate column in the
        // SELECT list must also appear in GROUP BY (SQL Server otherwise rejects the query
        // with "Column ... is invalid in the select list because it is not contained in
        // either an aggregate function or the GROUP BY clause"). Drop offenders rather than
        // emit invalid SQL.
        var hasAggregations = spec.Aggregations.Any(a => NormalizeAggFn(a.Function) is not null);
        var groupBySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // F1 — nullable GROUP BY columns get wrapped with ISNULL so the NULL bucket has a
        // human-readable label ("(Unassigned)") instead of a blank row. Shared with BuildGroupBy via _groupByDisplayMap.
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

        // Pre-pass: detect bare-column-name collisions (TicketStatuses.Name + TicketPriorities.Name).
        // Without aliasing, the result Dictionary collapses them under one key — second wins.
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
            // Inline aggregate expression ("AVG(X.Y) AS z") — render verbatim with qualified columns. Otherwise TryFormatColumn rejects it and the aggregation vanishes silently.
            if (LooksLikeAggregateExpression(col))
            {
                var qualified = QualifyColumnsInExpression(col, spec.Root);
                items.Add(qualified);
                continue;
            }
            // Non-aggregate inline expression in select (CASE WHEN / FORMAT / DATEDIFF / CAST /
            // ISNULL / etc.). Same pattern as the aggregation case above — without this branch
            // TryFormatColumn fails and the column vanishes silently from the result.
            if (LooksLikeColumnExpression(col))
            {
                items.Add(QualifyColumnsInExpression(StripTrailingAlias(col), spec.Root));
                continue;
            }
            if (!TryFormatColumn(col, out var f)) continue;
            if (hasAggregations && !groupBySet.Contains(f))
                continue; // would violate aggregate/GROUP BY rule
            var (table, bare) = SplitQualified(col);
            // PII denylist (#55) — refuse to emit any sensitive column. Executor also masks post-fetch as defence-in-depth.
            if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(bare)
                && _semanticLayer.IsSensitiveColumn(table, bare))
            {
                continue;
            }
            // F1 — nullable-GROUP-BY column: swap in the ISNULL-wrapped expression with explicit AS <alias>.
            if (_groupByDisplayMap.TryGetValue(f, out var wrappedExpr))
            {
                items.Add($"{wrappedExpr} AS {_dialect.QuoteIdentifier(bare)}");
            }
            else if (!string.IsNullOrEmpty(bare)
                && bareCounts.TryGetValue(bare, out var cnt) && cnt > 1
                && !string.IsNullOrEmpty(table))
            {
                // J2 — alias colliding columns. For "Table.Name" + "Table2.Name", use singularised table name as alias.
                var friendly = string.Equals(bare, "Name", StringComparison.OrdinalIgnoreCase)
                    ? SingulariseTableForAlias(table)
                    : $"{table}_{bare}";
                items.Add($"{f} AS {_dialect.QuoteIdentifier(friendly)}");
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

            // Reject AVG/SUM/MIN/MAX(*) — invalid T-SQL. Folded into expr: form by RewriteInvalidStarAggregates when possible.
            if (a.Column == SpecConst.Aggregates.Star && fn != SpecConst.Aggregates.Count) continue;

            // PII guard — bare-column form. The expression-form check below covers inline cases.
            if (!string.IsNullOrEmpty(a.Column) && a.Column != "*")
            {
                var (aggTable, aggBare) = SplitQualified(a.Column);
                if (!string.IsNullOrEmpty(aggTable) && !string.IsNullOrEmpty(aggBare)
                    && _semanticLayer.IsSensitiveColumn(aggTable, aggBare))
                    continue;
            }

            string colExpr;
            if (a.Column == "*")
            {
                colExpr = "*";
            }
            else if (a.Column.StartsWith("expr:", StringComparison.OrdinalIgnoreCase))
            {
                // Per-row expression folded by RewriteInvalidStarAggregates; render inline with qualification.
                var raw = a.Column.Substring("expr:".Length);
                if (ExpressionReferencesSensitiveColumn(raw)) continue;
                colExpr = QualifyColumnsInExpression(StripTrailingAlias(raw), spec.Root);
            }
            else if (LooksLikeColumnExpression(a.Column))
            {
                // Inline expression form (CASE WHEN / DATEDIFF / FORMAT / ISNULL / etc.) emitted
                // directly into the aggregation's column slot. Without this branch the column
                // fails TryFormatColumn and the aggregation is silently dropped — the wrong-
                // answer class the user explicitly called out.
                if (ExpressionReferencesSensitiveColumn(a.Column)) continue;
                colExpr = QualifyColumnsInExpression(StripTrailingAlias(a.Column), spec.Root);
            }
            else if (!TryFormatColumn(a.Column, out var f)) continue;
            else colExpr = f;

            // Distinct aggregates only apply to real columns; COUNT(DISTINCT *) isn't valid SQL.
            var inner = a.Distinct && colExpr != "*" ? $"DISTINCT {colExpr}" : colExpr;
            var alias = string.IsNullOrWhiteSpace(a.Alias) ? fn : a.Alias;
            items.Add($"{fn}({inner}) AS {_dialect.QuoteIdentifier(alias)}");
        }

        // Computed columns: rendered verbatim with an alias. Validator's AST pass catches name errors.
        foreach (var comp in spec.Computed)
        {
            if (string.IsNullOrWhiteSpace(comp.Expression)) continue;
            // PII guard — refuse to project a computed expression that touches a sensitive column,
            // even indirectly. Symmetric with the bare-column denylist in BuildSelect.
            if (ExpressionReferencesSensitiveColumn(comp.Expression)) continue;
            // Strip trailing " AS xxx" — we always append our own "AS [<alias>]" below; double-AS is a syntax error.
            var rawExpr = StripTrailingAlias(comp.Expression);
            var expr = QualifyColumnsInExpression(rawExpr, spec.Root);
            var alias = string.IsNullOrWhiteSpace(comp.Alias) ? "Computed" : comp.Alias;
            // Sanitize alias: strip brackets, drop any qualifier (Table.Column → Column), replace
            // disallowed identifier chars (dots, hyphens, square brackets). The LLM occasionally
            // emits the qualified column name AS the alias — producing "[Tickets].[RegionId] AS
            // [Tickets].[RegionId]" which SQL Server rejects with "Incorrect syntax near '.'".
            // Reproduced on session 134 case GRP-SIMPLE-1.
            alias = alias.Replace("[", "").Replace("]", "");
            var dotIdx = alias.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx + 1 < alias.Length) alias = alias.Substring(dotIdx + 1);
            alias = alias.Replace('-', '_').Trim();
            if (string.IsNullOrEmpty(alias)) alias = "Computed";
            items.Add($"{expr} AS {_dialect.QuoteIdentifier(alias)}");
        }

        // Empty SELECT defaulting: prefer GROUP BY columns (aggregate-safe), then DisplayColumns + filter columns, then "*".
        if (items.Count == 0)
        {
            if (hasAggregations || spec.GroupBy.Count > 0)
            {
                foreach (var g in spec.GroupBy)
                    if (TryFormatColumn(g, out var f)) items.Add(f);
                if (items.Count == 0 && hasAggregations) items.Add($"COUNT(*) AS {_dialect.QuoteIdentifier("Count")}");
            }
            if (items.Count == 0)
            {
                // Build a meaningful default rather than star-expand:
                //   1. Root entity DisplayColumns from semantic layer.
                //   2. Filter columns on joined tables (so the user sees WHY a row matched).
                //   3. Fall back to "*" only when both sources are empty.
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

        // #41 — dedupe colliding aliases (real / aggregate / computed sources can independently produce the same name).
        DedupeAliasesInPlace(items);
        sb.AppendLine(string.Join(", ", items));
    }

    /// <summary>
    /// Default SELECT list when the planner left <see cref="QuerySpec.Select"/> empty.
    /// Order: semantic-layer DisplayColumns → Id + natural-key + label column → empty (caller falls back to "*").
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
        // Fallback chain — only when DisplayColumns is missing or every entry was stale.
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
        foreach (var name in _semanticLayer.Config.Defaults.LabelColumnPreference)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!_catalog.ColumnExists(table, name)) continue;
            var q = $"{table}.{name}";
            if (emitted.Add(q)) { yield return q; break; }
        }
    }
}
