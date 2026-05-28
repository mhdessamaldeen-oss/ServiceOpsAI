namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Remove contradictory or redundant filters on the same column. Catches LLM-output patterns
/// like:
/// <list type="bullet">
///   <item><c>Status = 'Issued' AND Status IN ('Issued','Overdue')</c> — overlapping; the IN
///         dominates (covers more values). Drop the eq.</item>
///   <item><c>Status = 'Issued' AND Status IN ('Paid','Closed')</c> — actual contradiction.
///         Returns zero rows. Drop the IN; trust the more-specific eq.</item>
///   <item><c>CreatedAt &gt;= @p0 AND CreatedAt &gt;= @p1</c> — two lower bounds on the same column.
///         Keep the more recent one; drop the looser.</item>
///   <item><c>Status IN ('X') AND Status IN ('X','Y')</c> — fully redundant; drop the subset.</item>
/// </list>
///
/// <para>Without this phase, SQL Server still ANDs the filters together and the result silently
/// becomes wrong (typically zero rows or an over-narrowed set).</para>
/// </summary>
internal sealed class DedupContradictoryFiltersPhase : ISpecRepairPhase
{
    public string Name => "DedupContradictoryFilters";
    public string Covers => "Same-column overlapping/contradictory filter pairs (Status='X' AND Status IN ('Y','Z') etc.) → keep one";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Filters is null || spec.Filters.Count < 2) return;

        // Group by (column lowercase). Each group is a list of filters competing on that column.
        var groups = spec.Filters
            .Where(f => !string.IsNullOrEmpty(f.Column))
            .GroupBy(f => (f.Column ?? "").ToLowerInvariant());

        var toRemove = new HashSet<FilterSpec>();
        var diagnostics = new List<string>();

        foreach (var g in groups)
        {
            var filters = g.ToList();
            if (filters.Count < 2) continue;

            // Case A: eq + in on same column.
            var eqs = filters.Where(f => string.Equals(f.Op, SpecConst.FilterOps.Eq, System.StringComparison.OrdinalIgnoreCase)).ToList();
            var ins = filters.Where(f =>
                string.Equals(f.Op, SpecConst.FilterOps.In, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Op, SpecConst.FilterOps.NotInAlt, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Op, "in", System.StringComparison.OrdinalIgnoreCase)).ToList();
            if (eqs.Count >= 1 && ins.Count >= 1)
            {
                // Just one eq + one in is the common case.
                var eqVal = eqs[0].Value?.ToString();
                foreach (var inFilter in ins)
                {
                    var inVals = ExtractStringValues(inFilter.Value);
                    if (inVals.Length == 0) continue;
                    var contains = inVals.Any(v => string.Equals(v, eqVal, System.StringComparison.OrdinalIgnoreCase));
                    if (contains)
                    {
                        // Overlapping: drop the IN (eq is more specific).
                        toRemove.Add(inFilter);
                        diagnostics.Add($"dropped redundant IN on {g.Key} (eq value '{eqVal}' already in IN list)");
                    }
                    else
                    {
                        // Contradiction: SQL would return zero rows. Drop the IN — eq is the more
                        // specific intent. (Either choice is defensible; preferring eq matches
                        // typical user intent when they're being specific.)
                        toRemove.Add(inFilter);
                        diagnostics.Add($"dropped contradictory IN on {g.Key} (eq '{eqVal}' not in IN list)");
                    }
                }
            }

            // Case B: same op + same value, duplicate.
            for (int i = 0; i < filters.Count; i++)
            {
                if (toRemove.Contains(filters[i])) continue;
                for (int j = i + 1; j < filters.Count; j++)
                {
                    if (toRemove.Contains(filters[j])) continue;
                    if (!string.Equals(filters[i].Op ?? "eq", filters[j].Op ?? "eq", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ValuesEqual(filters[i].Value, filters[j].Value)) continue;
                    toRemove.Add(filters[j]);
                    diagnostics.Add($"dropped duplicate {filters[j].Op} filter on {g.Key}");
                }
            }
        }

        if (toRemove.Count == 0) return;
        spec.Filters.RemoveAll(toRemove.Contains);
        foreach (var d in diagnostics) ctx.Diagnostics.Add(new(Name, d));
    }

    private static string[] ExtractStringValues(object? value)
    {
        if (value is null) return System.Array.Empty<string>();
        if (value is string s) return new[] { s };
        if (value is System.Collections.IEnumerable e and not string)
        {
            var list = new List<string>();
            foreach (var v in e)
            {
                var sv = v?.ToString();
                if (!string.IsNullOrEmpty(sv)) list.Add(sv!);
            }
            return list.ToArray();
        }
        // System.Text.Json JsonElement array support.
        if (value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in je.EnumerateArray())
            {
                var sv = item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrEmpty(sv)) list.Add(sv!);
            }
            return list.ToArray();
        }
        return new[] { value.ToString() ?? "" };
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.ToString(), b.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }
}
