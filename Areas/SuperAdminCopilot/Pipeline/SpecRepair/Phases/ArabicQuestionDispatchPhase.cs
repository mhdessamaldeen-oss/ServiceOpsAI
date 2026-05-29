namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Phase 07.δ — early authoritative dispatch for Arabic question forms the local LLM mishandles.
///
/// <para>The 7B model is anglo-biased: Arabic questions with clear count / list intent
/// ("كم عدد X", "اظهر X", "إجمالي X") get routed to <c>Customers</c> regardless of which
/// entity the user named. Downstream InferRootFromQuestionPhase has the right scoring logic
/// but runs AFTER the LLM has populated <c>spec.Select</c> with Customer-flavoured columns —
/// the post-hoc override moves Root but the SELECT contradicts it, producing wrong SQL.</para>
///
/// <para>This phase fires FIRST in the SpecRepair pipeline. It is deterministic, schema-driven
/// (entity match via semantic-layer synonyms — no hardcoded entity names) and handles three
/// Arabic intent shapes:</para>
///
/// <list type="number">
///   <item><c>كم عدد X (الفلتر)</c> → root=X, aggregation=COUNT(*), apply Arabic status filter.</item>
///   <item><c>اظهر/أعطني/عرض X (الفلتر)</c> → root=X, clear Select to let label-enrichment fill it.</item>
///   <item><c>إجمالي/مجموع X</c> → root=X, aggregation=SUM(numericColumn).</item>
/// </list>
///
/// <para>Arabic adjective endings are matched as suffixes / contains since "النشطين" and
/// "النشطون" are inflections of the same canonical "Active". The map covers the common
/// status / severity / payment-state words used in the suite.</para>
/// </summary>
internal sealed partial class ArabicQuestionDispatchPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public ArabicQuestionDispatchPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "ArabicQuestionDispatch";
    public string Covers =>
        "Arabic count / list / aggregate forms — set root authoritatively before LLM-emitted " +
        "Customers-default leaks through, plus apply Arabic status-word filter.";

    // Weak-model crutch: hand-coded Arabic vocabulary (status verbs, intent verbs, regex
    // patterns). A planner with strong multilingual NLU (Qwen2.5-Coder-32B, Claude) handles
    // these natively. Auto-skipped at Medium+.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    // Intent regexes are BUILT FROM linguistic-cues.json at first use:
    //   • count/list/sum verbs come from `locales.ar.intentVerbs.{count,list,sum}`
    //   • status-stop adjectives come from `locales.ar.statusValues[].cue`
    // The noun-capture template + structural particles (في / بدون / مبالغ / قيم) are language
    // structure, not vocabulary — they stay in C#.
    private Regex? _intentGate;
    private Regex? _countForm;
    private Regex? _listForm;
    private Regex? _sumForm;
    private readonly object _formsLock = new();

    private (Regex Gate, Regex Count, Regex List, Regex Sum) GetIntentForms()
    {
        if (_intentGate is not null)
            return (_intentGate, _countForm!, _listForm!, _sumForm!);
        lock (_formsLock)
        {
            if (_intentGate is not null)
                return (_intentGate, _countForm!, _listForm!, _sumForm!);

            if (!_cues.Compiled.Locales.TryGetValue("ar", out var arCues) || arCues is null)
            {
                // No Arabic cues configured — match-nothing regex so all Apply calls no-op.
                var none = new Regex(@"(?!)", RegexOptions.Compiled);
                _intentGate = none; _countForm = none; _listForm = none; _sumForm = none;
                return (none, none, none, none);
            }

            string AltFromRegexes(IReadOnlyList<Regex> rxs, string fallback) =>
                rxs is { Count: > 0 } ? string.Join("|", rxs.Select(r => r.ToString())) : fallback;

            var countSrc = AltFromRegexes(arCues.IntentCount, @"كم\s*عدد");
            var listSrc  = AltFromRegexes(arCues.IntentList,  @"اظهر(?:\s*لي)?|اعرض|عرض|أعطني|اعطني|أرني|قائمة\s+ب?");
            var sumSrc   = AltFromRegexes(arCues.IntentSum,   @"إجمالي|اجمالي|مجموع");

            // Status stops — every cue from locales.ar.statusValues becomes a noun-phrase
            // terminator. Operator adds a dialect adjective in JSON → noun extraction picks
            // it up automatically.
            var statusStops = arCues.StatusValues
                .Select(s => s.Cue)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToArray();
            var stopAlt = statusStops.Length > 0 ? string.Join("|", statusStops) + "|في|بدون" : "في|بدون";

            const string nounCap = @"(?<noun>[؀-ۿ\s]+?)";
            var endOrStop = $@"(?:\s+(?:{stopAlt})\b|$)";

            _intentGate = new Regex($@"(?:{countSrc}|{listSrc}|{sumSrc})", RegexOptions.Compiled);
            _countForm  = new Regex($@"(?:{countSrc})\s+{nounCap}{endOrStop}", RegexOptions.Compiled);
            _listForm   = new Regex($@"(?:{listSrc})\s+{nounCap}{endOrStop}", RegexOptions.Compiled);
            // SUM keeps structural particles مبالغ/قيم (sum-form qualifiers, not vocabulary).
            _sumForm    = new Regex($@"(?:{sumSrc})\s+(?:مبالغ\s+|قيم\s+)?(?<noun>[؀-ۿ\s]+)", RegexOptions.Compiled);

            return (_intentGate, _countForm, _listForm, _sumForm);
        }
    }

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        var q = StripAnnotations(ctx.Question);
        var (intentGate, countForm, listForm, sumForm) = GetIntentForms();
        if (!intentGate.IsMatch(q)) return;

        // Resolve the entity by walking each Arabic noun token across semantic-layer synonyms.
        // The pattern groups give us the noun phrase; we then look up the longest synonym match.
        string? targetTable = null;

        var countMatch = countForm.Match(q);
        var listMatch = listForm.Match(q);
        var sumMatch = sumForm.Match(q);

        bool isCount = countMatch.Success;
        bool isList = !isCount && listMatch.Success;
        bool isSum = !isCount && !isList && sumMatch.Success;

        if (isCount) targetTable = ResolveEntityFromNoun(countMatch.Groups["noun"].Value, ctx);
        else if (isList) targetTable = ResolveEntityFromNoun(listMatch.Groups["noun"].Value, ctx);
        else if (isSum) targetTable = ResolveEntityFromNoun(sumMatch.Groups["noun"].Value, ctx);

        // Fallback: scan the whole question against every entity's Arabic synonyms.
        if (targetTable is null) targetTable = ResolveEntityFromAnyArabicSynonym(q, ctx);
        if (targetTable is null) return;
        if (!ctx.Catalog.TableExists(targetTable)) return;

        // Set Root authoritatively.
        var prevRoot = spec.Root;
        if (!string.Equals(spec.Root, targetTable, System.StringComparison.OrdinalIgnoreCase))
        {
            spec.Root = targetTable;
            ctx.Diagnostics.Add(new(Name,
                $"AR dispatch: root {(string.IsNullOrEmpty(prevRoot) ? "(unset)" : prevRoot)}→{targetTable}"));
            // The LLM's Select / Joins were aimed at the wrong entity; wipe them so downstream
            // EnrichSelectWithLabels can re-populate from the correct root.
            spec.Select.Clear();
            spec.Joins.Clear();
            // Aggressively clear filters whose column refers to a column on a DIFFERENT table —
            // the LLM-emitted Customer.Status filter is wrong for a Tickets question.
            for (int i = spec.Filters.Count - 1; i >= 0; i--)
            {
                var f = spec.Filters[i];
                if (string.IsNullOrEmpty(f.Column)) continue;
                var dot = f.Column.IndexOf('.');
                if (dot <= 0) continue;
                var tableQual = f.Column.Substring(0, dot);
                if (!string.Equals(tableQual, targetTable, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tableQual, prevRoot ?? "", System.StringComparison.OrdinalIgnoreCase))
                {
                    spec.Filters.RemoveAt(i);
                }
            }
            // Remove filters that still reference the OLD root's qualified columns.
            if (!string.IsNullOrEmpty(prevRoot))
            {
                for (int i = spec.Filters.Count - 1; i >= 0; i--)
                {
                    var f = spec.Filters[i];
                    if (string.IsNullOrEmpty(f.Column)) continue;
                    if (f.Column.StartsWith(prevRoot + ".", System.StringComparison.OrdinalIgnoreCase))
                        spec.Filters.RemoveAt(i);
                }
            }
        }

        // Apply Arabic adjective → canonical status filter (only if column exists on root).
        // Vocabulary loaded from linguistic-cues.json (locales.ar.statusValues) — no recompile.
        ApplyArabicStatusFilter(spec, q, ctx, _cues);

        // Shape-specific spec rewrites.
        if (isCount)
        {
            spec.Aggregations.Clear();
            spec.GroupBy.Clear();
            spec.Select.Clear();
            spec.Aggregations.Add(new AggregateSpec
            {
                Function = SpecConst.Aggregates.Count,
                Column = "*",
                Alias = "Count",
            });
            spec.Limit = null;
            spec.Offset = null;
            ctx.Diagnostics.Add(new(Name, $"AR dispatch: COUNT(*) over {targetTable}"));
        }
        else if (isSum)
        {
            var numericCol = PickNumericColumnFromConfig(targetTable, ctx);
            if (numericCol is not null)
            {
                spec.Aggregations.Clear();
                spec.GroupBy.Clear();
                spec.Select.Clear();
                spec.Aggregations.Add(new AggregateSpec
                {
                    Function = SpecConst.Aggregates.Sum,
                    Column = $"{targetTable}.{numericCol}",
                    Alias = $"SumOf{numericCol}",
                });
                spec.Limit = null;
                spec.Offset = null;
                ctx.Diagnostics.Add(new(Name, $"AR dispatch: SUM({targetTable}.{numericCol})"));
            }
        }
    }

    /// <summary>
    /// Resolve an entity table by matching each Arabic token in the noun phrase against the
    /// semantic-layer's entity synonyms. Longest synonym match wins.
    /// </summary>
    // Entity-resolution + status-filter + numeric-column + small helpers live in the
    // partial file ArabicQuestionDispatchPhase.EntityResolution.cs.
}
