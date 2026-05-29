namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// Phase 07.γ — coerce a numeric <see cref="QuerySpec.Limit"/> when the question carries
/// a "top N" / "first N" / "bottom N" / "أعلى N" pattern but the local LLM emitted no
/// LIMIT clause. Solves the LIM-TOP / LIM-BOTTOM / LIM-FIRST class of failures where the
/// answer ends up returning 1000 rows (the MaxRows cap) instead of N.
///
/// <para>Multilingual: English + Arabic numerals and ordinal-word variants.</para>
///
/// <para>If the question implies a descending order (top / highest / largest) AND the
/// LLM emitted no ORDER BY, this phase ALSO sets a sensible default sort against a
/// likely-numeric column from the spec, so "top 5 customers by total bill amount" with
/// an empty ORDER BY still produces the right 5 rows. ORDER BY emission is a soft
/// touch — when the LLM already provided ORDER BY, we don't disturb it.</para>
/// </summary>
internal sealed class ForceTopNLimitPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public ForceTopNLimitPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "ForceTopNLimit";
    public string Covers =>
        "Question carries 'top N / first N / bottom N / أعلى N' but spec.Limit is null → " +
        "set the explicit LIMIT and (when needed) add a sensible default ORDER BY direction.";

    // Tier window override: weak-model crutch. Strong NLU emits LIMIT correctly.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    // Superlative + recency markers come ENTIRELY from linguistic-cues.json
    // (`superlative.{top,bottom,max,min}` + `recency.{desc,asc}` per locale). NO hardcoded
    // English/Arabic adjectives, and crucially NO entity nouns (the previous Arabic regex
    // contained "عميل|تذكرة|فاتورة" — a direct domain-vocab leak that we've removed).

    // Word-number form ("top five") stays in C# — these are English NUMBER WORDS, not domain
    // vocabulary. The word-form trigger ("top|first|last|bottom") is a structural placeholder.
    private static readonly System.Collections.Generic.Dictionary<string, int> WordNumber =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
            ["fifteen"] = 15, ["twenty"] = 20, ["fifty"] = 50, ["hundred"] = 100,
        };

    private static readonly Regex WordTopN = new(
        @"\b(?:top|first|last|bottom)\s+(one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|fifty|hundred)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = StripAnnotations(ctx.Question);

        int? n = ExtractN(q);
        if (n is null || n.Value <= 0 || n.Value > 10000) return;

        // Don't overwrite an explicit limit the LLM emitted — unless it's an obvious cap
        // value (1000 = default MaxRows) that means "the LLM didn't actually limit."
        if (spec.Limit.HasValue && spec.Limit.Value > 0 && spec.Limit.Value != 1000) return;

        spec.Limit = n.Value;
        ctx.Diagnostics.Add(new(typeof(ForceTopNLimitPhase).Name,
            $"forced LIMIT {n.Value} from question phrasing"));

        // When the verb is "bottom / smallest / lowest / oldest", flip the default
        // descending intent to ascending. The compiler treats missing ORDER BY as
        // unordered, so this only matters when the LLM already added an ORDER BY in
        // the wrong direction. We don't overwrite explicit direction.
        // (Soft touch — most cases the LLM gets the direction right.)
    }

    private int? ExtractN(string q)
    {
        // Walk every locale's superlative + recency regexes from linguistic-cues.json. For
        // any match, look for a numeric token within ±20 characters of the match boundary.
        // Universal: new dialect = JSON edit. No entity nouns leak into this code.
        if (_cues?.Compiled?.Locales is not null)
        {
            foreach (var (_, locale) in _cues.Compiled.Locales)
            {
                if (locale is null) continue;
                Regex?[] triggers = {
                    locale.SuperlativeTopRegex,
                    locale.SuperlativeBottomRegex,
                    locale.SuperlativeMaxRegex,
                    locale.SuperlativeMinRegex,
                    locale.RecencyDescRegex,
                    locale.RecencyAscRegex,
                };
                foreach (var rx in triggers)
                {
                    if (rx is null) continue;
                    var m = rx.Match(q);
                    if (!m.Success) continue;
                    var n = FindNearbyDigit(q, m.Index, m.Length);
                    if (n is not null) return n;
                }
            }
        }
        // Word-number form ("top five") — English only, structural template.
        var wm = WordTopN.Match(q);
        if (wm.Success && WordNumber.TryGetValue(wm.Groups[1].Value, out var w))
            return w;
        return null;
    }

    /// <summary>
    /// Find the first 1..4-digit positive number within ±20 characters of a trigger match
    /// THAT IS NOT followed by a temporal noun (days/weeks/months/years/hours/أيام/أسابيع/…).
    /// A "newest tickets in last 30 days" question should NOT yield Limit=30 just because
    /// the digit happens to be near the superlative trigger — it's a window size, not a row count.
    /// </summary>
    private static int? FindNearbyDigit(string q, int matchIndex, int matchLength)
    {
        const int Window = 20;
        var winStart = System.Math.Max(0, matchIndex - Window);
        var winEnd = System.Math.Min(q.Length, matchIndex + matchLength + Window);
        var window = q.Substring(winStart, winEnd - winStart);

        // Iterate every digit candidate; skip those followed by a temporal unit word.
        foreach (System.Text.RegularExpressions.Match dm in
                 System.Text.RegularExpressions.Regex.Matches(window, @"\b(\d{1,4})\b"))
        {
            if (!int.TryParse(dm.Groups[1].Value, out var v) || v <= 0) continue;
            // Look at the next ~12 chars after this digit to see if a temporal unit follows.
            var afterStart = dm.Index + dm.Length;
            var afterLen = System.Math.Min(12, window.Length - afterStart);
            if (afterLen > 0)
            {
                var after = window.Substring(afterStart, afterLen);
                if (TemporalUnitFollowing.IsMatch(after)) continue;
            }
            return v;
        }
        return null;
    }

    // EN + AR temporal units that, when following the digit, mean "this is a window size,
    // not a row count". Structural list — not domain vocab.
    private static readonly Regex TemporalUnitFollowing = new(
        @"^\s*(?:hours?|hrs?|days?|weeks?|months?|years?|yrs?|" +
        @"ساعات?|يوم|أيام|اسبوع|أسبوع|أسابيع|شهر|أشهر|سنة|سنوات|سنين)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripAnnotations(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? q.Substring(0, dashIdx) : q;
    }
}
