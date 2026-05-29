namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question is a TIMESERIES shape ("by month" / "monthly" / "weekly" / "trend over time"
/// etc.) and the spec doesn't already include a date-bucketed GROUP BY, inject one.
///
/// <para>Default granularity is detected from keywords (day/week/month/quarter/year). The bucket
/// expression is rendered against the root entity's <c>"default"</c> date role from
/// semantic-layer.json — typically <c>CreatedAt</c> — so a new schema's date column is honored
/// without code change.</para>
///
/// <para>Why this phase exists: without it, the LLM emits <c>COUNT(*)</c> + raw <c>CreatedAt</c>
/// in GROUP BY (so every row becomes its own bucket) or skips bucketing entirely. The user asked
/// for a time series; they get a single number or 5000 buckets.</para>
/// </summary>
internal sealed class InjectTimeSeriesBucketingPhase : ISpecRepairPhase
{
    private enum Granularity { None, Day, Week, Month, Quarter, Year }

    // Order: most specific to least specific.
    private static readonly Regex DailyIntent = new(@"\b(by\s+day|per\s+day|daily|each\s+day|day\s*-?\s*over\s*-?\s*day)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WeeklyIntent = new(@"\b(by\s+week|per\s+week|weekly|each\s+week|week\s*-?\s*over\s*-?\s*week)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MonthlyIntent = new(@"\b(by\s+month|per\s+month|monthly|each\s+month|month\s*-?\s*over\s*-?\s*month)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuarterlyIntent = new(@"\b(by\s+quarter|per\s+quarter|quarterly|each\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearlyIntent = new(@"\b(by\s+year|per\s+year|yearly|annually|each\s+year|year\s*-?\s*over\s*-?\s*year|yoy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "trend" / "over time" without a granularity → default to monthly (most common cadence in ops dashboards).
    private static readonly Regex TrendIntent = new(@"\b(trend|over\s+time|time\s+series|history)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Arabic patterns mirror the English vocabulary used by the shape classifier.
    private static readonly Regex ArMonthly = new(@"شهري|شهريا|كل\s+شهر|بالشهر", RegexOptions.Compiled);
    private static readonly Regex ArWeekly = new(@"اسبوع|أسبوع|اسبوعيا|أسبوعيا|بالأسبوع", RegexOptions.Compiled);
    private static readonly Regex ArDaily = new(@"يومي|يوميا|كل\s+يوم", RegexOptions.Compiled);
    private static readonly Regex ArQuarterly = new(@"ربع\s+سنوي", RegexOptions.Compiled);

    public string Name => "InjectTimeSeriesBucketing";
    public string Covers => "TIMESERIES intent (by month / weekly / trend) without a date bucket → inject FORMAT/DATEADD + GROUP BY";

    // Tier window override: weak-model crutch.
    // TIMESERIES intent (by month / weekly / trend) → bucket + GROUP BY. Strong NLU emits the bucketing expression.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        var granularity = ClassifyGranularity(ctx.Question ?? string.Empty);
        if (granularity == Granularity.None) return;

        // Don't re-inject if there's already a date-bucket-shaped expression in GroupBy.
        foreach (var g in spec.GroupBy)
            if (g.Contains("DATEADD", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("FORMAT(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("DATEPART(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("YEAR(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("MONTH(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("DAY(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("CAST(", System.StringComparison.OrdinalIgnoreCase)
                || g.Contains("CONVERT(", System.StringComparison.OrdinalIgnoreCase))
                return;

        // Find raw date columns in GroupBy — the LLM put a bare datetime column (CreatedAt etc.)
        // which produces one bucket per row. REPLACE those with the proper bucket expression.
        // (Already-bucketed expressions like CAST/DATEADD are caught above and we skipped.)
        var rawDateColsInGroupBy = new List<string>();
        foreach (var g in spec.GroupBy)
        {
            if (g.Contains('(')) continue;                                   // expression — handled above
            var dotIdx = g.IndexOf('.');
            if (dotIdx <= 0) continue;
            var tbl = g.Substring(0, dotIdx).Trim('[', ']');
            var col = g.Substring(dotIdx + 1).Trim('[', ']');
            if (!ctx.Catalog.TableExists(tbl)) continue;
            var match = ctx.Catalog.GetColumns(tbl).FirstOrDefault(c =>
                string.Equals(c.ColumnName, col, System.StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            var t = match.DataType ?? "";
            if (t.StartsWith("date", System.StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("datetime", System.StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("smalldatetime", System.StringComparison.OrdinalIgnoreCase))
                rawDateColsInGroupBy.Add(g);
        }

        // Resolve the date column from semantic layer. The role is selected from a lifecycle verb
        // in the question ("tickets RESOLVED per quarter" → resolved role → ResolvedAt; "outages
        // STARTED per week" → started role → StartedAt). Falls back to the entity's "default"
        // role when no verb is matched. Without this verb-aware selection, the bucket targets
        // the default date column (e.g. CreatedAt) while the LLM may emit projections on the
        // verb-implied column (ResolvedAt) — the resulting SELECT/GROUP-BY mismatch errors out
        // ("Column 'Tickets.ResolvedAt' is invalid in the select list..."). Reproduced on
        // session 109 case B-WIN-run-2.
        var verbRole = DetectVerbRole(ctx.Question ?? string.Empty);
        var dateCol = !string.IsNullOrEmpty(verbRole)
            ? ctx.SemanticLayer.GetDateColumn(spec.Root, verbRole)
            : null;
        if (string.IsNullOrEmpty(dateCol))
            dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        if (string.IsNullOrEmpty(dateCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, dateCol)) return;

        var (expression, alias) = BuildBucketExpression(spec.Root, dateCol, granularity);

        // Strip raw date columns from GroupBy AND from SELECT (those would project per-row dates
        // alongside the bucket, defeating the bucketing). Same for ORDER BY.
        if (rawDateColsInGroupBy.Count > 0)
        {
            foreach (var raw in rawDateColsInGroupBy) spec.GroupBy.Remove(raw);
            spec.Select.RemoveAll(s => rawDateColsInGroupBy.Any(r =>
                string.Equals(r.Trim('[', ']'), s.Trim('[', ']'), System.StringComparison.OrdinalIgnoreCase)));
            spec.OrderBy.RemoveAll(o => rawDateColsInGroupBy.Any(r =>
                string.Equals(r.Trim('[', ']'), (o.Column ?? "").Trim('[', ']'), System.StringComparison.OrdinalIgnoreCase)));
            ctx.Diagnostics.Add(new(Name, $"replaced {rawDateColsInGroupBy.Count} raw date column(s) in GroupBy with bucket expression"));
        }

        // Also strip LLM-emitted date-extraction expressions on the SAME date column from
        // spec.Computed (i.e. SELECT-side ComputedSpecs like "DATEPART(WEEK, SignupAt) AS Week",
        // "YEAR(CreatedAt)", "MONTH(...)", "FORMAT(..., 'yyyy-MM')"). Without this, the LLM-emitted
        // expression projects into SELECT but isn't in GROUP BY (we group by OUR bucket only) —
        // SQL Server rejects with "Column X is invalid in the select list because it is not
        // contained in either an aggregate function or the GROUP BY clause." Our injected bucket
        // expression below replaces them as the canonical bucket projection.
        var dateColMarker = $"[{spec.Root}].[{dateCol}]";
        var dateColMarkerUnbracketed = $"{spec.Root}.{dateCol}";
        var removedAliases = new List<string>();
        var removedComputed = spec.Computed.RemoveAll(c =>
        {
            var ex = c.Expression ?? "";
            if (string.IsNullOrEmpty(ex)) return false;
            // Match expressions that reference the same root.dateCol and use a date-extraction
            // function. We intentionally do NOT remove DATEADD/CAST(AS DATE) here — those are
            // proper bucket expressions and the skip-block above already returned if any are in
            // GROUP BY. If one slipped into Computed only (rare), removing it would lose the bucket.
            var refsCol = ex.Contains(dateColMarker, System.StringComparison.OrdinalIgnoreCase)
                       || ex.Contains(dateColMarkerUnbracketed, System.StringComparison.OrdinalIgnoreCase);
            if (!refsCol) return false;
            var isExtraction = ex.Contains("DATEPART(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("YEAR(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("MONTH(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("DAY(", System.StringComparison.OrdinalIgnoreCase)
                || ex.Contains("FORMAT(", System.StringComparison.OrdinalIgnoreCase);
            if (isExtraction && !string.IsNullOrEmpty(c.Alias)) removedAliases.Add(c.Alias);
            return isExtraction;
        });
        if (removedComputed > 0)
            ctx.Diagnostics.Add(new(Name, $"removed {removedComputed} LLM date-extraction Computed entry(ies) — replaced by injected bucket"));

        // Scrub OrderBy entries that reference (a) the date-extraction expression itself, or
        // (b) the alias of a Computed entry we just removed. Without (b), an "ORDER BY [Week] ASC"
        // would survive even though [Week] no longer exists in SELECT — SQL Server errors with
        // "Invalid column name 'Week'." after our cleanup.
        spec.OrderBy.RemoveAll(o =>
        {
            var col = o.Column ?? "";
            var bare = col.Trim('[', ']');
            if (removedAliases.Any(a => string.Equals(a, bare, System.StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!col.Contains(dateColMarker, System.StringComparison.OrdinalIgnoreCase)
             && !col.Contains(dateColMarkerUnbracketed, System.StringComparison.OrdinalIgnoreCase))
                return false;
            return col.Contains("DATEPART(", System.StringComparison.OrdinalIgnoreCase)
                || col.Contains("YEAR(", System.StringComparison.OrdinalIgnoreCase)
                || col.Contains("MONTH(", System.StringComparison.OrdinalIgnoreCase)
                || col.Contains("DAY(", System.StringComparison.OrdinalIgnoreCase)
                || col.Contains("FORMAT(", System.StringComparison.OrdinalIgnoreCase);
        });

        spec.Computed.Add(new ComputedSpec { Alias = alias, Expression = expression });
        spec.GroupBy.Add(expression);
        if (spec.Aggregations.Count == 0)
            spec.Aggregations.Add(new AggregateSpec { Function = "COUNT", Column = "*", Alias = "Count" });
        if (spec.OrderBy.Count == 0)
            spec.OrderBy.Add(new OrderBySpec { Column = expression, Direction = "asc" });

        ctx.Diagnostics.Add(new(Name, $"injected {granularity} bucket on [{spec.Root}].[{dateCol}]"));
    }

    /// <summary>
    /// Pick a semantic-layer date-role key from a lifecycle verb in the question. Mirrors the
    /// vocabulary in SwapDateColumnByVerbPhase / QuestionGrounder so the bucket target column
    /// matches whatever else uses verb-routing. Returns null when no verb cue matches.
    /// </summary>
    private static string? DetectVerbRole(string question)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:resolved|fixed|completed|done)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "resolved";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:closed|closure|shut)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "closed";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:started|began|launched|initiated|onset)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "started";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:ended|finished|stopped|terminated|cleared|restored)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "ended";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:issued|billed|sent)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "issued";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:paid|settled)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "paid";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:signed\s+up|registered|joined|enrolled)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "signup";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:uploaded)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "uploaded";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:created|opened|filed|submitted|reported|raised|new(?:ly)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "created";
        return null;
    }

    private static Granularity ClassifyGranularity(string question)
    {
        if (DailyIntent.IsMatch(question) || ArDaily.IsMatch(question)) return Granularity.Day;
        if (WeeklyIntent.IsMatch(question) || ArWeekly.IsMatch(question)) return Granularity.Week;
        if (MonthlyIntent.IsMatch(question) || ArMonthly.IsMatch(question)) return Granularity.Month;
        if (QuarterlyIntent.IsMatch(question) || ArQuarterly.IsMatch(question)) return Granularity.Quarter;
        if (YearlyIntent.IsMatch(question)) return Granularity.Year;
        if (TrendIntent.IsMatch(question)) return Granularity.Month;   // default cadence for "trend"
        return Granularity.None;
    }

    // Renders a sortable date expression. Uses DATEADD/DATEDIFF round-tripping which returns
    // a real DATE value — sorts correctly and avoids string-vs-date ordering ambiguity.
    private static (string Expression, string Alias) BuildBucketExpression(string table, string dateCol, Granularity g)
    {
        var col = $"[{table}].[{dateCol}]";
        return g switch
        {
            Granularity.Day => ($"CAST({col} AS DATE)", "Day"),
            Granularity.Week => ($"DATEADD(week, DATEDIFF(week, 0, {col}), 0)", "WeekStart"),
            Granularity.Month => ($"DATEADD(month, DATEDIFF(month, 0, {col}), 0)", "MonthStart"),
            Granularity.Quarter => ($"DATEADD(quarter, DATEDIFF(quarter, 0, {col}), 0)", "QuarterStart"),
            Granularity.Year => ($"YEAR({col})", "Year"),
            _ => (col, "Date"),
        };
    }
}
