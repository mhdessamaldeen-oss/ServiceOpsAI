namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Selection;

using SuperAdminCopilot.Models;

/// <summary>
/// When the spec has a GROUP BY, every non-aggregated SELECT column / Computed expression
/// must also appear in GROUP BY. Otherwise SQL Server rejects with "Column X is invalid in
/// the select list because it is not contained in either an aggregate function or the GROUP
/// BY clause" — exact error from B-WIN-run-2 in session 109.
///
/// <para>This phase reconciles the two sides: any non-aggregated SELECT expression that the
/// LLM forgot to include in GROUP BY gets added there. Aggregations (COUNT/SUM/AVG/MIN/MAX
/// in spec.Aggregations) are exempt. Date-bucket expressions are exempt because they ARE
/// the GROUP BY canonicalised by the TimeSeriesBucketing phase.</para>
///
/// <para>Conservative gates:
/// <list type="bullet">
///   <item>Fires only when GROUP BY is non-empty (otherwise SQL has no group constraint to satisfy)</item>
///   <item>Skips raw column refs already in GROUP BY</item>
///   <item>Skips aggregation aliases in SELECT (those resolve via aggregations[])</item>
/// </list></para>
/// </summary>
internal sealed class EnsureSelectInGroupByPhase : ISpecRepairPhase
{
    public string Name => "EnsureSelectInGroupBy";
    public string Covers => "Reconcile SELECT vs GROUP BY: non-aggregated SELECT columns must be in GROUP BY";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.GroupBy.Count == 0) return;

        // Build a set of expressions already in GROUP BY (normalized).
        var groupBySet = new System.Collections.Generic.HashSet<string>(
            spec.GroupBy.Select(Normalise),
            System.StringComparer.OrdinalIgnoreCase);

        var added = 0;

        // SELECT items — bare column references like "Tickets.CreatedAt".
        foreach (var sel in spec.Select)
        {
            var n = Normalise(sel);
            // Skip if already in GROUP BY OR if it's an aggregation column (COUNT(*)/etc.).
            if (groupBySet.Contains(n)) continue;
            if (n.Contains("count(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("sum(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("avg(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("min(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("max(", System.StringComparison.OrdinalIgnoreCase)) continue;
            spec.GroupBy.Add(sel);
            groupBySet.Add(n);
            added++;
        }

        // Computed expressions that aren't aggregations or date buckets.
        foreach (var c in spec.Computed)
        {
            if (string.IsNullOrEmpty(c.Expression)) continue;
            var n = Normalise(c.Expression);
            if (groupBySet.Contains(n)) continue;
            var ex = c.Expression;
            // Skip expressions that ARE aggregations.
            if (ex.Contains("COUNT(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("SUM(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("AVG(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("MIN(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("MAX(", System.StringComparison.OrdinalIgnoreCase)) continue;
            spec.GroupBy.Add(ex);
            groupBySet.Add(n);
            added++;
        }

        // ORDER BY columns also need to be in GROUP BY (or be aggregations). Same reconciliation
        // logic as SELECT. Caught HAV-AR-1 in session 121 where ORDER BY Tickets.RegionId failed.
        foreach (var o in spec.OrderBy)
        {
            if (string.IsNullOrEmpty(o.Column)) continue;
            var n = Normalise(o.Column);
            if (groupBySet.Contains(n)) continue;
            if (n.Contains("count(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("sum(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("avg(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("min(", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("max(", System.StringComparison.OrdinalIgnoreCase)) continue;
            // ORDER BY may reference an aggregate ALIAS — those would not contain '('. Conservative:
            // only add to GROUP BY when the column has a table.column shape.
            if (!o.Column.Contains('.')) continue;
            spec.GroupBy.Add(o.Column);
            groupBySet.Add(n);
            added++;
        }

        if (added > 0)
            ctx.Diagnostics.Add(new(Name, $"appended {added} non-aggregated SELECT/ORDER-BY expression(s) to GROUP BY"));
    }

    private static string Normalise(string s) => (s ?? "").Replace("[", "").Replace("]", "").Replace(" ", "").ToLowerInvariant();
}
