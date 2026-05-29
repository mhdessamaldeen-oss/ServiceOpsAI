namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Phase 08 — universal text-search filter injection.
///
/// <para>The user asks "tickets containing electricity" / "outages about cable" / "work orders
/// mentioning transformer". The local 7B planner often (a) routes the request to the
/// semantic-search path (no embeddings for non-Tickets entities → 0 rows), or (b) emits a
/// LIKE filter on the wrong column (status enum, lookup ID), or (c) doesn't emit a filter at
/// all.</para>
///
/// <para>This phase detects a text-search trigger in the question — triggers loaded ENTIRELY
/// from <c>linguistic-cues.json</c>'s <c>textSearchTriggers</c> per locale. NO hardcoded
/// vocabulary in this file. When a trigger fires, we extract the captured noun and emit a
/// single FilterSpec with <c>op=text_search</c>. The compiler's existing
/// <c>BuildTextSearchClause</c> handler then renders it as
/// <c>(col1 LIKE @p OR col2 LIKE @p OR ...)</c> over the entity's
/// <see cref="Semantic.EntityDefinition.SearchableColumns"/>.</para>
///
/// <para><b>Universal:</b> new entity? Declare its <c>searchableColumns</c> in
/// semantic-layer.json — text search works without recompile. New trigger word / dialect?
/// Add to <c>linguistic-cues.json</c>'s <c>textSearchTriggers.&lt;locale&gt;</c> — works
/// without recompile. Works for any noun the user types after any configured trigger.</para>
///
/// <para>Weak-model crutch: a strong NLU planner emits the text_search filter itself.
/// Auto-skipped at Medium+ via the tier-toggle architecture.</para>
/// </summary>
internal sealed class InjectTextSearchFilterPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public InjectTextSearchFilterPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "InjectTextSearchFilter";
    public string Covers =>
        "'X containing Y' / 'X about Y' / 'X mentioning Y' (triggers from linguistic-cues.json) " +
        "→ emit Op=text_search over the root entity's searchableColumns from semantic-layer.json.";

    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = StripAnnotations(ctx.Question);

        // Extract the search noun using the per-locale trigger regexes from
        // linguistic-cues.json. No hardcoded words in this file — all vocab lives in JSON.
        string? noun = TryExtractNounFromCues(q, _cues);
        if (string.IsNullOrWhiteSpace(noun)) return;
        noun = noun.Trim().Trim('\'', '"', '“', '”');
        if (noun.Length < 2) return;

        // Load the root entity's searchableColumns. If none configured, the operator hasn't
        // declared this entity as text-searchable — silently skip (don't guess columns).
        var rootEntity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
        if (rootEntity is null) return;
        if (rootEntity.SearchableColumns is null || rootEntity.SearchableColumns.Count == 0) return;

        // Skip when the LLM already emitted a like / text_search filter on the root.
        // Three valid column shapes for an existing root-text filter:
        //   • "Root.SomeColumn"  — qualified LIKE on a specific column
        //   • "Root"             — bare root (this phase's own text_search emission)
        //   • "expr"             — legacy raw-expression placeholder
        foreach (var f in spec.Filters)
        {
            if (f is null) continue;
            var op = (f.Op ?? "").ToLowerInvariant();
            bool isTextish = op == SpecConst.FilterOps.TextSearch
                          || op == SpecConst.FilterOps.Like
                          || op == SpecConst.FilterOps.NotLike;
            if (isTextish && !string.IsNullOrEmpty(f.Column)
                && (f.Column.StartsWith(spec.Root + ".", System.StringComparison.OrdinalIgnoreCase)
                    || f.Column.Equals(spec.Root, System.StringComparison.OrdinalIgnoreCase)
                    || f.Column.Equals("expr", System.StringComparison.OrdinalIgnoreCase)))
                return;
        }

        // Emit ONE FilterSpec with op=text_search; the compiler's BuildTextSearchClause
        // walks SearchableColumns and renders `(col1 LIKE @p OR col2 LIKE @p OR ...)`.
        // We pass the root table as the column so the compiler resolves the entity.
        spec.Filters.Add(new FilterSpec
        {
            Column = spec.Root,
            Op = SpecConst.FilterOps.TextSearch,
            Value = noun,
        });
        ctx.Diagnostics.Add(new(Name,
            $"injected Op=text_search over {rootEntity.SearchableColumns.Count} searchable column(s) on {spec.Root} for noun '{noun}'"));
    }

    /// <summary>
    /// Walk every configured locale's text-search triggers from linguistic-cues.json. Each
    /// trigger is a regex with a named <c>noun</c> capture group. First trigger that fires
    /// against the question wins; we return the captured noun. Returns null when no trigger
    /// matches — caller then no-ops (universal — no English/Arabic fallback hardcoded).
    /// </summary>
    private static string? TryExtractNounFromCues(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return null;
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale?.TextSearchTriggers is null) continue;
            foreach (var rx in locale.TextSearchTriggers)
            {
                if (rx is null) continue;
                var m = rx.Match(question);
                if (!m.Success) continue;
                var noun = m.Groups["noun"].Success ? m.Groups["noun"].Value : null;
                if (!string.IsNullOrWhiteSpace(noun)) return noun;
            }
        }
        return null;
    }

    private static string StripAnnotations(string q)
    {
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? q.Substring(0, dashIdx) : q;
    }
}
