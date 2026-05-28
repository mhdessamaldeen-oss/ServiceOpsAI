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
                var rewritten = ApplyFilterRewrite(filter);
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
            var firstRewritten = ApplyFilterRewrite(group[0]);
            if (!TryFormatColumn(firstRewritten.Column, out var sharedCol))
            {
                _warnings.Add(Abstractions.CopilotWarning.UnknownColumn(firstRewritten.Column));
                continue;
            }
            var sub = new List<string>();
            foreach (var filter in group)
            {
                var rewritten = CoerceValueByColumnType(ApplyFilterRewrite(filter));
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

    /// <summary>Apply value-synonym rewrite ("urgent" → "Critical") for column-context synonyms. No-op for null/array/@-token.</summary>
    private FilterSpec ApplySynonymRewrite(FilterSpec filter)
    {
        if (filter.Value is not string sv || sv.Length == 0 || sv[0] == '@') return filter;
        var canonical = _semanticLayer.ResolveSynonymValue(filter.Column, sv);
        if (ReferenceEquals(canonical, sv) || string.Equals(canonical, sv, StringComparison.Ordinal))
            return filter;
        return new FilterSpec { Column = filter.Column, Op = filter.Op, Value = canonical };
    }

    /// <summary>
    /// Combined filter rewrite: value-synonym + lookup-name rewrite (#5) + drop string-literal column refs (#6).
    /// All callers should use this; ApplySynonymRewrite is preserved as the last step.
    /// </summary>
    private FilterSpec ApplyFilterRewrite(FilterSpec filter)
    {
        if (filter is null) return filter!;

        // #6 — string-literal column reference: drop the filter when value is "Table.Column" and both halves resolve.
        if (filter.Value is string svRef)
        {
            if (Regex.IsMatch(svRef, @"^[A-Za-z_]\w*\.[A-Za-z_]\w*$"))
            {
                var (refTable, refCol) = SplitQualified(svRef);
                if (!string.IsNullOrEmpty(refTable) && !string.IsNullOrEmpty(refCol)
                    && _catalog.ColumnExists(refTable, refCol))
                {
                    // Drop via the @-placeholder path; the relationship is already wired by the join graph.
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

    /// <summary>Rewrite "<T>.<X>Id eq '<name>'" → "<RefTable>.<LabelCol> eq '<name>'" when the FK exists and RefTable has a label column. Null = no rewrite.</summary>
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

        // Find a label column on the referenced table. Prefer the entity's explicit LabelColumn;
        // otherwise walk the semantic-layer Defaults.LabelColumnPreference fallback list.
        string? labelCol = null;
        var refEntity = _semanticLayer.GetEntityForTable(fk.ReferencedTable);
        if (refEntity is not null && !string.IsNullOrEmpty(refEntity.LabelColumn)
            && _catalog.ColumnExists(fk.ReferencedTable, refEntity.LabelColumn))
        {
            labelCol = refEntity.LabelColumn;
        }
        if (labelCol is null)
        {
            foreach (var candidate in _semanticLayer.Config.Defaults.LabelColumnPreference)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (_catalog.ColumnExists(fk.ReferencedTable, candidate)) { labelCol = candidate; break; }
            }
        }
        if (labelCol is null) return null;

        return new FilterSpec
        {
            Column = $"{fk.ReferencedTable}.{labelCol}",
            Op = filter.Op,
            Value = stringValue,
        };
    }

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

    private static string? BuildFilterClause(
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
                if (v is string s && TryExpandTemporalToken(s, dialect, out var sqlExpr))
                    return $"{col} {sqlOp} {sqlExpr}";
                if (IsPlaceholderToken(v)) return null;
                var p = "@p" + paramIndex++;
                parameters[p] = v ?? DBNull.Value;
                return $"{col} {sqlOp} {p}";
            }
        }
    }

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

    /// <summary>
    /// Expand a planner-emitted '@'-token (today/yesterday/week_start/days:-7/etc.) to an inline
    /// dialect-specific date expression. False = unrecognized, parameterize as string.
    ///
    /// Every emission routes through <see cref="Dialects.ISqlDialect"/>, so the same token
    /// expands to T-SQL on MSSQL (e.g. <c>DATEADD(day, -1, CAST(GETDATE() AS DATE))</c>) and to
    /// Postgres on PG (<c>(CURRENT_DATE + INTERVAL '-1 day')</c>) without changing this method.
    /// </summary>
    private static bool TryExpandTemporalToken(string token, Dialects.ISqlDialect dialect, out string sqlExpr)
    {
        sqlExpr = "";
        if (string.IsNullOrEmpty(token) || token[0] != '@') return false;
        var t = token.AsSpan(1).Trim().ToString().ToLowerInvariant();

        var now = dialect.NowExpression;
        var today = dialect.CurrentDateExpression;

        switch (t)
        {
            case "now":           sqlExpr = now; return true;
            case "today":         sqlExpr = today; return true;
            case "yesterday":     sqlExpr = dialect.DateAdd("day", -1, today); return true;
            case "tomorrow":      sqlExpr = dialect.DateAdd("day",  1, today); return true;
            case "today_start":
            case "day_start":     sqlExpr = today; return true;

            case "week_start":    sqlExpr = dialect.DateTrunc("week",    now); return true;
            case "month_start":   sqlExpr = dialect.DateTrunc("month",   now); return true;
            case "year_start":    sqlExpr = dialect.DateTrunc("year",    now); return true;
            case "quarter_start": sqlExpr = dialect.DateTrunc("quarter", now); return true;

            case "last_week_start":    sqlExpr = dialect.DateAdd("week",    -1, dialect.DateTrunc("week",    now)); return true;
            case "last_month_start":   sqlExpr = dialect.DateAdd("month",   -1, dialect.DateTrunc("month",   now)); return true;
            case "last_year_start":    sqlExpr = dialect.DateAdd("year",    -1, dialect.DateTrunc("year",    now)); return true;
            case "last_quarter_start": sqlExpr = dialect.DateAdd("quarter", -1, dialect.DateTrunc("quarter", now)); return true;

            // Named quarter starts/ends, anchored to the current year. Used by the
            // InjectTemporalFilterFromQuestion phase when the question says "Q1 of this year" /
            // "first quarter". q{N}_start = first day of quarter N this year. q{N}_end = first
            // day of quarter N+1 (half-open interval). Q4_end rolls into the next year so a
            // "Q4" range remains valid.
            case "q1_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "1",  "1"); return true;
            case "q1_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "4",  "1"); return true;
            case "q2_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "4",  "1"); return true;
            case "q2_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "7",  "1"); return true;
            case "q3_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "7",  "1"); return true;
            case "q3_end":   sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "10", "1"); return true;
            case "q4_start": sqlExpr = dialect.DateFromParts(dialect.DatePart("year", now), "10", "1"); return true;
            case "q4_end":   sqlExpr = dialect.DateFromParts($"({dialect.DatePart("year", now)} + 1)", "1", "1"); return true;
        }

        // Year-specific anchor: "@yearmonth:YYYY:M" → first day of month M in year YYYY.
        // Solves the "Q1 2027 vs Feb 2019" problem — the InjectTemporalFilterFromQuestion
        // phase detects an explicit year in the question and emits this token instead of the
        // year-anchored q{N}_start (which silently uses YEAR(GETDATE())). Half-open ranges
        // become [yearmonth:Y:M, yearmonth:Y:M+1) — the phase emits both legs.
        if (t.StartsWith("yearmonth:", StringComparison.Ordinal))
        {
            var parts = t.Substring("yearmonth:".Length).Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var ym_year)
                && int.TryParse(parts[1], out var ym_month)
                && ym_year >= 1900 && ym_year <= 2200
                && ym_month >= 1 && ym_month <= 12)
            {
                sqlExpr = dialect.DateFromParts(ym_year.ToString(), ym_month.ToString(), "1");
                return true;
            }
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
                    // Day-or-coarser offsets anchor on midnight (CURRENT_DATE on PG, CAST(GETDATE() AS DATE) on MSSQL)
                    // so range comparisons against datetime columns include the full day.
                    var baseExpr = sqlUnit is "second" or "minute" or "hour" ? now : today;
                    sqlExpr = dialect.DateAdd(sqlUnit, offset, baseExpr);
                    return true;
                }
            }
        }

        return false;
    }
}
