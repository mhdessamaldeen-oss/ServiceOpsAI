namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Models;

/// <summary>
/// Post-compile, pre-execute guard. Validates the INTEGRITY OF THE SPEC AND SQL against
/// universal structural invariants — never inspects the question text.
///
/// <para><b>Design philosophy</b>: the LLM is multilingual (Arabic / French / Chinese all
/// produce the same QuerySpec JSON for the same intent). Earlier versions of this guard
/// pattern-matched English verbs ("how many", "show me", "by/per", "vs"), which silently
/// failed on non-English inputs and required maintaining ever-growing keyword lists. The
/// current rules look only at the SPEC SHAPE and SQL STRUCTURE — no language assumptions.</para>
///
/// <para>Returns null when the SQL passes the guard. Returns a non-null
/// <see cref="SqlIntentGuardResult"/> when the SQL is rejected — the orchestrator can either
/// retry the planner with the rejection reason as context or surface a hard-fail message.</para>
/// </summary>
public interface ISqlIntentGuard
{
    SqlIntentGuardResult? Check(string question, QuerySpec spec, string compiledSql);
}

/// <summary>Rejection from the intent guard. <see cref="Reason"/> is the trace-friendly
/// summary; <see cref="RetryHint"/> is the hint passed back to the planner if the
/// orchestrator chooses to retry.</summary>
public sealed record SqlIntentGuardResult(string Reason, string RetryHint);

internal sealed class SqlIntentGuard : ISqlIntentGuard
{
    private readonly ILogger<SqlIntentGuard> _logger;

    public SqlIntentGuard(ILogger<SqlIntentGuard> logger)
    {
        _logger = logger;
    }

    public SqlIntentGuardResult? Check(string question, QuerySpec spec, string compiledSql)
    {
        if (spec is null || string.IsNullOrWhiteSpace(compiledSql)) return null;

        return CheckLimitWithoutOrderBy(spec)
            ?? CheckTopNCollapsedToCount(spec)
            ?? CheckCountShapeOnNonCountQuestion(question, spec)
            ?? CheckDegenerateAggregationProjection(spec)
            ?? CheckAntiJoinFarSideFilter(spec)
            ?? CheckDistinctWithAggregations(spec)
            ?? CheckDegenerateGroupByRootPk(compiledSql);
    }

    // ── R1c. Question-aware shape mismatch: spec is a pure COUNT but the question doesn't
    //         actually ask for a count. Diagnosed from session 3 trace mining — 37 of the 174
    //         "no error" traces had this shape, the single largest silent-failure category.
    //         Example: "5 longest outages" → SELECT TOP (5) COUNT(*) FROM [Outages] (got 1 row,
    //         user wanted 5 rows of outage data).
    //
    //         This rule DOES inspect the question text — small departure from the class-doc
    //         "no question inspection" principle, but justified by the magnitude of the
    //         silent-failure rate. Detection is by the ABSENCE of count-intent markers across
    //         the languages the deployment supports (EN + AR + common synonyms), not by an
    //         enumerated list of "this means count" patterns — so it generalises rather than
    //         pattern-matches.
    private static readonly string[] CountIntentMarkers =
    {
        // English — count / quantity intent
        "how many", "count", "number of", "total", "tally", "sum of",
        // English — single-value computed metrics (legitimately produce one row)
        "percentage", "percent", "%", "rate", "ratio", "proportion", "share",
        "average", "avg", "mean", "median", "max", "maximum", "min", "minimum",
        // Arabic — count / quantity
        "عدد", "كم", "مجموع", "إجمالي", "اجمالي",
        // Arabic — single-value metrics
        "نسبة", "متوسط", "معدل", "أعلى", "أدنى",
    };
    private SqlIntentGuardResult? CheckCountShapeOnNonCountQuestion(string question, QuerySpec spec)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        // Only fire when the spec is a PURE count (no select cols, no group-by, all aggs are COUNT).
        if (spec.GroupBy.Count > 0) return null;
        if (spec.Select.Count > 0) return null;
        if (spec.Aggregations.Count == 0) return null;
        var allAggsAreCount = spec.Aggregations.All(a =>
            string.Equals(a.Function, "COUNT", StringComparison.OrdinalIgnoreCase));
        if (!allAggsAreCount) return null;

