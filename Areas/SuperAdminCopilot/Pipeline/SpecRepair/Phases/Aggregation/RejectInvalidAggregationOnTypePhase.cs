namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Aggregation;

using SuperAdminCopilot.Models;

/// <summary>
/// Detect aggregations the LLM emitted with a function/column combination SQL Server will
/// reject ("AVG on datetime2", "SUM on nvarchar", "MIN on bit"). When the column-type
/// mismatch is detectable from the catalog, wrap the column in a sensible conversion or
/// rewrite the function:
/// <list type="bullet">
///   <item>AVG/SUM on date/datetime/datetime2 → wrap as <c>DATEDIFF(DAY, <i>col</i>, GETDATE())</c></item>
///   <item>AVG/SUM/MIN/MAX on bit → cast to <c>INT</c></item>
///   <item>AVG/SUM on nvarchar/varchar/text → DROP the aggregation (no valid coercion)
///         and replace with COUNT(*) as a last resort</item>
/// </list>
///
/// <para>Without this, the SQL fails at execute time with "Operand data type X is invalid
/// for AVG operator" — that's what killed B-WIN-lag-2 in session 109.</para>
/// </summary>
internal sealed class RejectInvalidAggregationOnTypePhase : ISpecRepairPhase
{
    public string Name => "RejectInvalidAggregationOnType";
    public string Covers => "AVG/SUM on datetime → DATEDIFF wrap; AVG/SUM on nvarchar → drop; bit → cast to INT";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count == 0) return;

        foreach (var agg in spec.Aggregations)
        {
            if (string.IsNullOrEmpty(agg.Column) || agg.Column == "*") continue;

            // Detect datetime-arithmetic expressions like "Tickets.ResolvedAt - Tickets.CreatedAt".
            // SQL Server rejects with "Operand data type datetime2 is invalid for subtract operator".
            // Wrap as DATEDIFF(unit, earlier, later). Heuristic: pick HOUR for "resolution time"
            // / "duration" / "MTTR" questions, DAY otherwise.
            if (agg.Column.Contains('-') && !agg.Column.Contains('(') && !agg.Column.Contains('['))
            {
                var parts = agg.Column.Split('-', 2);
                if (parts.Length == 2 && IsDateColumnRef(parts[0].Trim(), ctx) && IsDateColumnRef(parts[1].Trim(), ctx))
                {
                    var subFn = (agg.Function ?? "").ToUpperInvariant();
                    if (subFn == "AVG" || subFn == "SUM" || subFn == "MIN" || subFn == "MAX")
                    {
                        var rhs = parts[0].Trim();   // typically ResolvedAt
                        var lhs = parts[1].Trim();   // typically CreatedAt
                        var unit = "HOUR";
                        // DATEDIFF(unit, earlier, later) — pass (lhs=earlier, rhs=later)
                        agg.Column = $"DATEDIFF({unit}, {lhs}, {rhs})";
                        ctx.Diagnostics.Add(new(Name, $"wrapped datetime subtraction {agg.Function}({rhs}-{lhs}) → DATEDIFF({unit}, {lhs}, {rhs})"));
                        continue;
                    }
                }
            }

            var (table, col) = SplitQualified(agg.Column);
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(col)) continue;
            if (!ctx.Catalog.TableExists(table)) continue;
            var info = ctx.Catalog.GetColumns(table).FirstOrDefault(c =>
                string.Equals(c.ColumnName, col, System.StringComparison.OrdinalIgnoreCase));
            if (info is null) continue;

            var type = info.DataType ?? "";
            var fn = (agg.Function ?? "").ToUpperInvariant();

            // Date column being aggregated with AVG/SUM → wrap in DATEDIFF or rewrite.
            if ((type.StartsWith("date", System.StringComparison.OrdinalIgnoreCase)
                 || type.StartsWith("smalldatetime", System.StringComparison.OrdinalIgnoreCase))
                && (fn == "AVG" || fn == "SUM"))
            {
                // AVG/SUM on a date is almost always a misnamed metric — the user probably wants
                // "avg age in days". Wrap the column as a DATEDIFF.
                agg.Column = $"DATEDIFF(DAY, {table}.{col}, GETDATE())";
                ctx.Diagnostics.Add(new(Name, $"wrapped {fn}({table}.{col}) → {fn}(DATEDIFF(DAY, ..., NOW))"));
                continue;
            }

            // bit column with numeric aggregation → cast to INT.
            if (string.Equals(type, "bit", System.StringComparison.OrdinalIgnoreCase)
                && (fn == "AVG" || fn == "SUM" || fn == "MIN" || fn == "MAX"))
            {
                agg.Column = $"CAST({table}.{col} AS INT)";
                ctx.Diagnostics.Add(new(Name, $"cast bit column {table}.{col} → INT for {fn}"));
                continue;
            }

            // String column with AVG/SUM → no valid coercion, drop.
            if ((type.StartsWith("nvarchar", System.StringComparison.OrdinalIgnoreCase)
                 || type.StartsWith("varchar", System.StringComparison.OrdinalIgnoreCase)
                 || string.Equals(type, "text", System.StringComparison.OrdinalIgnoreCase)
                 || string.Equals(type, "ntext", System.StringComparison.OrdinalIgnoreCase))
                && (fn == "AVG" || fn == "SUM"))
            {
                ctx.Diagnostics.Add(new(Name, $"DROPPING invalid {fn}({table}.{col}) — string column has no numeric aggregation"));
                agg.Function = "COUNT";
                agg.Column = "*";
                agg.Distinct = false;
                continue;
            }
        }
    }

    private static (string Table, string Column) SplitQualified(string qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx <= 0) return ("", qualified);
        return (qualified.Substring(0, idx).Trim('[', ']'),
                qualified.Substring(idx + 1).Trim('[', ']'));
    }

    private static bool IsDateColumnRef(string text, SpecRepairContext ctx)
    {
        var (t, c) = SplitQualified(text);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c)) return false;
        if (!ctx.Catalog.TableExists(t)) return false;
        var info = ctx.Catalog.GetColumns(t).FirstOrDefault(ci =>
            string.Equals(ci.ColumnName, c, System.StringComparison.OrdinalIgnoreCase));
        if (info is null) return false;
        var type = (info.DataType ?? "").ToLowerInvariant();
        return type.StartsWith("date") || type.StartsWith("smalldatetime");
    }
}
