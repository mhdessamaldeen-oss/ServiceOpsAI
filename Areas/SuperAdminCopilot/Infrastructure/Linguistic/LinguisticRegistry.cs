namespace SuperAdminCopilot.Infrastructure.Linguistic;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Configuration;       // ILinguisticCuesProvider (v2 service we wrap)
using SuperAdminCopilot.Semantic;             // ISemanticLayer (v2 service we wrap)
using SuperAdminCopilot.Application.Repair;

/// <summary>
/// The single concrete implementation of <see cref="ILinguisticRegistry"/>. See ADR-004.
///
/// <para>This class is the ONLY place in v3 that may inline regex with non-ASCII letters.
/// It delegates to the v2 <see cref="ILinguisticCuesProvider"/> + <see cref="ISemanticLayer"/>
/// for the actual compiled patterns (no duplication). All public methods return typed mentions —
/// repair rules never see the raw question regex output.</para>
///
/// <para>Architecture test <c>NoHardcodedVocabTests</c> asserts no other v3 file holds
/// non-ASCII regex literals.</para>
/// </summary>
internal sealed class LinguisticRegistry : ILinguisticRegistry
{
    private readonly ILinguisticCuesProvider _cues;
    private readonly ISemanticLayer _semanticLayer;

    public LinguisticRegistry(ILinguisticCuesProvider cues, ISemanticLayer semanticLayer)
    {
        _cues = cues;
        _semanticLayer = semanticLayer;
    }

