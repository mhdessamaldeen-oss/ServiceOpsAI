namespace AnalystAgent.Grounding;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AnalystAgent.Grounding;
using AnalystAgent.Pipeline.Prompts;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// Default <see cref="IQuestionGrounder"/> implementation. Composes:
/// <list type="number">
///   <item>Intent shape from <see cref="IPromptShapeClassifier"/>.</item>
///   <item>Value linking via <see cref="IValueLinker"/> (NER → DB content match).</item>
///   <item>Natural-key resolution against semantic-layer naturalKeyFormat regexes.</item>
///   <item>Temporal slot extraction (this week / Q1 / today / last 30 days / Arabic equivalents).</item>
///   <item>"All-time" and "distinct" intent flags.</item>
///   <item>Lifecycle verb → date-role hint (resolved → ResolvedAt, etc.).</item>
/// </list>
///
/// <para>All component logic is shared with the existing SpecRepair phases — those phases stay in
/// place as a backstop and become no-ops once the LLM uses the grounded context correctly.</para>
/// </summary>
internal sealed class QuestionGrounder : IQuestionGrounder
{
    private readonly IPromptShapeClassifier _shapeClassifier;
    private readonly IValueLinker _valueLinker;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IEntityCatalog _catalog;
    private readonly ILinguisticRegistry _registry;
    private readonly IAnalystSchemaAccessPolicy _accessPolicy;
    private readonly ILogger<QuestionGrounder> _logger;

    public QuestionGrounder(
        IPromptShapeClassifier shapeClassifier,
        IValueLinker valueLinker,
        ISemanticLayer semanticLayer,
        IEntityCatalog catalog,
        ILinguisticRegistry registry,
        IAnalystSchemaAccessPolicy accessPolicy,
        ILogger<QuestionGrounder> logger)
    {
        _shapeClassifier = shapeClassifier;
        _valueLinker = valueLinker;
        _semanticLayer = semanticLayer;
        _catalog = catalog;
        _registry = registry;
        _accessPolicy = accessPolicy;
        _logger = logger;
    }

    public async Task<QuestionGroundingContext> GroundAsync(
        string question,
        IReadOnlyList<InferredTable> linkedTables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return QuestionGroundingContext.Empty;

        // Strip the "-- requested columns:" trailing hint the structural-cue parser injects.
        var q = question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // 1. Intent shape (cheap, synchronous). Convert enum to its name so the grounding
        // context stays a string-only DTO (decouples consumers from the Prompts namespace).
        var shape = _shapeClassifier.Classify(q).ToString();

        // 2. Value linking: scan question against actual lookup values, FK-reachable from
        //    any of the linked tables (1 or 2 hops).
        var values = await _valueLinker.LinkAsync(q, linkedTables, cancellationToken);

        // 3. Natural-key resolution.
        var naturalKeys = LinkNaturalKeys(q);

        // 4. Temporal slots — sourced from linguistic-cues.json via ILinguisticRegistry.
        var temporal = _registry.ExtractTemporal(q)
            .Select(s => new TemporalBinding(s.Label, s.StartToken, s.EndToken, s.Op))
            .ToList();

        // 4b. Time-series bucket ("per month" / "monthly" / "by year") — so the hint can tell the
        //     model to bucket with FORMAT/CAST instead of splitting the date into separate parts.
        var bucket = _registry.ExtractTimeSeriesGranularity(q)?.Bucket ?? "";

        // 5. Intent flags — sourced from linguistic-cues.json via ILinguisticRegistry.
        var isAllTime = _registry.HasCue(q, CueKind.AllTime);
        var isDistinct = _registry.HasCue(q, CueKind.Distinct);

        // 6. Date-role hint from lifecycle verb — sourced from linguistic-cues.json.
        var dateRole = _registry.ExtractLifecycleVerb(q)?.DateRoleHint ?? "";

        // 7. Derived-metric column hints — "age", "resolution time", "MTTR", "revenue", "duration"
        // map to canonical SQL expressions the LLM should aggregate over. Without these the
        // 7B model frequently picks the wrong column (e.g., AffectedUsersCount for "age").
        var metricHints = LinkDerivedMetrics(q, linkedTables);

        _logger.LogInformation(
            "[QuestionGrounder] shape={Shape} values={V} naturalKeys={K} temporal={T} allTime={All} distinct={D} dateRole={R} metricHints={M}",
            shape, values.Count, naturalKeys.Count, temporal.Count, isAllTime, isDistinct, dateRole, metricHints.Count);

        return new QuestionGroundingContext
        {
            Question = q,
            PromptShape = shape,
            LinkedTables = linkedTables.Select(t => t.Name).ToList(),
            LinkedValues = values,
            LinkedTemporal = temporal,
            LinkedNaturalKeys = naturalKeys,
            IsAllTimeIntent = isAllTime,
            IsDistinctCountIntent = isDistinct,
            DateRoleHint = dateRole,
            DerivedMetricHints = metricHints,
            TimeBucketHint = bucket,
        };
    }

