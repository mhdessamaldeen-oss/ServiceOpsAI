namespace SuperAdminCopilot.Compilation;

using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;

/// <summary>
/// Resolves every <c>metric:&lt;name&gt;</c> / <c>dimension:&lt;name&gt;</c> reference in a
/// <see cref="QuerySpec"/> into concrete SQL fragments using the semantic layer. Metric refs
/// in aggregations become inline expressions on synthesized <see cref="ComputedSpec"/>;
/// dimension refs in SELECT / GROUP BY / Computed become real column references or
/// computed expressions.
///
/// <para>Extracted from <c>SqlCompiler</c> as part of the 2026-06-01 de-couple pass. The
/// expander operates on the mutable v2 spec because that's the form the compiler consumes.
/// Owning this concern as a collaborator means the compiler's <see cref="SqlCompiler.Compile"/>
/// can read as "expand semantic refs → enrich → emit SQL" instead of weaving metric/dimension
/// translation through filter rewriting and group-by inference.</para>
///
/// <para>Unit-suffix logic for metric aliases (e.g. metric <c>total_size_mb</c> → alias
/// <c>"Total (MB)"</c>) lives here too — it's the semantic-layer's data-driven naming
/// convention, not a compiler concern.</para>
/// </summary>
public interface ISemanticExpander
{
    /// <summary>Mutate the spec in place — replace metric/dimension tokens with real SQL fragments.</summary>
    void Expand(QuerySpec spec);
}

internal sealed class SemanticExpander : ISemanticExpander
{
    private readonly ISemanticLayer _semanticLayer;

    public SemanticExpander(ISemanticLayer semanticLayer)
    {
        _semanticLayer = semanticLayer;
    }

    public void Expand(QuerySpec spec)
    {
        if (spec is null) return;

        // 1) Aggregations: function = "metric:<name>" → expand to inline computed expression
        //    and merge metric.Filters into spec.Filters (deduped by column+op).
        for (int i = spec.Aggregations.Count - 1; i >= 0; i--)
        {
            var a = spec.Aggregations[i];
            if (!a.Function.StartsWith("metric:", System.StringComparison.OrdinalIgnoreCase)) continue;
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
                    string.Equals(ef.Column, mf.Column, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ef.Op, mf.Op, System.StringComparison.OrdinalIgnoreCase));
                if (!dup)
                    spec.Filters.Add(new FilterSpec { Column = mf.Column, Op = mf.Op, Value = mf.Value });
            }
        }

        // 2) Select entries: "dimension:<name>" → either a column ref (rewrite in place) or
        //    a computed expression (move to spec.Computed).
        for (int i = spec.Select.Count - 1; i >= 0; i--)
        {
            var s = spec.Select[i];
            if (!s.StartsWith("dimension:", System.StringComparison.OrdinalIgnoreCase)) continue;
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
            if (!g.StartsWith("dimension:", System.StringComparison.OrdinalIgnoreCase)) continue;
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
            if (!c.Expression.StartsWith("dimension:", System.StringComparison.OrdinalIgnoreCase)) continue;
            var dim = _semanticLayer.GetDimension(c.Expression);
            if (dim is null) continue;
            spec.Computed[i] = new ComputedSpec
            {
                Alias = string.IsNullOrEmpty(c.Alias) ? dim.Name : c.Alias,
                Expression = dim.Expression ?? dim.Column ?? "",
            };
        }
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
        var unit = UnitSuffixes.FirstOrDefault(u => lowerName.EndsWith(u, System.StringComparison.Ordinal));
        if (unit is null) return alias;
        var unitLabel = unit.TrimStart('_').ToUpperInvariant();
        if (alias.Contains(unitLabel, System.StringComparison.OrdinalIgnoreCase)) return alias;
        return $"{alias} ({unitLabel})";
    }
}
