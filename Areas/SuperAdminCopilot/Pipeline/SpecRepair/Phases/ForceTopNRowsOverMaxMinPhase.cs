namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question is a TOPN shape ("top 5 customers by total billed", "3 worst regions by
/// outage count", "least 5 active customers by ticket count") AND the LLM emitted a single
/// MAX/MIN aggregation (collapsing into one row) AND a LIMIT/TOP-N row count was set, this
/// phase converts the spec to actual TOP-N rows ordered by the metric:
/// <list type="bullet">
///   <item>Drops the MAX/MIN aggregation.</item>
///   <item>Adds the metric column to SELECT (so users see the value driving the ranking).</item>
///   <item>Adds an ORDER BY on the metric column (DESC for top/worst-by-volume; ASC for least/bottom).</item>
/// </list>
///
/// <para>Catches the common silent-failure: "top 10 most recent bills" → spec emits
/// <c>SELECT TOP 10 MAX(BaseAmount)</c> instead of TOP 10 rows ordered by IssuedAt DESC.</para>
///
/// <para>Direction inference: "top / most / highest / largest / longest / worst-by-volume" → DESC.
/// "bottom / least / lowest / smallest / shortest" → ASC. The metric column comes from the
/// existing MAX/MIN's column reference (the LLM at least picked the right column when it chose
/// the aggregation).</para>
/// </summary>
internal sealed class ForceTopNRowsOverMaxMinPhase : ISpecRepairPhase
{
    public string Name => "ForceTopNRowsOverMaxMin";
    public string Covers => "TOPN intent + MAX/MIN aggregation + Limit set → swap to TOP-N rows ORDER BY metric";

    private static readonly Regex AscCue = new(
        @"\b(?:bottom|least|lowest|smallest|shortest|fewest|min|minimum)\s+\d+\b|\b\d+\s+(?:bottom|least|lowest|smallest|shortest|fewest)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescCue = new(
        @"\b(?:top|highest|largest|longest|worst|most(?:\s+\w+){0,2}|biggest|max|maximum|newest|latest|recent)\s+\d+\b|\b\d+\s+(?:top|highest|largest|longest|worst|biggest|newest|latest|most\s+\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Recency keywords that imply "rank by date" rather than by a numeric metric. Used
    /// to override the LLM's metric-column choice (which is often wrong) with the entity's date
    /// column. DESC variant: most recent, newest, latest.</summary>
    private static readonly Regex RecencyCueDesc = new(
        @"\b(?:most\s+recent|newest|latest|recent(?:ly)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ASC variant: oldest, earliest, first.</summary>
    private static readonly Regex RecencyCueAsc = new(
        @"\b(?:oldest|earliest|first(?:\s+\d+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        if (spec.Aggregations.Count != 1) return;

        var agg = spec.Aggregations[0];
        var fn = (agg.Function ?? "").ToUpperInvariant();
        if (fn != "MAX" && fn != "MIN") return;
        if (string.IsNullOrEmpty(agg.Column) || agg.Column == "*") return;

        // Only fire on TOPN intent — TOP N with a real Limit, OR the question matches one of the
        // direction cues + a number. Both gates together avoid mis-firing on legitimate AGGREGATE
        // queries like "largest single bill amount ever issued" (those are MAX with no Limit).
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        var isAscQuestion = AscCue.IsMatch(q);
        var isDescQuestion = DescCue.IsMatch(q);
        if (!isAscQuestion && !isDescQuestion) return;

        // Must have a Limit set (otherwise this isn't a TOPN — it's a pure aggregate).
        if (!(spec.Limit is > 0)) return;

        var metricCol = agg.Column;
        var direction = isAscQuestion ? "asc" : "desc";

        // Recency override: when the question implies "by date" — "most recent", "newest",
        // "latest", "oldest", "earliest" — the LLM frequently picks the wrong column
        // (e.g. "top 10 most recent bills" → MAX(BaseAmount) instead of MAX(IssuedAt)). Swap
        // the metric to the root entity's default date column.
        // Same heuristic for ASC direction with "oldest" / "earliest".
        if (RecencyCueDesc.IsMatch(q) || RecencyCueAsc.IsMatch(q))
        {
            var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
            if (!string.IsNullOrEmpty(dateCol) && ctx.Catalog.ColumnExists(spec.Root, dateCol!))
            {
                metricCol = $"{spec.Root}.{dateCol}";
                direction = RecencyCueAsc.IsMatch(q) ? "asc" : "desc";
                ctx.Diagnostics.Add(new(Name, $"recency cue detected → routing TOP {spec.Limit} to date column {metricCol} {direction.ToUpperInvariant()}"));
            }
        }

        // Drop the aggregation — we want rows, not a single value.
        spec.Aggregations.RemoveAt(0);

        // Add metric column to SELECT if not already there (so user sees the ranking value).
        var bare = metricCol.Trim('[', ']');
        if (!spec.Select.Any(s => string.Equals(s.Trim('[', ']'), bare, System.StringComparison.OrdinalIgnoreCase)))
            spec.Select.Add(metricCol);

        // Replace OrderBy (LLM's OrderBy was probably on the aggregate alias which no longer
        // exists). Cleaner to set our own.
        spec.OrderBy.Clear();
        spec.OrderBy.Add(new OrderBySpec { Column = metricCol, Direction = direction });

        // Drop GROUP BY entries on non-bucket columns — we're projecting rows, not groups.
        spec.GroupBy.RemoveAll(g =>
            !g.Contains("DATEADD", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("DATEPART", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("YEAR(", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("MONTH(", System.StringComparison.OrdinalIgnoreCase)
            && !g.Contains("CAST(", System.StringComparison.OrdinalIgnoreCase));

        ctx.Diagnostics.Add(new(Name, $"replaced {fn}({metricCol}) with TOP {spec.Limit} … ORDER BY {metricCol} {direction.ToUpperInvariant()}"));
    }
}
