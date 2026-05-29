namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Aggregation;

using SuperAdminCopilot.Models;

/// <summary>
/// When the QuestionGrounder produced one or more DerivedMetricHints (ticket-age,
/// resolution-time, outage-duration, revenue, consumption) and the LLM emitted an aggregation
/// over a different column, OVERRIDE the aggregation column to match the hint. Catches the
/// wrong-column failure mode where the 7B model picks AffectedUsersCount or RelatedBillId
/// when asked about "age" / "resolution time" / "revenue".
///
/// <para>The grounder also passes the hints to the prompt — but the LLM doesn't always follow
/// hints under pressure. This phase is the deterministic backstop.</para>
///
/// <para>Conservative gates:
/// <list type="bullet">
///   <item>Fires only when DerivedMetricHints is non-empty (grounder matched a metric)</item>
///   <item>Skips if the existing aggregation already uses the hint expression (no-op)</item>
///   <item>If hints carry a preferred function (e.g. AVG for time, SUM for revenue), use it</item>
///   <item>Preserves the alias the LLM set, so result rendering keeps user-friendly headings</item>
/// </list></para>
///
/// <para>Note: the grounder lives upstream of SpecRepair; we read its hints by stashing them in
/// SpecRepairContext.GroundingHints (or, when that channel isn't wired, by re-detecting via the
/// question text — same regex set as the grounder).</para>
/// </summary>
internal sealed class ApplyDerivedMetricHintPhase : ISpecRepairPhase
{
    public string Name => "ApplyDerivedMetricHint";
    public string Covers => "Override LLM's wrong aggregation column with the derived-metric hint (age / resolution-time / revenue / consumption / duration)";

    // Tier window override: weak-model crutch.
    // Derived-metric column override (age, MTTR, revenue …). Strong NLU picks the right aggregation column without hint.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count != 1) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        if (string.IsNullOrEmpty(spec.Root)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Re-detect the same metric cues as the grounder (avoid plumbing GroundingContext into
        // every phase). Keep these in sync with QuestionGrounder.LinkDerivedMetrics.
        string? expr = null;
        string? preferredFn = null;
        string? metricKey = null;

        if (string.Equals(spec.Root, "Tickets", System.StringComparison.OrdinalIgnoreCase))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:resolution\s+time|time\s+to\s+resolve|time\s+to\s+resolution|mttr)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var unit = System.Text.RegularExpressions.Regex.IsMatch(q, @"\bhours?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "HOUR" : "DAY";
                expr = $"DATEDIFF({unit}, Tickets.CreatedAt, Tickets.ResolvedAt)";
                preferredFn = "AVG";
                metricKey = "resolution-time";
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:ticket\s+age|age\s+of\s+(?:the\s+)?ticket|how\s+old)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                expr = "DATEDIFF(DAY, Tickets.CreatedAt, GETDATE())";
                preferredFn = "AVG";
                metricKey = "ticket-age";
            }
        }
        else if (string.Equals(spec.Root, "Outages", System.StringComparison.OrdinalIgnoreCase))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:outage\s+duration|duration|outage\s+length|mttr|hours?\s+down)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var unit = System.Text.RegularExpressions.Regex.IsMatch(q, @"\bminutes?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "MINUTE"
                         : System.Text.RegularExpressions.Regex.IsMatch(q, @"\bdays?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "DAY"
                         : "HOUR";
                expr = $"DATEDIFF({unit}, Outages.StartedAt, Outages.EndedAt)";
                preferredFn = "AVG";
                metricKey = "outage-duration";
            }
        }
        else if (string.Equals(spec.Root, "Bills", System.StringComparison.OrdinalIgnoreCase))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:revenue|sales|total\s+billed|billed\s+amount|amount\s+billed|billing\s+total)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                expr = "Bills.TotalAmount";
                preferredFn = "SUM";
                metricKey = "revenue";
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:consumption|usage|kwh|kilowatt[-\s]?hours?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                expr = "Bills.UsageAmount";
                preferredFn = "SUM";
                metricKey = "consumption";
            }
        }
        else if (string.Equals(spec.Root, "MeterReadings", System.StringComparison.OrdinalIgnoreCase))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(?:consumption|usage|kwh|kilowatt[-\s]?hours?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                expr = "MeterReadings.Value";
                preferredFn = "SUM";
                metricKey = "consumption";
            }
        }

        if (expr is null) return;

        var agg = spec.Aggregations[0];
        var currentCol = (agg.Column ?? "").Trim('[', ']');
        // Already correct? Skip.
        if (string.Equals(currentCol, expr.Trim('[', ']'), System.StringComparison.OrdinalIgnoreCase)) return;

        // Override.
        var oldCol = agg.Column;
        var oldFn = agg.Function;
        agg.Column = expr;
        if (!string.IsNullOrEmpty(preferredFn)
            && !string.Equals(agg.Function, preferredFn, System.StringComparison.OrdinalIgnoreCase))
        {
            // Only override function if the LLM picked a non-aggregable function for the metric
            // (e.g. COUNT(BaseAmount) for revenue — should be SUM). Don't override AVG → SUM.
            var llmFn = (agg.Function ?? "").ToUpperInvariant();
            if (llmFn == "COUNT" || string.IsNullOrEmpty(llmFn))
                agg.Function = preferredFn;
        }
        // Always normalize alias — hyphens break T-SQL identifiers, even when wrapped in [].
        // The LLM occasionally copies our metric-key alias verbatim ("resolution-time") which
        // breaks the AST validator. Force underscores.
        if (string.IsNullOrEmpty(agg.Alias))
            agg.Alias = metricKey?.Replace("-", "_") ?? "metric";
        else if (agg.Alias.Contains('-'))
            agg.Alias = agg.Alias.Replace('-', '_');

        // Inject NOT NULL guards on the date column the DATEDIFF references — otherwise NULL
        // (unresolved tickets, ongoing outages) poisons AVG with NULL or counts them as 0.
        if (metricKey == "resolution-time"
            && !spec.Filters.Any(f => string.Equals(f.Column, "Tickets.ResolvedAt", System.StringComparison.OrdinalIgnoreCase)))
        {
            spec.Filters.Add(new FilterSpec { Column = "Tickets.ResolvedAt", Op = "notnull", Value = null });
            ctx.Diagnostics.Add(new(Name, "injected Tickets.ResolvedAt IS NOT NULL guard for resolution-time aggregation"));
        }
        else if (metricKey == "outage-duration"
            && !spec.Filters.Any(f => string.Equals(f.Column, "Outages.EndedAt", System.StringComparison.OrdinalIgnoreCase)))
        {
            spec.Filters.Add(new FilterSpec { Column = "Outages.EndedAt", Op = "notnull", Value = null });
            ctx.Diagnostics.Add(new(Name, "injected Outages.EndedAt IS NOT NULL guard for outage-duration aggregation"));
        }

        ctx.Diagnostics.Add(new(Name, $"overrode {oldFn}({oldCol}) → {agg.Function}({expr}) for derived metric '{metricKey}'"));
    }
}
