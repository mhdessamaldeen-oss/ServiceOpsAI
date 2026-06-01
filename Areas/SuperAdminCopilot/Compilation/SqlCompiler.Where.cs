namespace SuperAdminCopilot.Compilation;

using System.Text;
using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

// WHERE-clause emitter, filter rewrites (synonym, lookup-name, dropped-column-ref),
// text-search expansion, OR grouping, placeholder-token rejection, and temporal-token expansion.
// Splits ~420 lines out of the main file.
internal sealed partial class SqlCompiler
{
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

        // Collapse "same column, same op, multiple values" into a single OR group. Spec grammar has no OR,
        // so without this the compiler ANDs them and the query returns 0 rows.
        var groupedFilters = OrGroupFilters(spec.Filters ?? new List<FilterSpec>());

        var pieces = new List<string>();
        foreach (var group in groupedFilters)
        {
            if (group.Count == 1)
            {
                var filter = group[0];

                // text_search — cross-column OR over the entity's SearchableColumns.
                if (string.Equals(filter.Op, SpecConst.FilterOps.TextSearch, StringComparison.OrdinalIgnoreCase))
                {
                    var clauseTs = BuildTextSearchClause(spec.Root, filter, parameters, ref paramIndex);
                    if (!string.IsNullOrEmpty(clauseTs)) pieces.Add(clauseTs);
                    continue;
                }

                // Rewrite BEFORE formatting so a lookup-name filter (StatusId='Closed' → TicketStatuses.Name='Closed') gets the new column path.
                var rewritten = _filterValueRewriter.Rewrite(filter);
                if (!TryFormatColumn(rewritten.Column, out var col))
                {
                    _warnings.Add(Abstractions.CopilotWarning.UnknownColumn(rewritten.Column));
                    continue;
                }
                rewritten = CoerceValueByColumnType(rewritten);
                var clause = BuildFilterClause(col, rewritten, parameters, ref paramIndex, _dialect);
                if (!string.IsNullOrEmpty(clause)) pieces.Add(clause);
                else _warnings.Add(Abstractions.CopilotWarning.RejectedFilterValue(rewritten.Column));
                continue;
            }

            // Multi-filter group on same column+op: emit "(c1 OR c2 OR c3)".
            var firstRewritten = _filterValueRewriter.Rewrite(group[0]);
            if (!TryFormatColumn(firstRewritten.Column, out var sharedCol))
            {
                _warnings.Add(Abstractions.CopilotWarning.UnknownColumn(firstRewritten.Column));
                continue;
            }
            var sub = new List<string>();
            foreach (var filter in group)
            {
                var rewritten = CoerceValueByColumnType(_filterValueRewriter.Rewrite(filter));
                var clause = BuildFilterClause(sharedCol, rewritten, parameters, ref paramIndex, _dialect);
                if (!string.IsNullOrEmpty(clause)) sub.Add(clause);
            }
            if (sub.Count == 1) pieces.Add(sub[0]);
            else if (sub.Count > 1) pieces.Add("(" + string.Join(" OR ", sub) + ")");
        }

        // Anti-join IS NULL clauses for each LEFT JOIN target declared as "anti".
        if (joinEdges is not null && joinKindByTable is not null)
        {
            foreach (var edge in joinEdges)
            {
                if (!joinKindByTable.TryGetValue(edge.TargetTable, out var k) || k != SpecConst.JoinKinds.Anti) continue;
                var fk = edge.Fk;
                // The "PK" side of the FK — when LEFT JOIN found no row, every related-table column is NULL.
                var antiCol = string.Equals(fk.ParentTable, edge.TargetTable, StringComparison.OrdinalIgnoreCase)
                    ? _dialect.QuoteQualified(fk.ParentTable, fk.ParentColumn)
                    : _dialect.QuoteQualified(fk.ReferencedTable, fk.ReferencedColumn);
                pieces.Add($"{antiCol} IS NULL");
            }
        }

