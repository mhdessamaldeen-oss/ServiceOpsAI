namespace SuperAdminCopilot.Grounding;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Pipeline.Prompts;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

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
    private readonly ILogger<QuestionGrounder> _logger;

    public QuestionGrounder(
        IPromptShapeClassifier shapeClassifier,
        IValueLinker valueLinker,
        ISemanticLayer semanticLayer,
        IEntityCatalog catalog,
        ILogger<QuestionGrounder> logger)
    {
        _shapeClassifier = shapeClassifier;
        _valueLinker = valueLinker;
        _semanticLayer = semanticLayer;
        _catalog = catalog;
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

        // 4. Temporal slots.
        var temporal = LinkTemporal(q);

        // 5. Intent flags.
        var isAllTime = AllTimeCueEn.IsMatch(q) || AllTimeCueAr.IsMatch(q);
        var isDistinct = DistinctCueEn.IsMatch(q) || DistinctCueAr.IsMatch(q);

        // 6. Date-role hint from lifecycle verb.
        var dateRole = "";
        foreach (var (rx, role) in VerbToRole)
            if (rx.IsMatch(q)) { dateRole = role; break; }

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
        };
    }

    // ── Derived-metric resolution ──────────────────────────────────────────
    // For each linked table, when the question mentions a known derived metric ("age", "duration",
    // "resolution time", "MTTR", "revenue", "consumption"), emit a hint pointing to the canonical
    // SQL expression. The LLM uses this hint as the AGGREGATION TARGET — preventing the wrong-
    // column failure mode where the 7B picks AffectedUsersCount for "ticket age" etc.

    private static readonly (Regex Pattern, string PreferredFn)[] MetricCues = new[]
    {
        (new Regex(@"\b(?:resolution\s+time|time\s+to\s+resolve|time\s+to\s+resolution|mttr)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AVG"),
        (new Regex(@"\b(?:ticket\s+age|age\s+of\s+ticket|how\s+old)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AVG"),
        (new Regex(@"\b(?:outage\s+duration|duration\s+of\s+outage|outage\s+length|mttr)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AVG"),
        (new Regex(@"\bage\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AVG"),
        (new Regex(@"\b(?:revenue|sales|billing\s+total|amount\s+billed|billed\s+amount|total\s+billed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "SUM"),
        (new Regex(@"\b(?:consumption|usage|kwh|kilowatt[-\s]?hours?|cubic\s+meters?|m3)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "SUM"),
    };

    private List<DerivedMetricHint> LinkDerivedMetrics(string question, IReadOnlyList<InferredTable> linkedTables)
    {
        var hints = new List<DerivedMetricHint>();
        var tableSet = new HashSet<string>(linkedTables.Select(t => t.Name), System.StringComparer.OrdinalIgnoreCase);

        // Ticket age: Tickets.CreatedAt vs now
        if (tableSet.Contains("Tickets") && System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:ticket\s+age|age\s+of\s+(?:the\s+)?ticket|how\s+old\s+(?:is|are)\s+(?:the\s+)?ticket)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            hints.Add(new DerivedMetricHint("ticket-age", "DATEDIFF(DAY, Tickets.CreatedAt, GETDATE())", "AVG"));
        }
        // Ticket resolution time
        if (tableSet.Contains("Tickets") && System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:resolution\s+time|time\s+to\s+resolve|time\s+to\s+resolution|mttr)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var unit = System.Text.RegularExpressions.Regex.IsMatch(question, @"\bhours?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "HOUR" : "DAY";
            hints.Add(new DerivedMetricHint("resolution-time", $"DATEDIFF({unit}, Tickets.CreatedAt, Tickets.ResolvedAt)", "AVG"));
        }
        // Outage duration
        if (tableSet.Contains("Outages") && System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:outage\s+duration|duration|outage\s+length|mttr|hours?\s+down)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var unit = System.Text.RegularExpressions.Regex.IsMatch(question, @"\bminutes?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "MINUTE"
                     : System.Text.RegularExpressions.Regex.IsMatch(question, @"\bdays?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "DAY"
                     : "HOUR";
            hints.Add(new DerivedMetricHint("outage-duration", $"DATEDIFF({unit}, Outages.StartedAt, Outages.EndedAt)", "AVG"));
        }
        // Revenue / billed amount → Bills.TotalAmount, SUM
        if (tableSet.Contains("Bills") && System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:revenue|sales|total\s+billed|billed\s+amount|amount\s+billed|total\s+bill|billing\s+total)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            hints.Add(new DerivedMetricHint("revenue", "Bills.TotalAmount", "SUM"));
        }
        // Consumption / usage → MeterReadings.Value or Bills.UsageAmount
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:consumption|usage|kwh|kilowatt[-\s]?hours?|cubic\s+meters?|m3)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (tableSet.Contains("MeterReadings"))
                hints.Add(new DerivedMetricHint("consumption", "MeterReadings.Value", "SUM"));
            else if (tableSet.Contains("Bills"))
                hints.Add(new DerivedMetricHint("consumption", "Bills.UsageAmount", "SUM"));
        }

        return hints;
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
            if (!_catalog.TableExists(entity.Table)) continue;
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

    // ── Temporal slot extraction ────────────────────────────────────────────
    // Mirrors the patterns in InjectTemporalFilterFromQuestionPhase. Keeping them duplicated
    // (rather than sharing a static helper) preserves the SpecRepair phase as a self-contained
    // backstop — if the grounder is disabled or fails, the phase still fires.

    private sealed record TemporalPattern(Regex Match, string Token, string? RangeEnd = null, string Op = "gte");

    private static readonly TemporalPattern[] TemporalPatterns = new[]
    {
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+hours?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@hours:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+days?\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled), "@days:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+weeks?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@weeks:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+months?\b",RegexOptions.IgnoreCase | RegexOptions.Compiled), "@months:-{0}"),
        new TemporalPattern(new Regex(@"\b(?:in\s+the\s+|over\s+the\s+)?(?:last|past)\s+(\d+)\s+years?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@years:-{0}"),
        new TemporalPattern(new Regex(@"\bthis\s+week\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "@week_start",       "@weeks:1"),
        new TemporalPattern(new Regex(@"\blast\s+week\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_week_start",  "@week_start"),
        new TemporalPattern(new Regex(@"\bthis\s+month\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled), "@month_start",      "@months:1"),
        new TemporalPattern(new Regex(@"\blast\s+month\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_month_start", "@month_start"),
        new TemporalPattern(new Regex(@"\bthis\s+quarter\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled), "@quarter_start",    "@quarters:1"),
        new TemporalPattern(new Regex(@"\blast\s+quarter\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_quarter_start","@quarter_start"),
        new TemporalPattern(new Regex(@"\bthis\s+year\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "@year_start",       "@years:1"),
        new TemporalPattern(new Regex(@"\blast\s+year\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "@last_year_start",  "@year_start"),
        new TemporalPattern(new Regex(@"\btoday\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled), "@today",            "@tomorrow"),
        new TemporalPattern(new Regex(@"\byesterday\b",        RegexOptions.IgnoreCase | RegexOptions.Compiled), "@yesterday",        "@today"),
        new TemporalPattern(new Regex(@"\b(?:q1|first\s+quarter)\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q1_start", "@q1_end"),
        new TemporalPattern(new Regex(@"\b(?:q2|second\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q2_start", "@q2_end"),
        new TemporalPattern(new Regex(@"\b(?:q3|third\s+quarter)\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q3_start", "@q3_end"),
        new TemporalPattern(new Regex(@"\b(?:q4|fourth\s+quarter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "@q4_start", "@q4_end"),
    };

    private static List<TemporalBinding> LinkTemporal(string question)
    {
        var results = new List<TemporalBinding>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var p in TemporalPatterns)
        {
            var m = p.Match.Match(question);
            if (!m.Success) continue;
            var startToken = p.Token;
            if (m.Groups.Count > 1 && m.Groups[1].Success)
                startToken = string.Format(System.Globalization.CultureInfo.InvariantCulture, startToken, m.Groups[1].Value);
            var key = startToken + "|" + (p.RangeEnd ?? "");
            if (!seen.Add(key)) continue;
            results.Add(new TemporalBinding(m.Value.Trim(), startToken, p.RangeEnd, p.Op));
        }
        return results;
    }

    // ── Intent cues ─────────────────────────────────────────────────────────

    private static readonly Regex AllTimeCueEn = new(
        @"\b(?:ever|of\s+all\s+time|all\s+time|in\s+history|since\s+the\s+beginning|since\s+inception|at\s+any\s+time)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllTimeCueAr = new(
        @"على\s+الإطلاق|في\s+التاريخ|منذ\s+البداية|في\s+أي\s+وقت",
        RegexOptions.Compiled);

    private static readonly Regex DistinctCueEn = new(
        @"\b(?:how\s+many\s+|number\s+of\s+|count\s+of\s+)(?:distinct|unique|different)\b|\bdistinct\s+(?:customers|users|tickets|bills|outages|regions|services|service\s+types|categories|departments)\b|\bunique\s+(?:customers|users|tickets|bills|outages|regions|services|service\s+types|categories|departments)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DistinctCueAr = new(
        @"كم\s+عدد\s+.*?(?:المختلفين|المختلفة|المتفردين)|عدد\s+.*?(?:مختلف|متفرد)",
        RegexOptions.Compiled);

    // Maps lifecycle verbs to semantic-layer dateRole keys. Same vocabulary as
    // SwapDateColumnByVerbPhase — kept in sync deliberately.
    private static readonly (Regex Pattern, string Role)[] VerbToRole = new[]
    {
        (new Regex(@"\b(?:resolved|fixed|completed|done)\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "resolved"),
        (new Regex(@"\b(?:closed|closure|shut)\b",                RegexOptions.IgnoreCase | RegexOptions.Compiled), "closed"),
        (new Regex(@"\b(?:created|opened|filed|submitted|reported|raised|new(?:ly)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "created"),
        (new Regex(@"\b(?:started|began|launched|initiated|onset)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "started"),
        (new Regex(@"\b(?:ended|finished|stopped|terminated|cleared|restored)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ended"),
        (new Regex(@"\b(?:issued|billed|sent)\b",                 RegexOptions.IgnoreCase | RegexOptions.Compiled), "issued"),
        (new Regex(@"\b(?:paid|settled)\b",                       RegexOptions.IgnoreCase | RegexOptions.Compiled), "paid"),
        (new Regex(@"\b(?:signed\s+up|registered|joined|enrolled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "signup"),
        (new Regex(@"\b(?:uploaded)\b",                           RegexOptions.IgnoreCase | RegexOptions.Compiled), "uploaded"),
    };
}
