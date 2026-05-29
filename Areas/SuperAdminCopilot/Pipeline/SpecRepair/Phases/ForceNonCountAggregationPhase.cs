namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Phase 07.γ — force aggregation when the question carries an aggregate verb (sum / total /
/// average / mean / max / min / highest / lowest) but the planner emitted a raw row-listing
/// SELECT instead of an aggregated one.
///
/// <para>Symptom on local LLM: "total bill amount" returned 1000 rows of individual bills
/// instead of 1 row with <c>SUM(TotalAmount)</c>. The verb is unambiguous; the local model
/// just forgot to wrap the SELECT in an aggregate.</para>
///
/// <para>Strategy:</para>
/// <list type="number">
///   <item>Scan the question for an aggregate verb mapped to a SQL function.</item>
///   <item>If spec already has Aggregations, leave it alone (LLM did the right thing).</item>
///   <item>Pick the numeric SELECT column (or a default like TotalAmount/Amount on the root).</item>
///   <item>Add an <see cref="AggregateSpec"/> for that column with the matched function.</item>
///   <item>Clear the raw row-listing SELECT (the aggregated result is the answer now).</item>
/// </list>
///
/// <para>This is a deterministic safety net — universal, no hardcoded entity names. Pulls
/// the target column from the entity's semantic-layer hints when available.</para>
/// </summary>
internal sealed class ForceNonCountAggregationPhase : ISpecRepairPhase
{
    private readonly SuperAdminCopilot.Configuration.ILinguisticCuesProvider _cues;

    public ForceNonCountAggregationPhase(SuperAdminCopilot.Configuration.ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "ForceNonCountAggregation";
    public string Covers =>
        "Question uses 'total / sum / average / mean / max / min / highest / lowest' but spec " +
        "has no aggregation → coerce to AggregateSpec with the right SQL function.";

    // Weak-model crutch: aggregate-verb vocabulary. Now read from
    // Configuration/linguistic-cues.json (locales.en.aggregateVerbs + locales.ar.aggregateVerbs).
    // Operators add new verbs / dialects without recompile.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = StripAnnotations(ctx.Question);
        var qLower = " " + q.ToLowerInvariant() + " ";

        // ORDER-BY intent guard: "newest X first" is a LIST query, not an aggregation.
        // Markers loaded from linguistic-cues.json (locales.{en,ar}.orderingIntent) — operators
        // add new languages or paraphrases there without recompile.
        bool isOrderingIntent = false;
        foreach (var (_, localeCues) in _cues.Compiled.Locales)
        {
            foreach (var marker in localeCues.OrderingIntent)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if (qLower.Contains(marker)) { isOrderingIntent = true; break; }
            }
            if (isOrderingIntent) break;
        }

        // Walk both locales' aggregate-verb vocabularies (en + ar). Each entry carries the
        // ambiguousWithOrderBy flag from JSON so "newest tickets first" stays a list.
        string? function = null;
        foreach (var (_, localeCues) in _cues.Compiled.Locales)
        {
            foreach (var entry in localeCues.AggregateVerbs)
            {
                if (string.IsNullOrEmpty(entry.Verb) || string.IsNullOrEmpty(entry.Function)) continue;
                if (!qLower.Contains(entry.Verb)) continue;
                if (entry.AmbiguousWithOrderBy && isOrderingIntent) continue;
                function = entry.Function;
                break;
            }
            if (function is not null) break;
        }
        if (function is null) return;

        // Already aggregated? Two sub-cases:
        //   • Some entry already uses the right function → done.
        //   • LLM emitted a placeholder COUNT(*) but verb is SUM/AVG/MAX/MIN → upgrade
        //     it in place. This is the H-AGG-SUM regression where the previous early-return
        //     left the wrong aggregation alone, returning 1000 raw rows.
        if (spec.Aggregations is { Count: > 0 })
        {
            foreach (var a in spec.Aggregations)
            {
                if (a is null) continue;
                if (string.Equals(a.Function, function, System.StringComparison.OrdinalIgnoreCase))
                    return;
            }
            var firstAgg = spec.Aggregations[0];
            if (firstAgg is not null
                && string.Equals(firstAgg.Function, SpecConst.Aggregates.Count, System.StringComparison.OrdinalIgnoreCase)
                && function != SpecConst.Aggregates.Count)
            {
                var upgradeCol = ResolveAggregationColumn(spec, ctx, function);
                if (!string.IsNullOrEmpty(upgradeCol))
                {
                    var prevFn = firstAgg.Function;
                    firstAgg.Function = function;
                    firstAgg.Column = upgradeCol.Contains('.') ? upgradeCol : $"{spec.Root}.{upgradeCol}";
                    firstAgg.Alias = function + "Of" + upgradeCol.Replace(".", "_");
                    ctx.Diagnostics.Add(new(typeof(ForceNonCountAggregationPhase).Name,
                        $"upgraded misplaced {prevFn} → {function}({firstAgg.Column})"));
                    spec.Select.Clear();
                    spec.Limit = null;
                }
            }
            return;
        }

        // Resolve which column to aggregate. Priority:
        //   1. A numeric column referenced in spec.Select (LLM half-knew what to do).
        //   2. The entity's default numeric column from the semantic layer (TotalAmount,
        //      Amount, AmountInBase, BaseAmount, UsageAmount, etc.).
        //   3. The entity's date column for MIN/MAX of date intents.
        string? column = ResolveAggregationColumn(spec, ctx, function);
        if (string.IsNullOrEmpty(column)) return;

        var qualified = column.Contains('.') ? column : $"{spec.Root}.{column}";

        spec.Aggregations.Add(new AggregateSpec
        {
            Function = function,
            Column = qualified,
            Alias = function + "Of" + column.Replace(".", "_"),
        });

        // Wipe the raw row-listing SELECT — the aggregated value IS the answer.
        spec.Select.Clear();
        // Also drop any ORDER BY / LIMIT the LLM emitted for the row listing — they no
        // longer make sense over a single aggregated row.
        spec.Limit = null;
        spec.Offset = null;

        ctx.Diagnostics.Add(new(typeof(ForceNonCountAggregationPhase).Name,
            $"forced {function}({qualified}); cleared row-listing SELECT"));
    }

    /// <summary>
    /// Pick the column to aggregate. Universal: examines spec.Select for an existing
    /// numeric column, falls back to known financial-column names found on the root, then
    /// the entity's default date column if function is MIN/MAX.
    /// </summary>
    private static string? ResolveAggregationColumn(QuerySpec spec, SpecRepairContext ctx, string function)
    {
        // Heuristic vocabulary loaded from semantic-layer.json's defaults block. Empty lists
        // = no heuristic; the planner falls through to the date-column path or returns null.
        var numericHints = ctx.SemanticLayer.Config.Defaults?.NumericColumnHints ?? new();
        var numericPreference = ctx.SemanticLayer.Config.Defaults?.NumericColumnPreference ?? new();

        // 1. Numeric column already in spec.Select (LLM half-knew what to aggregate).
        foreach (var s in spec.Select)
        {
            if (string.IsNullOrEmpty(s)) continue;
            var bare = StripQualifier(s);
            if (IsLikelyNumeric(bare, numericHints)) return s;
        }

        // 2. Preference list from semantic-layer.json — first column that exists on the root.
        foreach (var p in numericPreference)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (ctx.Catalog.ColumnExists(spec.Root, p)) return p;
        }

        // 3. MIN/MAX over date: use the entity's default date column.
        if (function == SpecConst.Aggregates.Min || function == SpecConst.Aggregates.Max)
        {
            var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
            if (!string.IsNullOrEmpty(dateCol) && ctx.Catalog.ColumnExists(spec.Root, dateCol))
                return dateCol;
        }

        return null;
    }

    private static bool IsLikelyNumeric(string columnName, System.Collections.Generic.List<string> hints)
    {
        if (string.IsNullOrEmpty(columnName) || hints is null || hints.Count == 0) return false;
        var lower = columnName.ToLowerInvariant();
        foreach (var h in hints)
        {
            if (string.IsNullOrEmpty(h)) continue;
            if (lower.Contains(h.ToLowerInvariant())) return true;
        }
        return false;
    }

    private static string StripQualifier(string column)
    {
        if (string.IsNullOrEmpty(column)) return column;
        var dot = column.LastIndexOf('.');
        return dot >= 0 && dot < column.Length - 1 ? column[(dot + 1)..] : column;
    }

    private static string StripAnnotations(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? q.Substring(0, dashIdx) : q;
    }
}