        // If the question contains ANY count-intent marker (any language), the COUNT shape is
        // legitimate. Only fire when no marker is present at all.
        var lower = question.ToLowerInvariant();
        foreach (var marker in CountIntentMarkers)
        {
            if (lower.Contains(marker, StringComparison.OrdinalIgnoreCase)) return null;
        }

        _logger.LogWarning("[SqlIntentGuard] pure COUNT spec but question has no count-intent markers — likely silent shape failure. Q='{Q}'",
            question.Length > 80 ? question[..80] + "…" : question);
        return new SqlIntentGuardResult(
            "Spec produces a single COUNT row but the question does not ask for a count, total, or quantity. The query likely returns a number when the user wanted a list of rows.",
            "Drop the COUNT aggregation and select the entity's display columns instead. If a limit is implied by the question (e.g. 'top 5'), add orderBy + limit. If the question asks for ALL items matching a filter, just SELECT the columns with the filter applied.");
    }

    // ── R1b. TOP-N with N > 1 collapsed to a single COUNT ⇒ "5 longest outages" shape failure.
    //         The spec returns 1 row (the count) instead of N rows the user asked for. Pure
    //         spec-shape check, no language assumptions. Diagnosed from session 3 silent
    //         failures: 'SELECT TOP (5) COUNT(*) FROM [Outages]' for "5 longest outages".
    private SqlIntentGuardResult? CheckTopNCollapsedToCount(QuerySpec spec)
    {
        if (!spec.Limit.HasValue || spec.Limit.Value <= 1) return null;
        if (spec.GroupBy.Count > 0) return null;
        if (spec.Select.Count > 0) return null;
        var allAggsAreCount = spec.Aggregations.Count > 0
            && spec.Aggregations.All(a => string.Equals(a.Function, "COUNT", StringComparison.OrdinalIgnoreCase));
        if (!allAggsAreCount) return null;
        _logger.LogWarning("[SqlIntentGuard] limit={Limit} with pure COUNT — N items collapsed to 1 row", spec.Limit);
        return new SqlIntentGuardResult(
            $"Spec requests TOP {spec.Limit} rows but produces a single COUNT — the query returns one number instead of {spec.Limit} items.",
            "Replace the COUNT aggregation with the entity's display columns and keep limit + orderBy so the query returns the top-N rows of data, not a count of all rows.");
    }

    // ── R1. limit > 0 without orderBy ⇒ "top of WHAT?" — sort key is required for TOP-N. ────
    //         Allowed when the result is a single row (pure-count, no group-by) — TOP 1 there is harmless.
    private SqlIntentGuardResult? CheckLimitWithoutOrderBy(QuerySpec spec)
    {
        if (!spec.Limit.HasValue || spec.Limit.Value <= 0) return null;
        if (spec.OrderBy.Count > 0) return null;
        var hasRealAgg = spec.Aggregations.Any(a => !string.IsNullOrEmpty(a.Function));
        if (hasRealAgg && spec.GroupBy.Count == 0) return null; // pure-count + limit is fine
        _logger.LogWarning("[SqlIntentGuard] limit={Limit} without orderBy", spec.Limit);
        return new SqlIntentGuardResult(
            $"Spec has limit={spec.Limit} but no orderBy — TOP-N requires a sort key, otherwise the rows returned are arbitrary.",
            "Add an orderBy on the dimension that defines 'top' (e.g., the count alias DESC, or a date DESC for 'newest').");
    }

    // ── R2. Real aggregations + non-aggregate SELECT + no GROUP BY ⇒ degenerate. ────────────
    //         Either the SELECT cols should move into groupBy, or they should be dropped.
    //         SpecEnricher.R5 auto-promotes SELECT → groupBy, so reaching this state is rare.
    private SqlIntentGuardResult? CheckDegenerateAggregationProjection(QuerySpec spec)
    {
        var hasRealAgg = spec.Aggregations.Any(a => !string.IsNullOrEmpty(a.Function));
        if (!hasRealAgg) return null;
        if (spec.GroupBy.Count > 0) return null;
        if (spec.Select.Count == 0) return null;
        _logger.LogWarning("[SqlIntentGuard] aggregations + non-agg SELECT but no GROUP BY");
        return new SqlIntentGuardResult(
            "Spec has aggregations and SELECT columns but no GROUP BY — SQL Server will reject the SELECT columns.",
            "Either drop the non-aggregate SELECT columns, or add them to groupBy so the result has one row per group.");
    }

    // ── R3. Anti-join on table T + any filter on a column qualified by T ⇒ contradiction. ───
    //         Anti-join means "no row from T matches". Filtering T is incoherent.
    private SqlIntentGuardResult? CheckAntiJoinFarSideFilter(QuerySpec spec)
    {
        if (spec.Joins.Count == 0 || spec.Filters.Count == 0) return null;
        var antiTables = spec.Joins
            .Where(j => string.Equals(j.Kind, "anti", StringComparison.OrdinalIgnoreCase))
            .Select(j => j.Table)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (antiTables.Count == 0) return null;
        foreach (var f in spec.Filters)
        {
            var col = (f.Column ?? "").Replace("[", "").Replace("]", "");
            var dot = col.IndexOf('.');
            if (dot <= 0) continue;
            var t = col[..dot];
            if (antiTables.Contains(t))
            {
                _logger.LogWarning("[SqlIntentGuard] anti-join on {T} + filter on {Col}", t, col);
                return new SqlIntentGuardResult(
                    $"Spec has anti-join on '{t}' but also a filter on '{col}' — anti-join means 'no row exists', so filtering that table is contradictory.",
                    $"Drop the filter on '{col}'. The anti-join already expresses 'no matching row'.");
            }
        }
        return null;
    }

    // ── R4. distinct:true with real aggregations ⇒ contradictory. ────────────────────────────
    //         The compiler's normalize step now resolves this by clearing groupBy when distinct
    //         is set without aggs. With aggregations, distinct on top is degenerate (the result
    //         is already one row per group); rejecting here surfaces a clearer error than the
    //         silent shadow.
    private SqlIntentGuardResult? CheckDistinctWithAggregations(QuerySpec spec)
    {
        if (!spec.Distinct) return null;
        var hasRealAgg = spec.Aggregations.Any(a => !string.IsNullOrEmpty(a.Function));
        if (!hasRealAgg) return null;
        _logger.LogWarning("[SqlIntentGuard] distinct:true combined with aggregations");
        return new SqlIntentGuardResult(
            "Spec has distinct:true and aggregations together — DISTINCT on grouped results is degenerate.",
            "Use either distinct (for unique projection) OR aggregations (for grouped totals), not both.");
    }

    // ── R5. SQL groups by <table>.[Id] with COUNT(*) ⇒ degenerate row list pretending to be ─
    //         a count (each row has COUNT=1). Pure SQL structural check, no question lookup.
    private static readonly Regex GroupByRootPk = new(
        @"GROUP\s+BY[^;]*\[(?<table>\w+)\]\.\[Id\][^a-zA-Z0-9_]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CountStar = new(
        @"COUNT\s*\(\s*\*\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private SqlIntentGuardResult? CheckDegenerateGroupByRootPk(string sql)
    {
        if (!CountStar.IsMatch(sql)) return null;
        var m = GroupByRootPk.Match(sql);
        if (!m.Success) return null;
        var table = m.Groups["table"].Value;
        _logger.LogWarning("[SqlIntentGuard] degenerate GROUP BY {Table}.Id with COUNT(*)", table);
        return new SqlIntentGuardResult(
            $"SQL groups by [{table}].[Id] with COUNT(*) — produces a row list with COUNT=1 per row instead of a real count.",
            $"Drop the GROUP BY {table}.Id and the COUNT(*); return either a row list (SELECT real columns) OR a scalar count (SELECT COUNT(*) with no GROUP BY).");
    }
}
