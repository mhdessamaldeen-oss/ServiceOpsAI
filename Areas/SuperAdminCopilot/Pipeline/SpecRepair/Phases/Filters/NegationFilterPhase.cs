namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Filters;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// When the question carries a negation cue ("not in Damascus" / "except gas" / "excluding
/// closed tickets" / "other than X") AND a filter exists whose value matches the negated noun,
/// flip the filter's operator from <c>eq</c>/<c>in</c> to <c>neq</c>/<c>notin</c>.
///
/// <para>Works in concert with the grounding ValueLinker / LookupValueFilter phases: those
/// inject a positive filter from a value match; this phase detects when that value was actually
/// being NEGATED in the question and inverts the polarity.</para>
///
/// <para>Conservative: only fires when the negation cue token is within ~30 characters of the
/// matched value in the question. Avoids flipping a filter when "not" appears elsewhere in the
/// sentence ("not yet resolved tickets in Damascus" — the "not" belongs to "resolved", not
/// "Damascus").</para>
/// </summary>
internal sealed class NegationFilterPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public NegationFilterPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "NegationFilter";
    public string Covers => "Negation cue near filter value (not in Damascus / except gas / excluding X) → flip eq/in to neq/notin";

    // Tier window override: weak-model crutch.
    // Negation cues come from linguistic-cues.json `negation` block per locale.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (spec.Filters.Count == 0) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Aggregate every locale's compiled negation matches against the question.
        var negationHits = new System.Collections.Generic.List<Match>();
        if (_cues?.Compiled?.Locales is not null)
        {
            foreach (var (_, locale) in _cues.Compiled.Locales)
            {
                if (locale?.NegationRegex is null) continue;
                foreach (Match m in locale.NegationRegex.Matches(q))
                    negationHits.Add(m);
            }
        }
        if (negationHits.Count == 0) return;

        var flipped = 0;
        foreach (var f in spec.Filters)
        {
            if (f.Value is not string sv || string.IsNullOrEmpty(sv)) continue;
            if (sv[0] == '@') continue;                                   // temporal token, skip

            // Find the value's first occurrence in the question (case-insensitive).
            var valuePos = q.IndexOf(sv, System.StringComparison.OrdinalIgnoreCase);
            if (valuePos < 0) continue;

            // Any negation cue whose END is within 30 chars BEFORE the value start?
            var hitsBeforeValue = negationHits.Where(n => n.Index + n.Length <= valuePos
                                                          && valuePos - (n.Index + n.Length) <= 30);
            if (!hitsBeforeValue.Any()) continue;

            var op = (f.Op ?? "eq").ToLowerInvariant();
            if (op == SpecConst.FilterOps.Eq) { f.Op = SpecConst.FilterOps.Neq; flipped++; }
            else if (op == SpecConst.FilterOps.In || op == SpecConst.FilterOps.NotInAlt)
            {
                f.Op = SpecConst.FilterOps.NotInAlt;
                flipped++;
            }
            else if (op == SpecConst.FilterOps.Like)
            {
                f.Op = SpecConst.FilterOps.NotLike;
                flipped++;
            }
        }

        if (flipped > 0)
            ctx.Diagnostics.Add(new(Name, $"flipped polarity on {flipped} filter(s) due to negation cue"));
    }
}
