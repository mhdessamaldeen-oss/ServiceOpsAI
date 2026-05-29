namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>Score candidates against question text + entity synonyms; set or override root when multi-word match beats LLM choice.</summary>
internal sealed class InferRootFromQuestionPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public InferRootFromQuestionPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "InferRootFromQuestion";
    public string Covers => "Missing or weakly-matched root vs better question-text match";

    // Tier window override: weak-model crutch.
    // Possessive-marker tiers. Logic is universal; the markers themselves are language-specific
    // and live in linguistic-cues.json (locales.{en,ar}.possessive). Strong NLU routes possessives natively.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Medium;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip annotation lines appended by the retriever — they pollute scoring.
        var cleaned = ctx.Question;
        var dashIdx = cleaned.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) cleaned = cleaned.Substring(0, dashIdx);
        var qLower = cleaned.ToLowerInvariant();

        // Phase 07.γ — "X and their Y" / "X with their Y" deterministic root choice.
        // When the question uses possessive phrasing ("their" / "its"), the FIRST noun
        // anchors the root, not the second. Without this, the LLM often picks the second
        // noun (especially when EnrichSelectWithLabels later projects FK names), reversing
        // the intended X→Y relationship. Example: "tickets and their customer names" →
        // root MUST be Tickets (not Customers).
        var possessiveRoot = ResolvePossessiveRoot(qLower, ctx, _cues);
        if (!string.IsNullOrEmpty(possessiveRoot)
            && !string.Equals(spec.Root, possessiveRoot, System.StringComparison.OrdinalIgnoreCase))
        {
            var prevRoot = spec.Root;
            spec.Root = possessiveRoot;
            ctx.Diagnostics.Add(new(Name,
                $"deterministic root from 'X and their Y' pattern: {(string.IsNullOrEmpty(prevRoot) ? "(unset)" : prevRoot)}→{possessiveRoot}"));
            return;
        }

        // Phase 07.ε — broaden the candidate pool. The vector retriever populates
        // CandidateTables but it doesn't handle Arabic well, so the right entity may be
        // missing. Walk the FULL semantic-layer entity list — if any synonym matches the
        // question, include that entity's table as a candidate (subject to existing in
        // the schema catalog). Universal: works for any language, any entity, no
        // hardcoded names — driven entirely by the synonym list each entity declares.
        var pool = ctx.CandidateTables.ToList();
        var poolNames = new System.Collections.Generic.HashSet<string>(
            pool.Select(p => p.Name), System.StringComparer.OrdinalIgnoreCase);
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            if (poolNames.Contains(e.Table)) continue;
            if (!ctx.Catalog.TableExists(e.Table)) continue;
            if (e.Synonyms is not { Count: > 0 }) continue;
            // Add only when at least one synonym (or the canonical Name) actually hits the
            // question — otherwise we'd consider every entity for every question.
            bool hits = false;
            if (!string.IsNullOrEmpty(e.Name) && qLower.Contains(e.Name.ToLowerInvariant())) hits = true;
            if (!hits)
            {
                foreach (var s in e.Synonyms)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (qLower.Contains(s.ToLowerInvariant())) { hits = true; break; }
                }
            }
            if (!hits) continue;
            pool.Add(new SuperAdminCopilot.Schema.InferredTable { Name = e.Table, Schema = "dbo" });
            poolNames.Add(e.Table);
        }
        if (pool.Count == 0) return;

        string? best = null;
        int bestScore = 0;
        bool bestIsMultiWord = false;
        foreach (var t in pool)
        {
            var (score, isMulti) = ScoreCandidate(t.Name, qLower, ctx);
            if (score > bestScore) { bestScore = score; best = t.Name; bestIsMultiWord = isMulti; }
        }
        if (best is null) return;

        if (string.IsNullOrWhiteSpace(spec.Root))
        {
            spec.Root = best;
            ctx.Diagnostics.Add(new(Name, $"set root={best} (score={bestScore})"));
            return;
        }

        // Override rules (Phase E — Arabic-aware):
        //   1. If the current root has ZERO synonym/name match against the question, ANY
        //      candidate with a positive match wins. This handles Arabic single-word
        //      synonyms (الفنيين, العملاء) that previously couldn't override because the
        //      multi-word-only rule excluded them.
        //   2. Otherwise, the strong-override rule still applies: alternative must be
        //      multi-word AND meaningfully better than current.
        if (string.Equals(spec.Root, best, System.StringComparison.OrdinalIgnoreCase)) return;
        var (currentScore, _) = ScoreCandidate(spec.Root, qLower, ctx);
        if (currentScore == 0 && bestScore > 0)
        {
            var prev = spec.Root;
            spec.Root = best;
            ctx.Diagnostics.Add(new(Name, $"overrode root {prev}→{best} (current had no match against question; bestScore={bestScore})"));
            return;
        }
        if (!bestIsMultiWord) return;
        if (bestScore <= currentScore) return;
        var previous = spec.Root;
        spec.Root = best;
        ctx.Diagnostics.Add(new(Name, $"overrode root {previous}→{best} (score {currentScore}→{bestScore})"));
    }

    private static (int Score, bool IsMultiWord) ScoreCandidate(string? name, string qLower, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(name)) return (0, false);
        var lower = name.ToLowerInvariant();
        // 1. Multi-word PascalCase phrase match (strongest signal)
        var pascalSplit = SplitPascalCase(name).ToLowerInvariant();
        if (pascalSplit.Contains(' ') && qLower.Contains(pascalSplit))
            return (pascalSplit.Length + 100, true);

        // 2. Semantic-layer synonyms (multi-word strong, single-word regular)
        var entity = ctx.SemanticLayer.GetEntityForTable(name);
        if (entity is not null)
        {
            int bestSynScore = 0;
            bool bestSynMulti = false;
            foreach (var syn in entity.Synonyms)
            {
                if (string.IsNullOrEmpty(syn)) continue;
                var sLower = syn.ToLowerInvariant();
                if (!qLower.Contains(sLower)) continue;
                bool multi = sLower.Contains(' ');
                int score = multi ? sLower.Length + 100 : sLower.Length + 1;
                if (score > bestSynScore) { bestSynScore = score; bestSynMulti = multi; }
            }
            if (bestSynScore > 0) return (bestSynScore, bestSynMulti);
        }

        if (qLower.Contains(lower)) return (lower.Length + 1, false);
        if (lower.EndsWith("s") && qLower.Contains(lower.Substring(0, lower.Length - 1)))
            return (lower.Length, false);
        return (0, false);
    }

    private static string SplitPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Detect possessive-phrasing patterns where the FIRST noun anchors the root:
    ///   "tickets and their customer names"   → tickets
    ///   "customers with their bills"         → customers
    ///   "outages and their affected regions" → outages
    /// Walks every semantic-layer entity and tries the pattern
    ///   \b(synonym1) (and|with) (their|its)?
    /// against the lowercased question. First entity whose synonym matches at the
    /// LEFT side of the possessive marker wins.
    /// </summary>
    private static string? ResolvePossessiveRoot(string qLower, SpecRepairContext ctx, ILinguisticCuesProvider cues)
    {
        // Markers come from linguistic-cues.json — one block per locale. Collect tiered lists
        // across all configured locales (en+ar+future). Empty if the file is missing.
        var possessiveMarkers = new System.Collections.Generic.List<string>();
        var definiteMarkers   = new System.Collections.Generic.List<string>();
        var plainMarkers      = new System.Collections.Generic.List<string>();
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale.Possessive is null) continue;
            possessiveMarkers.AddRange(locale.Possessive.Possessive ?? new());
            definiteMarkers.AddRange(locale.Possessive.Definite ?? new());
            plainMarkers.AddRange(locale.Possessive.Plain ?? new());
        }
        if (possessiveMarkers.Count == 0 && definiteMarkers.Count == 0 && plainMarkers.Count == 0)
            return null;

        // Cheap pre-check: skip when no marker is present at all.
        bool anyPresent = false;
        foreach (var m in possessiveMarkers) if (qLower.Contains(m)) { anyPresent = true; break; }
        if (!anyPresent) foreach (var m in definiteMarkers) if (qLower.Contains(m)) { anyPresent = true; break; }
        if (!anyPresent) foreach (var m in plainMarkers) if (qLower.Contains(m)) { anyPresent = true; break; }
        if (!anyPresent) return null;

        // Two-pass scan: first try possessive+definite (stronger signal); if any entity
        // matches, pick the LONGEST among them. Only fall through to plain markers if zero
        // possessive matches were found.
        var strongResult = ScanWithMarkers(qLower, ctx,
            new[] { possessiveMarkers.ToArray(), definiteMarkers.ToArray() });
        if (strongResult is not null) return strongResult;
        return ScanWithMarkers(qLower, ctx, new[] { plainMarkers.ToArray() });
    }

    private static string? ScanWithMarkers(string qLower, SpecRepairContext ctx, string[][] markerTiers)
    {
        string? bestTable = null;
        int bestLen = 0;
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            foreach (var candidate in EnumerateNamesAndSynonyms(e))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var cLower = candidate.ToLowerInvariant();
                // Skip single-letter or stop-word candidates; they over-match.
                if (cLower.Length < 3) continue;
                foreach (var tier in markerTiers)
                {
                    foreach (var marker in tier)
                    {
                        var needle = cLower + marker;
                        if (!qLower.Contains(needle)) continue;
                        int idx = qLower.IndexOf(needle, System.StringComparison.Ordinal);
                        bool atBoundary = idx == 0 || qLower[idx - 1] == ' ';
                        if (!atBoundary) continue;
                        if (cLower.Length > bestLen) { bestLen = cLower.Length; bestTable = e.Table; }
                        goto NextCandidate;
                    }
                }
                NextCandidate: ;
            }
        }
        return bestTable;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateNamesAndSynonyms(Semantic.EntityDefinition e)
    {
        if (!string.IsNullOrEmpty(e.Name)) yield return e.Name;
        if (!string.IsNullOrEmpty(e.Table)) yield return e.Table;
        if (e.Synonyms is { Count: > 0 })
            foreach (var s in e.Synonyms) if (!string.IsNullOrEmpty(s)) yield return s;
    }
}
