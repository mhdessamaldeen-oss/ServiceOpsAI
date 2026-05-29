namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Joins;

using SuperAdminCopilot.Models;

/// <summary>
/// Detect and remove tables in <see cref="QuerySpec.Joins"/> (and the implicit-join targets that
/// only appear as column qualifiers in SELECT/FILTER/GROUP-BY/ORDER-BY) that are NOT referenced
/// anywhere in the spec. The compiler walks these to produce JOIN clauses, so an unreferenced
/// table still gets JOINed — fanning out the result set on 1:N relationships and inflating
/// COUNT(*) on aggregate queries.
///
/// <para>Concrete example from session 104: A-COUNT-006 "how many active outages" produced
/// <c>FROM Outages LEFT JOIN Regions INNER JOIN Customers ON Customers.RegionId = Regions.Id</c>.
/// The Customers join is pure noise — the question is about outages, no Customer dimension is
/// selected or filtered. The Customers JOIN multiplies the row count and the COUNT(*) result
/// becomes meaningless.</para>
///
/// <para>Algorithm:
/// <list type="number">
///   <item>Collect every table referenced in: <c>Select</c>, <c>Filters</c>, <c>OrderBy</c>,
///         <c>GroupBy</c>, <c>Having</c>, <c>Computed</c>, <c>Aggregations</c>.</item>
///   <item>Add <c>Root</c> and any anti-join target (those are deliberately empty references).</item>
///   <item>Any explicit JoinSpec NOT in the referenced set → remove.</item>
/// </list></para>
/// </summary>
internal sealed class DetectOverJoinPhase : ISpecRepairPhase
{
    public string Name => "DetectOverJoin";
    public string Covers => "Remove joined tables that aren't referenced in SELECT/FILTER/GROUPBY/HAVING (anti-fanout)";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Joins is null || spec.Joins.Count == 0) return;
        if (string.IsNullOrEmpty(spec.Root)) return;

        var referenced = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { spec.Root };

        foreach (var s in spec.Select.NotNull()) Collect(referenced, s);
        foreach (var f in spec.Filters.NotNull()) Collect(referenced, f.Column);
        foreach (var g in spec.GroupBy.NotNull()) Collect(referenced, g);
        foreach (var o in spec.OrderBy.NotNull()) Collect(referenced, o.Column);
        foreach (var c in spec.Computed.NotNull()) Collect(referenced, c.Expression);
        foreach (var a in spec.Aggregations.NotNull()) Collect(referenced, a.Column);
        foreach (var h in spec.Having.NotNull()) Collect(referenced, h.Column);

        // Anti-join targets are deliberately unreferenced (their "presence" is the constraint).
        foreach (var j in spec.Joins.NotNull())
            if (string.Equals(j.Kind, "anti", System.StringComparison.OrdinalIgnoreCase))
                referenced.Add(j.Table);

        // PeriodComparisons filters also count as references.
        foreach (var p in spec.PeriodComparisons)
            foreach (var f in p.Filters)
                Collect(referenced, f.Column);

        // Remove join entries that no part of the query touches.
        var removed = spec.Joins.RemoveAll(j =>
            !string.IsNullOrEmpty(j.Table) && !referenced.Contains(j.Table));

        if (removed > 0)
            ctx.Diagnostics.Add(new(Name, $"removed {removed} unreferenced join target(s) — anti-fanout"));
    }

    private static void Collect(HashSet<string> set, string? expr)
    {
        if (string.IsNullOrEmpty(expr)) return;
        // Cheap parse: find every "Word." pattern. Includes expressions that have multiple
        // table refs (e.g. "DATEDIFF(day, Tickets.CreatedAt, Tickets.ResolvedAt)").
        var matches = System.Text.RegularExpressions.Regex.Matches(
            expr, @"\[?([A-Za-z_][A-Za-z0-9_]*)\]?\.");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Groups[1].Success) set.Add(m.Groups[1].Value);
        }
    }
}
