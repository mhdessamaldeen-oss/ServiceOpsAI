namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Entity-resolution + status-filter helpers for <see cref="ArabicQuestionDispatchPhase"/>.
///
/// <para>This partial contains the synonym-walking logic that maps an Arabic noun phrase to
/// a target entity table, the Arabic status-adjective → canonical-value filter injection,
/// the numeric-column picker (driven by semantic-layer.json), and small string helpers.</para>
/// </summary>
internal sealed partial class ArabicQuestionDispatchPhase
{
    /// <summary>
    /// Resolve an entity table by matching each Arabic token in the noun phrase against the
    /// semantic-layer's entity synonyms. Longest synonym match wins. Universal — no hardcoded
    /// entity names; new entity gets resolved as soon as it lists Arabic synonyms.
    /// </summary>
    private static string? ResolveEntityFromNoun(string nounPhrase, SpecRepairContext ctx)
    {
        if (string.IsNullOrWhiteSpace(nounPhrase)) return null;
        var nounLower = nounPhrase.Trim().ToLowerInvariant();

        string? bestTable = null;
        int bestLen = 0;
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            if (e.Synonyms is not { Count: > 0 }) continue;
            foreach (var syn in e.Synonyms)
            {
                if (string.IsNullOrWhiteSpace(syn)) continue;
                // Only consider Arabic-script synonyms here — English ones would over-match on
                // any word boundary and create false positives.
                if (!ContainsArabic(syn)) continue;
                var sLower = syn.ToLowerInvariant();
                if (!nounLower.Contains(sLower)) continue;
                if (sLower.Length > bestLen) { bestLen = sLower.Length; bestTable = e.Table; }
            }
        }
        return bestTable;
    }

    /// <summary>
    /// Fallback when the regex grouping didn't isolate the noun cleanly (e.g. punctuation,
    /// extra particles). Scans the entire question against every entity's Arabic synonyms.
    /// </summary>
    private static string? ResolveEntityFromAnyArabicSynonym(string question, SpecRepairContext ctx)
    {
        var qLower = question.ToLowerInvariant();
        string? bestTable = null;
        int bestLen = 0;
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            if (e.Synonyms is not { Count: > 0 }) continue;
            foreach (var syn in e.Synonyms)
            {
                if (string.IsNullOrWhiteSpace(syn)) continue;
                if (!ContainsArabic(syn)) continue;
                var sLower = syn.ToLowerInvariant();
                if (!qLower.Contains(sLower)) continue;
                if (sLower.Length > bestLen) { bestLen = sLower.Length; bestTable = e.Table; }
            }
        }
        return bestTable;
    }

    /// <summary>
    /// Walk Arabic status / severity cues (from <c>linguistic-cues.json.locales.ar.statusValues</c>);
    /// for any cue present in the question, if the cue's target column exists on the chosen
    /// root, add an equality filter against the canonical English value. Skip if a filter on
    /// that column already exists. Vocabulary lives entirely in JSON.
    /// </summary>
    private static void ApplyArabicStatusFilter(QuerySpec spec, string q, SpecRepairContext ctx, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrEmpty(spec.Root)) return;
        if (!cues.Compiled.Locales.TryGetValue("ar", out var arCues)) return;

        foreach (var entry in arCues.StatusValues)
        {
            if (string.IsNullOrEmpty(entry.Cue) || string.IsNullOrEmpty(entry.Column)) continue;
            if (!q.Contains(entry.Cue)) continue;
            if (!ctx.Catalog.ColumnExists(spec.Root, entry.Column)) continue;

            // Already filtered on this column?
            bool already = false;
            foreach (var f in spec.Filters.NotNull())
            {
                if (string.IsNullOrEmpty(f.Column)) continue;
                var bare = f.Column.Contains('.') ? f.Column.Substring(f.Column.IndexOf('.') + 1) : f.Column;
                if (string.Equals(bare, entry.Column, System.StringComparison.OrdinalIgnoreCase)) { already = true; break; }
            }
            if (already) continue;

            spec.Filters.Add(new FilterSpec
            {
                Column = $"{spec.Root}.{entry.Column}",
                Op = SpecConst.FilterOps.Eq,
                Value = entry.Value,
            });
            // Only one cue per question — the first match wins to avoid contradictory filters.
            break;
        }
    }

    /// <summary>
    /// Pick a representative numeric column for SUM. Preference list comes from
    /// <c>semantic-layer.json.defaults.numericColumnPreference</c> — operator adds a new
    /// column name once and both this Arabic-dispatch phase AND
    /// <c>ForceNonCountAggregationPhase</c> pick it up. NO hardcoded column names in C#.
    /// </summary>
    private static string? PickNumericColumnFromConfig(string table, SpecRepairContext ctx)
    {
        var preferred = ctx.SemanticLayer?.Config?.Defaults?.NumericColumnPreference;
        if (preferred is null || preferred.Count == 0) return null;
        foreach (var p in preferred)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (ctx.Catalog.ColumnExists(table, p)) return p;
        }
        return null;
    }

    private static bool ContainsArabic(string s)
    {
        foreach (var ch in s)
            if (ch >= '؀' && ch <= 'ۿ') return true;
        return false;
    }

    private static string StripAnnotations(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? q.Substring(0, dashIdx) : q;
    }
}
