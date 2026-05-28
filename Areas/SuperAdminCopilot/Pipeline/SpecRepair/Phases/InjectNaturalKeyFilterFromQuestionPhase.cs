namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Semantic;

/// <summary>
/// When the user's question contains a token matching the root entity's
/// <see cref="EntityDefinition.NaturalKeyFormat"/> regex (e.g. <c>TKT-00050</c> for Tickets,
/// <c>ORD-2026-005</c> for Orders), inject a <c>WHERE NaturalKeyColumn = '&lt;match&gt;'</c>
/// filter. Without this, "show me ticket TKT-00030" generates SELECT … WITHOUT the filter
/// → returns every ticket → useless answer.
///
/// <para>Universal & schema-driven: works for any entity that declares <c>naturalKeyColumn</c>
/// + <c>naturalKeyFormat</c> in <c>semantic-layer.json</c>. No hardcoded entity names.</para>
/// </summary>
internal sealed class InjectNaturalKeyFilterFromQuestionPhase : ISpecRepairPhase
{
    public string Name => "InjectNaturalKeyFilterFromQuestion";
    public string Covers => "Natural-key token in question text (TKT-00030) → inject WHERE NaturalKeyCol=value";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip the "-- requested columns: ..." trailing hint the structural-cue parser injects;
        // we only want the user's actual words for natural-key matching.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Pre-extract all natural-key-shaped tokens in the question once; tokenising on
        // whitespace/punctuation avoids the false-positive case where an entity's regex (e.g.
        // <c>^TKT-\d+$</c>) is anchored on the whole string. We still want to match a bare token
        // in "show me ticket TKT-00030 please".
        var keyTokens = Regex.Matches(q, @"[A-Za-z]{1,6}-\d[\d-]*", RegexOptions.IgnoreCase);
        if (keyTokens.Count == 0) return;

        // Try Root entity FIRST — the common case.
        var rootEntity = ctx.SemanticLayer.GetEntityForTable(spec.Root);
        if (TryMatch(rootEntity, keyTokens, out var rootMatch))
        {
            ApplyMatch(spec, ctx, spec.Root, rootEntity!.NaturalKeyColumn!, rootMatch!);
            return;
        }

        // Fallback — Root has no natural-key match. Scan ALL entities; if some OTHER entity's
        // natural-key format matches a token in the question, the LLM almost certainly picked
        // the wrong root (e.g. "what is the status of TKT-00020" → model picked TicketStatuses
        // instead of Tickets). Rewrite the root to the matched entity and inject the filter.
        // The pre-natural-key rewrite is more correct than running with the bogus root.
        foreach (var candidate in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(candidate.NaturalKeyColumn) || string.IsNullOrEmpty(candidate.NaturalKeyFormat)) continue;
            if (string.Equals(candidate.Table, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!ctx.Catalog.TableExists(candidate.Table)) continue;
            if (!ctx.Catalog.ColumnExists(candidate.Table, candidate.NaturalKeyColumn)) continue;
            if (!TryMatch(candidate, keyTokens, out var otherMatch)) continue;

            var previousRoot = spec.Root;
            spec.Root = candidate.Table;
            ctx.Diagnostics.Add(new(Name, $"rewrote root '{previousRoot}' → '{candidate.Table}' (natural-key '{otherMatch}' found in question)"));
            ApplyMatch(spec, ctx, candidate.Table, candidate.NaturalKeyColumn, otherMatch!);
            return;
        }
    }

    private static bool TryMatch(EntityDefinition? entity, MatchCollection tokens, out string? matched)
    {
        matched = null;
        if (entity is null) return false;
        if (string.IsNullOrEmpty(entity.NaturalKeyColumn) || string.IsNullOrEmpty(entity.NaturalKeyFormat)) return false;
        Regex rx;
        try { rx = new Regex(entity.NaturalKeyFormat!, RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return false; }
        foreach (Match m in tokens)
        {
            if (rx.IsMatch(m.Value)) { matched = m.Value; return true; }
        }
        return false;
    }

    private static void ApplyMatch(QuerySpec spec, SpecRepairContext ctx, string table, string col, string value)
    {
        var qualified = $"{table}.{col}";
        // Skip if the LLM already added a filter on the natural-key column — don't double-up.
        foreach (var f in spec.Filters)
            if (string.Equals(f.Column, qualified, System.StringComparison.OrdinalIgnoreCase))
                return;
        spec.Filters.Add(new FilterSpec { Column = qualified, Op = "eq", Value = value });
        ctx.Diagnostics.Add(new("InjectNaturalKeyFilterFromQuestion", $"injected filter {qualified} = '{value}'"));
    }
}
