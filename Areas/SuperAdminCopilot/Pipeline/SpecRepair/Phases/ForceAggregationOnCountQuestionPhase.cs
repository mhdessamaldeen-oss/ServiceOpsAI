namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>Question asks for aggregation ("how many"/"total"/"max"/etc) but spec lacks one → inject COUNT/SUM/AVG/MAX/MIN.</summary>
internal sealed class ForceAggregationOnCountQuestionPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public ForceAggregationOnCountQuestionPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "ForceAggregationOnCountQuestion";
    public string Covers => "Paraphrased aggregation intents that produce list specs";

    // Tier window override: weak-model crutch.
    // Aggregate-verb vocabulary now lives in linguistic-cues.json (locales.{en,ar}.aggregateVerbs)
    // — shared with ForceNonCountAggregationPhase. Strong NLU infers aggregation natively.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Aggregations.Count > 0) return;
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        var q = ctx.Question ?? string.Empty;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);
        var qLower = " " + q.ToLowerInvariant() + " ";

        var fn = ClassifyIntentFromConfig(qLower, _cues);
        if (string.IsNullOrEmpty(fn)) return;

        var (resolvedFn, column, alias) = BuildAggregation(fn, spec.Root, ctx);
        if (resolvedFn is null) return;

        spec.Aggregations.Add(new AggregateSpec { Function = resolvedFn, Column = column, Alias = alias });
        // When forcing an aggregation on a scalar shape (no groupBy), drop list-style select items.
        if (spec.GroupBy.Count == 0 && spec.Select.Count > 0) spec.Select.Clear();
        ctx.Diagnostics.Add(new(Name, $"forced {resolvedFn}({column}) for {fn} intent"));
    }

    /// <summary>
    /// Walk the aggregateVerbs vocabulary from linguistic-cues.json. Returns the SQL function
    /// (COUNT / SUM / AVG / MAX / MIN) of the first matching verb, or null.
    /// </summary>
    private static string? ClassifyIntentFromConfig(string qLower, ILinguisticCuesProvider cues)
    {
        foreach (var (_, localeCues) in cues.Compiled.Locales)
        {
            foreach (var entry in localeCues.AggregateVerbs)
            {
                if (string.IsNullOrEmpty(entry.Verb) || string.IsNullOrEmpty(entry.Function)) continue;
                if (qLower.Contains(entry.Verb)) return entry.Function;
            }
        }
        return null;
    }

    private static (string? Fn, string Column, string Alias) BuildAggregation(string fn, string rootTable, SpecRepairContext ctx) => fn switch
    {
        "COUNT" => ("COUNT", "*", "Count"),
        "SUM"   => PickNumericColumn(rootTable, ctx) is { } cs ? ("SUM", $"{rootTable}.{cs}", "Total") : ("COUNT", "*", "Count"),
        "AVG"   => PickNumericColumn(rootTable, ctx) is { } ca ? ("AVG", $"{rootTable}.{ca}", "Average") : ("COUNT", "*", "Count"),
        "MAX"   => PickNumericOrDateColumn(rootTable, ctx) is { } cx ? ("MAX", $"{rootTable}.{cx}", "Maximum") : ("COUNT", "*", "Count"),
        "MIN"   => PickNumericOrDateColumn(rootTable, ctx) is { } cm ? ("MIN", $"{rootTable}.{cm}", "Minimum") : ("COUNT", "*", "Count"),
        _ => (null, "", ""),
    };

    // Numeric-column picker. The "looks like a metric column?" heuristic now lives in
    // semantic-layer.json's defaults block (numericColumnHints) so operators can teach the
    // pipeline about new financial / measurement column conventions without recompile.
    private static string? PickNumericColumn(string table, SpecRepairContext ctx)
    {
        var hints = ctx.SemanticLayer.Config.Defaults?.NumericColumnHints ?? new();
        var cols = ctx.Catalog.GetColumns(table);
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType) && IsLikelyMetricName(c.ColumnName, hints)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType)) return c.ColumnName; }
        return null;
    }

    private static string? PickNumericOrDateColumn(string table, SpecRepairContext ctx)
    {
        var hints = ctx.SemanticLayer.Config.Defaults?.NumericColumnHints ?? new();
        var cols = ctx.Catalog.GetColumns(table);
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType) && IsLikelyMetricName(c.ColumnName, hints)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsNumeric(c.DataType)) return c.ColumnName; }
        foreach (var c in cols) { if (!IsIdLike(c.ColumnName) && IsDateLike(c.DataType)) return c.ColumnName; }
        return null;
    }

    private static bool IsIdLike(string name) =>
        string.Equals(name, "Id", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyMetricName(string name, System.Collections.Generic.List<string> hints)
    {
        if (string.IsNullOrEmpty(name) || hints is null || hints.Count == 0) return false;
        var lower = name.ToLowerInvariant();
        foreach (var h in hints)
        {
            if (string.IsNullOrEmpty(h)) continue;
            if (lower.Contains(h.ToLowerInvariant())) return true;
        }
        return false;
    }

    private static bool IsNumeric(string type) =>
        type.StartsWith("int", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("bigint", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("decimal", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("numeric", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("money", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("float", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("real", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("smallint", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("tinyint", System.StringComparison.OrdinalIgnoreCase);

    private static bool IsDateLike(string type) =>
        type.StartsWith("date", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("datetime", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("smalldatetime", System.StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("time", System.StringComparison.OrdinalIgnoreCase);
}
