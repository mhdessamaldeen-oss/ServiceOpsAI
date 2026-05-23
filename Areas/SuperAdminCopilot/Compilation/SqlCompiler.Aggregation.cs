namespace SuperAdminCopilot.Compilation;

using System.Text;
using SuperAdminCopilot.Models;

/// <summary>
/// D.4 — Aggregation cluster of <see cref="SqlCompiler"/>. Contains <c>GROUP BY</c> and
/// <c>HAVING</c> clause emitters. <c>ORDER BY</c> stays in the main file because its alias-
/// resolution logic interacts heavily with SELECT-side bookkeeping declared there; a deeper
/// extraction would force the SELECT cluster out at the same time and is a larger refactor.
/// </summary>
internal sealed partial class SqlCompiler
{
    private void BuildGroupBy(QuerySpec spec, StringBuilder sb)
    {
        if (spec.GroupBy is null || spec.GroupBy.Count == 0) return;
        var items = new List<string>();
        foreach (var col in spec.GroupBy)
        {
            if (TryFormatColumn(col, out var f))
            {
                // F1 — emit the ISNULL-wrapped form when this column was flagged as nullable
                // by BuildSelect, so the GROUP BY expression is lexically identical to the
                // SELECT-side expression and SQL Server treats them as the same group key.
                items.Add(_groupByDisplayMap.TryGetValue(f, out var wrapped) ? wrapped : f);
                continue;
            }
            // dimension expansion may have left a free-form SQL expression here — emit it
            // verbatim with column qualification so it groups correctly.
            if (!string.IsNullOrEmpty(col) && (col.Contains('(') || col.Contains(' ')))
                items.Add(QualifyColumnsInExpression(col, spec.Root));
        }
        if (items.Count == 0) return;
        sb.Append("GROUP BY ").AppendLine(string.Join(", ", items));
    }

    private void BuildHaving(QuerySpec spec, StringBuilder sb, Dictionary<string, object?> parameters, ref int paramIndex)
    {
        if (spec.Having is null || spec.Having.Count == 0) return;
        // HAVING is meaningful only with GROUP BY (or pure aggregates).
        var pieces = new List<string>();
        foreach (var h in spec.Having)
        {
            var fn = NormalizeAggFn(h.Function);
            if (fn is null) continue;

            string colExpr;
            if (h.Column == "*") colExpr = "*";
            else if (!TryFormatColumn(h.Column, out var f)) continue;
            else colExpr = f;

            var sqlOp = (h.Op ?? "gt").ToLowerInvariant() switch
            {
                "eq" => "=", "neq" => "<>", "ne" => "<>",
                "gt" => ">", "lt" => "<", "gte" => ">=", "lte" => "<=",
                _ => ">"
            };
            var v = ExtractValues(h.Value).FirstOrDefault();
            // Drop the entire HAVING clause when the value is a placeholder echo (the planner
            // sometimes copies "@p0" / "@p2" as a literal string from the retry prompt). Without
            // this skip, the parameter ends up as an nvarchar string and SQL Server fails with
            // "Conversion failed when converting the nvarchar value '@p2' to data type int."
            if (IsPlaceholderToken(v)) continue;
            var pn = "@p" + paramIndex++;
            parameters[pn] = v ?? DBNull.Value;
            pieces.Add($"{fn}({colExpr}) {sqlOp} {pn}");
        }
        if (pieces.Count == 0) return;
        sb.Append("HAVING ").AppendLine(string.Join(" AND ", pieces));
    }
}