        if (pieces.Count == 0) return;
        sb.Append("WHERE ").AppendLine(string.Join(" AND ", pieces));
    }

    // ApplyFilterRewrite + ApplySynonymRewrite + TryRewriteLookupNameFilter moved to
    // IFilterValueRewriter (2026-06-01). Callers use _filterValueRewriter.Rewrite(filter).
    // See Compilation/IFilterValueRewriter.cs.

    /// <summary>Group adjacent filters that share (Column, Op) so they can render as a single OR group. Only LIKE/eq/neq are grouped.</summary>
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

    /// <summary>text_search expansion: cross-column OR LIKE over the entity's SearchableColumns. Null = entity has no searchable columns configured.</summary>
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

        if (!phrase.Contains('%')) phrase = "%" + phrase + "%";

        // Single shared parameter — same value, multiple columns.
        var p = "@p" + paramIndex++;
        parameters[p] = phrase;

        var clauses = new List<string>();
        foreach (var colName in entity.SearchableColumns)
        {
            if (!_catalog.ColumnExists(rootTable, colName)) continue;
            clauses.Add($"{_dialect.QuoteQualified(rootTable, colName)} {_dialect.LikeOperator} {p}");
        }
        if (clauses.Count == 0) return null;
        return clauses.Count == 1 ? clauses[0] : "(" + string.Join(" OR ", clauses) + ")";
    }

    private string? BuildFilterClause(
        string col, FilterSpec f, Dictionary<string, object?> parameters, ref int paramIndex,
        Dialects.ISqlDialect dialect)
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
                    if (IsPlaceholderToken(v)) return null;
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
                v = AutoWrapLikePattern(v);
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} {dialect.LikeOperator} {p}";
            }
            case SpecConst.FilterOps.NotLike:
            {
                // J7 — without this branch the default arm fell through to `=`, silently converting not_like into = with percent-signs.
                var v = ExtractValues(f.Value).FirstOrDefault();
                if (IsPlaceholderToken(v)) return null;
                v = AutoWrapLikePattern(v);
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} {dialect.NotLikeOperator} {p}";
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
                // Temporal tokens expand to inline dialect-specific date math — not parameterized.
                if (v is string s && _temporalTokenizer.TryExpand(s, dialect, out var sqlExpr))
                    return $"{col} {sqlOp} {sqlExpr}";
                if (IsPlaceholderToken(v)) return null;
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} {sqlOp} {p}";
            }
        }
    }

    // 2026-06-01 — REVERTED hierarchical-lookup expansion. It fired on ANY equality on a
    // self-FK table, including the auto-injected soft-delete filter (Tickets.IsDeleted = 0,
    // because Tickets has a ParentTicketId self-FK), wrapping it in a 3-level subquery. One
    // test query ran for 181 seconds. It also never helped its intended "tickets in Damascus"
    // case (the planner filtered the region with LIKE on a joined table, which the eq-only
    // expansion never touched). Net-negative; removed. Region-hierarchy traversal, if needed,
    // belongs in a targeted + unit-tested repair rule, not a blanket compiler expansion.

    /// <summary>
    /// Wrap a LIKE pattern with leading/trailing <c>%</c> when it has no wildcards. The LLM
    /// frequently emits a plain word ("Open", "Electricity") with op=like; without wrapping,
    /// LIKE devolves to an equality check on a string with no metacharacters and silently
    /// returns zero rows for substring-intent questions. Leaves user-supplied wildcards alone.
    /// Pass-through for null / empty / non-string values.
    /// </summary>
    private static object? AutoWrapLikePattern(object? value)
    {
        if (value is not string s || s.Length == 0) return value;
        if (s.Contains('%') || s.Contains('_') || s.Contains('[')) return value;
        return "%" + s + "%";
    }

    /// <summary>Drop filters whose value is a planner-emitted placeholder ("@p0", "?", "(SELECT...)", "{...JSON...}") — avoids nvarchar→int conversion errors.</summary>
    private static bool IsPlaceholderToken(object? value)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return false;
        var t = s.Trim();
        if (t == "?") return true;
        // Subquery-as-string echo: drop rather than parameterize as nvarchar.
        if (t.Length > 8 && t[0] == '(' && t.StartsWith("(SELECT", StringComparison.OrdinalIgnoreCase)) return true;
        // JSON envelope echo: the LLM's own structured output ({"intent":..} / {"query":..} / [{"…":..}]) leaked into a value field. Drop the filter — the spec is broken; running it would crash SQL Server with a nvarchar→int conversion error.
        if (t.Length > 2 && (t[0] == '{' || t[0] == '[') && JsonValueShape.IsMatch(t)) return true;
        if (t.Length < 2 || t[0] != '@') return false;
        // "@p0" / "@param" / "@arg" / etc. — temporal tokens are caught earlier.
        return true;
    }

    // Detects a JSON-object / JSON-array literal masquerading as a filter value: starts with { or [ and contains a "word": pattern within the first ~100 chars.
    private static readonly Regex JsonValueShape = new(
        @"^[\{\[].*""[A-Za-z_]\w*""\s*:",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Type-coerce a filter's value to match the target column's SQL type before parameter binding.
    /// The LLM emits every value as a JSON string (e.g. <c>"2026-05-26"</c>, <c>"50000"</c>, <c>"true"</c>).
    /// Without this, SqlClient binds them all as <c>nvarchar</c> and SQL Server's implicit conversion
    /// fails for date/datetime/decimal columns ("Conversion failed when converting…").
    ///
    /// <para>Returns a new <see cref="FilterSpec"/> with <see cref="FilterSpec.Value"/> retyped to
    /// the CLR type that SqlClient maps to the column's SQL type. Falls back to the original value
    /// on unknown column, unknown SQL type, parse failure, placeholder/@token, or already-typed value.</para>
    ///
    /// <para>For IN/NOTIN with array values, coerces each element independently.</para>
    /// </summary>
    private FilterSpec CoerceValueByColumnType(FilterSpec filter)
    {
        if (filter is null || filter.Value is null) return filter!;
        var op = (filter.Op ?? "eq").ToLowerInvariant();
        // isnull/notnull have no value; text_search is handled separately and always nvarchar.
        if (op is "isnull" or "notnull" or "text_search") return filter;

        var (table, column) = SplitQualified(filter.Column);
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) return filter;
        var colInfo = _catalog.GetColumns(table).FirstOrDefault(c =>
            string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase));
        if (colInfo is null) return filter;
        var sqlType = colInfo.DataType ?? "";
        if (string.IsNullOrEmpty(sqlType)) return filter;

        // IN / NOTIN — coerce each array element.
        if (op is "in" or "notin")
        {
            var arr = ExtractValues(filter.Value);
            if (arr.Length == 0) return filter;
            var coerced = new object?[arr.Length];
            var anyChanged = false;
            for (var i = 0; i < arr.Length; i++)
            {
                coerced[i] = TryCoerceScalar(arr[i], sqlType, out var c) ? c : arr[i];
                if (!ReferenceEquals(coerced[i], arr[i])) anyChanged = true;
            }
            if (!anyChanged) return filter;
            return new FilterSpec { Column = filter.Column, Op = filter.Op, Value = coerced };
        }

        // Scalar value.
        if (!TryCoerceScalar(filter.Value, sqlType, out var coercedScalar)) return filter;
        if (ReferenceEquals(coercedScalar, filter.Value)) return filter;
        return new FilterSpec { Column = filter.Column, Op = filter.Op, Value = coercedScalar };
    }

    /// <summary>
    /// Coerce a single CLR value (typically <see cref="string"/> from the LLM JSON) to the CLR type
    /// that matches the given SQL data type. Returns true if a coercion happened; false if the value
    /// should be passed through unchanged (placeholder token, JSON literal, already-typed, parse error).
    /// </summary>
    private static bool TryCoerceScalar(object? value, string sqlType, out object? coerced)
    {
        coerced = value;
        if (value is null) return false;
        // Already a non-string CLR type (DateTime, int, decimal, etc.) — SqlClient handles it.
        if (value is not string s) return false;
        if (s.Length == 0) return false;
        // Temporal token (@today / @days:-7) and other planner placeholders are processed earlier;
        // never coerce these — they'd otherwise be parsed as bogus DateTime/int values.
        if (s[0] == '@') return false;
        if (IsPlaceholderToken(s)) return false;

        var lower = sqlType.ToLowerInvariant();
        // Date / datetime family. SqlClient maps DateTime → datetime2/datetime/date/smalldatetime.
        if (lower is "date" or "datetime" or "datetime2" or "smalldatetime")
        {
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
            {
                coerced = dt; return true;
            }
            return false;
        }
        if (lower == "datetimeoffset")
        {
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var dto))
            {
                coerced = dto; return true;
            }
            return false;
        }
        if (lower == "time")
        {
            if (TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            {
                coerced = ts; return true;
            }
            return false;
        }
        // Integer family.
        if (lower is "int" or "bigint" or "smallint" or "tinyint")
        {
            if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var l))
            {
                coerced = l; return true;
            }
            return false;
        }
        // Decimal family.
        if (lower is "decimal" or "numeric" or "money" or "smallmoney")
        {
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                coerced = d; return true;
            }
            return false;
        }
        // Floating point.
        if (lower is "float" or "real")
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
            {
                coerced = f; return true;
            }
            return false;
        }
        // Bit — accept "true"/"false"/"1"/"0".
        if (lower == "bit")
        {
            if (bool.TryParse(s, out var b)) { coerced = b; return true; }
            if (s == "1") { coerced = true; return true; }
            if (s == "0") { coerced = false; return true; }
            return false;
        }
        // GUID.
        if (lower == "uniqueidentifier")
        {
            if (Guid.TryParse(s, out var g)) { coerced = g; return true; }
            return false;
        }
        // String family (nvarchar, varchar, nchar, char, text, ntext) — leave as string.
        return false;
    }

    // TryExpandTemporalToken moved to ITemporalTokenizer / TemporalTokenizer (2026-06-01).
    // Both WHERE-builder and QualifyColumnsInExpression call _temporalTokenizer.TryExpand —
    // single source of truth for the grammar. See Compilation/ITemporalTokenizer.cs.
}