    public bool HasCue(string question, CueKind kind)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return false;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale is null) continue;
            var rx = kind switch
            {
                CueKind.Absence    => locale.AbsenceRegex,
                CueKind.AllTime    => locale.AllTimeRegex,
                CueKind.Distinct   => locale.DistinctRegex,
                CueKind.Negation   => locale.NegationRegex,
                CueKind.Comparison => locale.CompareMarkersRegex,
                _ => null,
            };
            if (rx is null) continue;
            if (rx.IsMatch(question)) return true;
        }
        return false;
    }

    public bool LooksLikeKnowledgeQuestion(string question, out string term)
    {
        term = "";
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return false;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            var rx = locale?.KnowledgeQuestionRegex;
            if (rx is null) continue;
            var m = rx.Match(question);
            if (!m.Success) continue;
            term = m.Groups["term"].Value.Trim();
            return !string.IsNullOrEmpty(term);
        }
        return false;
    }

    public bool LooksLikeAggregateQuery(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return false;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            var rx = locale?.AggregateMarkerRegex;
            if (rx is null) continue;
            if (rx.IsMatch(question)) return true;
        }
        return false;
    }

    public IReadOnlyList<TemporalSpan> ExtractTemporal(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null)
            return System.Array.Empty<TemporalSpan>();
        var hits = new List<TemporalSpan>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale?.Temporal is null) continue;
            foreach (var p in locale.Temporal)
            {
                if (p?.Pattern is null) continue;
                var m = p.Pattern.Match(question);
                if (!m.Success) continue;
                var start = p.Start;
                var end = p.End;
                if (m.Groups.Count > 1 && m.Groups[1].Success)
                {
                    var captured = m.Groups[1].Value;
                    start = string.Format(System.Globalization.CultureInfo.InvariantCulture, start, captured);
                    if (!string.IsNullOrEmpty(end) && end.Contains("{0}"))
                        end = string.Format(System.Globalization.CultureInfo.InvariantCulture, end, captured);
                }
                var key = start + "|" + (end ?? "");
                if (!seen.Add(key)) continue;
                hits.Add(new TemporalSpan(p.Label ?? m.Value, start, end, p.Op));
            }
        }
        return hits;
    }

    public IReadOnlyList<StatusMention> ExtractStatus(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null)
            return System.Array.Empty<StatusMention>();
        var mentions = new List<StatusMention>();
        var qLower = question.ToLowerInvariant();
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale?.StatusValues is null) continue;
            foreach (var sv in locale.StatusValues)
            {
                if (string.IsNullOrEmpty(sv?.Cue)) continue;
                if (qLower.Contains(sv.Cue.ToLowerInvariant()))
                    mentions.Add(new StatusMention(sv.Cue, sv.Column, sv.Value));
            }
        }
        return mentions;
    }

    public IReadOnlyList<EntityMention> ExtractEntityMentions(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question)) return System.Array.Empty<EntityMention>();
        var mentions = new List<EntityMention>();
        var qLower = question.ToLowerInvariant();
        foreach (var e in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            int bestLen = 0;
            string bestMatch = "";
            if (!string.IsNullOrEmpty(e.Name) && qLower.Contains(e.Name.ToLowerInvariant()))
            { bestLen = e.Name.Length; bestMatch = e.Name; }
            if (e.Synonyms is { Count: > 0 })
            {
                foreach (var syn in e.Synonyms)
                {
                    if (string.IsNullOrWhiteSpace(syn)) continue;
                    if (qLower.Contains(syn.ToLowerInvariant()) && syn.Length > bestLen)
                    { bestLen = syn.Length; bestMatch = syn; }
                }
            }
            if (bestLen > 0) mentions.Add(new EntityMention(e.Table, bestMatch, bestLen));
        }
        return mentions.OrderByDescending(m => m.Score).ToList();
    }

    public Superlative? ExtractSuperlative(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return null;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale is null) continue;
            var (rx, dir) = ChooseSuperlativeRegex(locale);
            if (rx is null) continue;
            var m = rx.Match(question);
            if (!m.Success) continue;
            var trigger = m.Value;
            // Look for a 1-4 digit count near the trigger (caller's old FindNearbyDigit logic).
            var n = FindNearbyDigit(question, m.Index, m.Length);
            return new Superlative(trigger, n, dir);
        }
        return null;
    }

    public AntiJoinMention? ExtractAntiJoin(string question)
    {
        // Delegate to the v2 phase's built regex by reusing the AntiJoin trigger set: build
        // a longest-first alternation from every locale's AntiJoin phrases. Same algorithm
        // the v2 InjectAntiJoinFromQuestionPhase uses — moved here so the rule code is clean.
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return null;
        if (_antiJoinRegex is null) _antiJoinRegex = BuildAntiJoinRegex();
        var m = _antiJoinRegex.Match(question);
        if (!m.Success) return null;
        var noun = m.Groups["noun"].Success ? m.Groups["noun"].Value : null;
        return string.IsNullOrEmpty(noun) ? null : new AntiJoinMention(m.Value, noun);
    }

    public TextSearchMention? ExtractTextSearch(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return null;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale?.TextSearchTriggers is null) continue;
            foreach (var rx in locale.TextSearchTriggers)
            {
                if (rx is null) continue;
                var m = rx.Match(question);
                if (!m.Success) continue;
                var noun = m.Groups["noun"].Success ? m.Groups["noun"].Value.Trim() : null;
                if (!string.IsNullOrEmpty(noun))
                    return new TextSearchMention(m.Value, noun);
            }
        }
        return null;
    }

    public NumericRange? ExtractRange(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return null;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale is null) continue;
            // BETWEEN A AND B — two capture groups.
            foreach (var rx in locale.RangeBetween ?? System.Array.Empty<System.Text.RegularExpressions.Regex>())
            {
                var m = rx.Match(question);
                if (m.Success && m.Groups.Count >= 3
                    && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var lo)
                    && decimal.TryParse(m.Groups[2].Value.Replace(",", ""), out var hi))
                    return new NumericRange(lo, hi, "between");
            }
            // GT / GTE / LT / LTE — single capture group.
            (System.Collections.Generic.IReadOnlyList<System.Text.RegularExpressions.Regex>? rxs, string op)[] singleBound =
            {
                (locale.RangeGte, "gte"),
                (locale.RangeGt,  "gt"),
                (locale.RangeLte, "lte"),
                (locale.RangeLt,  "lt"),
                (locale.RangeEq,  "eq"),
            };
            foreach (var (rxs, op) in singleBound)
            {
                if (rxs is null) continue;
                foreach (var rx in rxs)
                {
                    var m = rx.Match(question);
                    if (m.Success && m.Groups.Count >= 2
                        && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var n))
                    {
                        return op switch
                        {
                            "gte" or "gt" => new NumericRange(n, null, op),
                            "lte" or "lt" => new NumericRange(null, n, op),
                            _              => new NumericRange(n, n, "eq"),
                        };
                    }
                }
            }
        }
        return null;
    }

    public NegationMention? ExtractNegation(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || _cues.Compiled.Locales is null) return null;
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale?.NegationRegex is null) continue;
            var m = locale.NegationRegex.Match(question);
            if (m.Success) return new NegationMention(m.Value, m.Index);
        }
        return null;
    }

    public TimeSeriesGranularity? ExtractTimeSeriesGranularity(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question)) return null;
        // Structural English/Arabic time-series cues — kept as parser glue, not domain vocab.
        // EN
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:per\s+|by\s+|each\s+)day\b|\bdaily\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("day", "daily");
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:per\s+|by\s+|each\s+)week\b|\bweekly\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("week", "weekly");
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:per\s+|by\s+|each\s+)month\b|\bmonthly\b|\bmonth\s+over\s+month\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("month", "monthly");
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:per\s+|by\s+|each\s+)quarter\b|\bquarterly\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("quarter", "quarterly");
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(?:per\s+|by\s+|each\s+)year\b|\byearly\b|\bannual(?:ly)?\b|\byear\s+over\s+year\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("year", "yearly");
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"\btrend\b|\bover\s+time\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new TimeSeriesGranularity("month", "trend");        // default trend bucket = month
        return null;
    }

    public NaturalKeyTokenMention? ExtractNaturalKeyToken(string question, IReadOnlyList<NaturalKeyFormat> formats)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question) || formats is null || formats.Count == 0) return null;
        foreach (var fmt in formats)
        {
            if (string.IsNullOrEmpty(fmt.FormatRegex)) continue;
            try
            {
                var rx = new System.Text.RegularExpressions.Regex(fmt.FormatRegex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var m = rx.Match(question);
                if (m.Success) return new NaturalKeyTokenMention(fmt.Table, fmt.NaturalKeyColumn, m.Value);
            }
            catch { /* malformed regex in config — skip silently */ }
        }
        return null;
    }

    public LifecycleVerbMention? ExtractLifecycleVerb(string question)
    {
        question = StripAnnotation(question);
        if (string.IsNullOrWhiteSpace(question)) return null;
        // Structural English lifecycle verbs — these are GRAMMATICAL constructs (past
        // participles) not domain vocab. Date-role mapping comes from semantic-layer via
        // SemanticView.ResolveDateRoleForVerb. We surface the verb here; caller resolves the role.
        var verbs = new (string Pattern, string Verb)[]
        {
            (@"\bresolved\b",   "resolved"),
            (@"\bclosed\b",     "closed"),
            (@"\bcreated\b|\bopened\b|\bfiled\b|\bsubmitted\b|\breported\b|\braised\b", "created"),
            (@"\bstarted\b|\bbegan\b|\blaunched\b|\binitiated\b", "started"),
            (@"\bended\b|\bfinished\b|\bstopped\b|\bterminated\b", "ended"),
            (@"\bissued\b|\bbilled\b|\bsent\b", "issued"),
            (@"\bpaid\b|\bsettled\b", "paid"),
            (@"\bsigned\s+up\b|\bregistered\b|\bjoined\b", "registered"),
        };
        foreach (var (pat, verb) in verbs)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(question, pat,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return new LifecycleVerbMention(verb, verb);
        }
        return null;
    }

    private static (System.Text.RegularExpressions.Regex? rx, SuperlativeDirection dir) ChooseSuperlativeRegex(
        CompiledLocaleCues locale)
    {
        if (locale.SuperlativeTopRegex is not null) return (locale.SuperlativeTopRegex, SuperlativeDirection.Top);
        if (locale.SuperlativeBottomRegex is not null) return (locale.SuperlativeBottomRegex, SuperlativeDirection.Bottom);
        if (locale.SuperlativeMaxRegex is not null) return (locale.SuperlativeMaxRegex, SuperlativeDirection.MaxValue);
        if (locale.SuperlativeMinRegex is not null) return (locale.SuperlativeMinRegex, SuperlativeDirection.MinValue);
        return (null, SuperlativeDirection.Top);
    }

    private static int? FindNearbyDigit(string q, int matchIndex, int matchLength)
    {
        const int Window = 20;
        var winStart = System.Math.Max(0, matchIndex - Window);
        var winEnd = System.Math.Min(q.Length, matchIndex + matchLength + Window);
        var window = q.Substring(winStart, winEnd - winStart);
        foreach (System.Text.RegularExpressions.Match dm in
                 System.Text.RegularExpressions.Regex.Matches(window, @"\b(\d{1,4})\b"))
        {
            if (!int.TryParse(dm.Groups[1].Value, out var v) || v <= 0) continue;
            var afterStart = dm.Index + dm.Length;
            var afterLen = System.Math.Min(12, window.Length - afterStart);
            if (afterLen > 0)
            {
                var after = window.Substring(afterStart, afterLen);
                if (System.Text.RegularExpressions.Regex.IsMatch(after, "^[a-z]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // If followed by an alpha word, check the v2 list (structural — units only).
                    // We keep this here because it's parser glue, not domain vocabulary.
                    if (System.Text.RegularExpressions.Regex.IsMatch(after,
                            @"^\s*(?:hours?|hrs?|days?|weeks?|months?|years?|yrs?)\b",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        continue;
                }
            }
            return v;
        }
        return null;
    }

    /// <summary>
    /// Remove the trailing <c>\n-- ...</c> annotation block that v2's retriever appends to the
    /// question (e.g. "-- requested columns: a, b, c"). Centralised here so every Extract
    /// method strips uniformly — v2 had this scattered across phases.
    /// </summary>
    private static string StripAnnotation(string question)
    {
        if (string.IsNullOrEmpty(question)) return question;
        var dashIdx = question.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? question.Substring(0, dashIdx) : question;
    }

    // --- AntiJoin alternation builder ---
    private System.Text.RegularExpressions.Regex? _antiJoinRegex;
    private System.Text.RegularExpressions.Regex BuildAntiJoinRegex()
    {
        var alternatives = new List<string>();
        foreach (var (_, locale) in _cues.Compiled.Locales)
        {
            if (locale?.AntiJoin is null) continue;
            foreach (var phrase in locale.AntiJoin)
            {
                if (string.IsNullOrWhiteSpace(phrase)) continue;
                alternatives.Add(System.Text.RegularExpressions.Regex.Escape(phrase));
            }
        }
        if (alternatives.Count == 0)
            return new System.Text.RegularExpressions.Regex(@"(?!)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        alternatives.Sort((a, b) => b.Length.CompareTo(a.Length));
        var src = @"(?:" + string.Join("|", alternatives) + @")\s+(?<noun>\S+)";
        return new System.Text.RegularExpressions.Regex(src,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