    // ── Derived-metric resolution ──────────────────────────────────────────
    // For each linked table, iterate its `derivedMetrics` block from semantic-layer.json.
    // The first cue that matches the question wins for that entity. `{unit}` substitution
    // honours per-metric unitCue regexes; falls back to DefaultUnit. NO table-name conditionals
    // here — adding a new entity / metric is a JSON edit. The DerivedMetricRule does the same
    // resolution server-side via ISemanticView.ResolveDerivedMetric — this grounding path is
    // a hint that teaches the LLM the same answer in the prompt.
    private List<DerivedMetricHint> LinkDerivedMetrics(string question, IReadOnlyList<InferredTable> linkedTables)
    {
        var hints = new List<DerivedMetricHint>();
        if (string.IsNullOrWhiteSpace(question) || linkedTables.Count == 0) return hints;
        var qLower = question.ToLowerInvariant();

        foreach (var t in linkedTables)
        {
            if (!_accessPolicy.IsTableQueryable(t.Name)) continue;   // never derive a metric from a hidden table
            var entity = _semanticLayer.GetEntityForTable(t.Name);
            if (entity?.DerivedMetrics is null || entity.DerivedMetrics.Count == 0) continue;
            foreach (var m in entity.DerivedMetrics)
            {
                if (m.Cues is null || m.Cues.Count == 0) continue;
                string? matchedCue = null;
                foreach (var cue in m.Cues)
                {
                    if (string.IsNullOrEmpty(cue)) continue;
                    if (qLower.Contains(cue.ToLowerInvariant())) { matchedCue = cue; break; }
                }
                if (matchedCue is null) continue;

                var expr = m.Expression ?? "";
                if (expr.Contains("{unit}", System.StringComparison.Ordinal))
                {
                    var unit = ResolveUnitCue(qLower, m) ?? m.DefaultUnit ?? "DAY";
                    expr = expr.Replace("{unit}", unit);
                }
                hints.Add(new DerivedMetricHint(matchedCue, expr, m.Function ?? "AVG"));
                break;  // one metric per entity per question — same as the previous behaviour
            }
        }
        return hints;
    }

    private static string? ResolveUnitCue(string qLower, AnalystAgent.Semantic.DerivedMetricDefinition metric)
    {
        if (metric.UnitCue is null || metric.UnitCue.Count == 0) return null;
        foreach (var (unit, patterns) in metric.UnitCue)
        {
            if (patterns is null) continue;
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(qLower, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return unit;
            }
        }
        return null;
    }

    // ── Natural-key resolution ──────────────────────────────────────────────

    private static readonly Regex NaturalKeyShape = new(
        @"[A-Za-z]{1,6}-\d[\d-]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private List<NaturalKeyBinding> LinkNaturalKeys(string question)
    {
        var results = new List<NaturalKeyBinding>();
        var tokenMatches = NaturalKeyShape.Matches(question);
        if (tokenMatches.Count == 0) return results;

        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(entity.NaturalKeyColumn) || string.IsNullOrEmpty(entity.NaturalKeyFormat)) continue;
            if (!_catalog.TableExists(entity.Table) || !_accessPolicy.IsTableQueryable(entity.Table)) continue;  // never bind a hidden table's natural key
            if (!_catalog.ColumnExists(entity.Table, entity.NaturalKeyColumn!)) continue;

            Regex rx;
            try { rx = new Regex(entity.NaturalKeyFormat!, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { continue; }

            foreach (Match m in tokenMatches)
            {
                if (!rx.IsMatch(m.Value)) continue;
                results.Add(new NaturalKeyBinding(
                    Entity: entity.Name,
                    Table: entity.Table,
                    Column: entity.NaturalKeyColumn!,
                    Value: m.Value));
            }
        }
        return results;
    }

    // Temporal slots, all-time/distinct cues, and lifecycle-verb date-role hints all read
    // through ILinguisticRegistry → linguistic-cues.json. No inline regex vocab lives here.
}
