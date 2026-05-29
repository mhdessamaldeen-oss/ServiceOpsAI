namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// When the question says "no X" / "without X" / "missing X" / "has no X" / "lacks X" and the LLM
/// emitted a filter of shape <c>column = '' </c> (or <c>column = null-string</c>) on a nullable
/// column, rewrite the filter to <c>column IS NULL</c>. Mirrors the same fix for absence-of-value
/// questions in Arabic ("بدون", "ما عنده").
///
/// <para>Why: real-world database "missing" values are NULL, not empty string. The LLM
/// frequently emits <c>WHERE Email = @p0</c> with @p0='' for "customers with no email" — that
/// matches zero rows when the column is NULL for missing emails. The semantic intent is
/// IS NULL.</para>
///
/// <para>Also detects the inverse case where NO filter was emitted at all but the question
/// asks about absence — injects <c>WHERE column IS NULL</c> on the most likely candidate
/// column. The candidate is matched from the noun after "no" / "without" against root entity's
/// nullable columns. Conservative: only fires when exactly one nullable column matches.</para>
/// </summary>
internal sealed class RewriteEmptyToIsNullPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public RewriteEmptyToIsNullPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "RewriteEmptyToIsNull";
    public string Covers => "'no X' / 'without X' / 'missing X' → rewrite column='' to column IS NULL (or inject the IS NULL filter when missing)";

    // Tier window override: weak-model crutch.
    // Absence cue regex (from linguistic-cues.json.absence per locale) → IS NULL. Strong NLU emits IS NULL directly.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Weak;

    // Noun-capture regex for Pass-2 (the singular noun after an absence keyword). The
    // ABSENCE KEYWORDS THEMSELVES come from linguistic-cues.json (provider.AbsenceRegex);
    // this regex only extracts the noun after the keyword. The regex is locale-agnostic in
    // shape — runs once the provider has already confirmed an absence cue is present.
    private static readonly Regex EnAbsenceNounCapture = new(
        @"\b(?:no|without|missing|lacks?|lacking|has\s+no|have\s+no|do\s+not\s+have|don'?t\s+have)\s+([a-z][a-z_\s]{2,30})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        // Strip the "-- requested columns: ..." trailing hint.
        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        // Absence cue check — all locales from linguistic-cues.json's `absence` block. NO
        // hardcoded vocab in C#: operator adds dialects / synonyms via JSON.
        if (!QuestionHasAbsenceCue(q, _cues)) return;
        var enMatches = EnAbsenceNounCapture.Matches(q);

        // Pass 1 — rewrite any existing filter that's `column = ''` (or empty/null literal) to IS NULL,
        // when the column is nullable. Independent of the noun matching below: even if we can't
        // find the column from the question, an empty-string filter on a nullable column is almost
        // always a mistake.
        var rewriteCount = 0;
        foreach (var f in spec.Filters)
        {
            if (f is null) continue;
            var op = (f.Op ?? "eq").ToLowerInvariant();
            if (op != "eq") continue;
            if (!IsEmptyOrNullValue(f.Value)) continue;
            if (!IsNullableColumn(f.Column, ctx)) continue;
            f.Op = SpecConst.FilterOps.IsNullAlt;
            f.Value = null;
            rewriteCount++;
        }
        if (rewriteCount > 0)
            ctx.Diagnostics.Add(new(Name, $"rewrote {rewriteCount} '= empty' filter(s) to IS NULL (absence cue in question)"));

        // Pass 2 — for each absence noun captured from the question (English only — Arabic
        // capture is harder; rely on Pass 1 for AR), look up a nullable column on spec.Root
        // whose name "looks like" the noun. Inject IS NULL when no filter on that column exists.
        if (enMatches.Count == 0) return;
        var existingCols = new HashSet<string>(
            spec.Filters.Select(f => (f.Column ?? "").ToLowerInvariant()),
            System.StringComparer.OrdinalIgnoreCase);

        foreach (Match m in enMatches)
        {
            var noun = m.Groups[1].Value.Trim().Trim('s');         // crude singularisation
            if (noun.Length < 3) continue;
            var nounNormalised = noun.Replace(" ", "").ToLowerInvariant();
            // Candidate columns on root that match the noun (case-insensitive contains).
            var cols = ctx.Catalog.GetColumns(spec.Root);
            var candidates = cols.Where(c =>
                c.IsNullable
                && c.ColumnName.ToLowerInvariant().Contains(nounNormalised, System.StringComparison.Ordinal)).ToList();

            // Be conservative: only act if exactly one nullable column matches. Multiple matches
            // = ambiguous; better to do nothing than guess.
            if (candidates.Count != 1) continue;
            var col = candidates[0];
            var qualified = $"{spec.Root}.{col.ColumnName}";
            if (existingCols.Contains(qualified.ToLowerInvariant())) continue;
            spec.Filters.Add(new FilterSpec { Column = qualified, Op = SpecConst.FilterOps.IsNullAlt, Value = null });
            existingCols.Add(qualified.ToLowerInvariant());
            ctx.Diagnostics.Add(new(Name, $"injected {qualified} IS NULL (absence cue '{m.Value.Trim()}')"));
        }

        // Pass 3 — strip hallucinated filters on tables the user never mentioned, AND that
        // are dangling (no Select / GroupBy / OrderBy / Join reference). Symptom: "customers
        // without email" → LLM emits Email=@p0 + spurious filter on TicketSources.Name=@p1
        // that filters everything to 0 rows. Guards keep legitimate lookup-join filters
        // intact ("open tickets" → join TicketStatuses + filter on Name='Open' — TicketStatuses
        // isn't in the question but it's referenced by the join AND part of the filter
        // expression, so we keep it).
        var explicitTables = ExtractExplicitlyNamedTables(q, ctx);
        explicitTables.Add(spec.Root);
        var referencedTables = CollectReferencedTables(spec);
        var stripped = 0;
        for (int i = spec.Filters.Count - 1; i >= 0; i--)
        {
            var f = spec.Filters[i];
            if (f is null || string.IsNullOrEmpty(f.Column)) continue;
            // GUARD: text_search filters are emitted with Column=spec.Root (bare table) by
            // InjectTextSearchFilterPhase; the compiler resolves them via spec.Root directly.
            // Never strip them — their semantics aren't expressed via a Table.Column qualifier.
            if (string.Equals(f.Op, SpecConst.FilterOps.TextSearch, System.StringComparison.OrdinalIgnoreCase))
                continue;
            var dotIdx = f.Column.IndexOf('.');
            if (dotIdx <= 0) continue;
            var tableQual = f.Column.Substring(0, dotIdx).Trim('[', ']');
            if (explicitTables.Contains(tableQual)) continue;
            // GUARD: keep when the table is referenced by Select / GroupBy / OrderBy / Joins.
            // If the LLM joined and projected from it, the filter is part of an intentional
            // join expression — don't strip.
            if (referencedTables.Contains(tableQual)) continue;
            spec.Filters.RemoveAt(i);
            stripped++;
        }
        if (stripped > 0)
            ctx.Diagnostics.Add(new(Name, $"stripped {stripped} dangling filter(s) on tables not named in the question and unreferenced elsewhere in the spec"));
    }

    /// <summary>
    /// Collect every table qualifier referenced by anything OTHER than Filters: Select,
    /// GroupBy, OrderBy, Joins, Aggregations, Computed. A table that appears in any of those
    /// is part of the intended query shape; its filter, even if the table wasn't named in
    /// the question, is intentional and must not be stripped.
    /// </summary>
    /// <summary>
    /// True when ANY locale's compiled absence regex matches the question. Provider compiles
    /// `linguistic-cues.json.locales[*].absence` arrays into <see cref="CompiledLocaleCues.AbsenceRegex"/>.
    /// </summary>
    private static bool QuestionHasAbsenceCue(string question, ILinguisticCuesProvider cues)
    {
        if (string.IsNullOrWhiteSpace(question) || cues?.Compiled?.Locales is null) return false;
        foreach (var (_, locale) in cues.Compiled.Locales)
        {
            if (locale?.AbsenceRegex is null) continue;
            if (locale.AbsenceRegex.IsMatch(question)) return true;
        }
        return false;
    }

    private static System.Collections.Generic.HashSet<string> CollectReferencedTables(QuerySpec spec)
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        void AddFromColumn(string? col)
        {
            if (string.IsNullOrEmpty(col)) return;
            var dotIdx = col.IndexOf('.');
            if (dotIdx <= 0) return;
            set.Add(col.Substring(0, dotIdx).Trim('[', ']'));
        }
        if (spec.Select is not null) foreach (var s in spec.Select) AddFromColumn(s);
        if (spec.GroupBy is not null) foreach (var g in spec.GroupBy) AddFromColumn(g);
        if (spec.OrderBy is not null) foreach (var o in spec.OrderBy) AddFromColumn(o?.Column);
        if (spec.Joins is not null) foreach (var j in spec.Joins) if (!string.IsNullOrEmpty(j?.Table)) set.Add(j!.Table);
        if (spec.Aggregations is not null) foreach (var a in spec.Aggregations) AddFromColumn(a?.Column);
        if (spec.Computed is not null) foreach (var c in spec.Computed) AddFromColumn(c?.Expression);
        return set;
    }

    /// <summary>
    /// Walk the semantic-layer entity synonyms; collect tables whose Name or any synonym
    /// substring-matches the (lowercased) question. Universal — driven by config, no
    /// hardcoded entity names. Used to detect which tables the user actually mentioned, so
    /// we know which downstream filters are spurious LLM hallucinations.
    /// </summary>
    private static System.Collections.Generic.HashSet<string> ExtractExplicitlyNamedTables(string question, SpecRepairContext ctx)
    {
        var result = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(question)) return result;
        var qLower = question.ToLowerInvariant();
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            bool hit = false;
            if (!string.IsNullOrEmpty(e.Name) && qLower.Contains(e.Name.ToLowerInvariant())) hit = true;
            if (!hit && e.Synonyms is { Count: > 0 })
            {
                foreach (var syn in e.Synonyms)
                {
                    if (string.IsNullOrWhiteSpace(syn)) continue;
                    if (qLower.Contains(syn.ToLowerInvariant())) { hit = true; break; }
                }
            }
            if (hit) result.Add(e.Table);
        }
        return result;
    }

    private static bool IsEmptyOrNullValue(object? v)
    {
        if (v is null) return true;
        if (v is string s) return s.Length == 0 || string.Equals(s, "null", System.StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool IsNullableColumn(string? column, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(column)) return false;
        var dotIdx = column.IndexOf('.');
        if (dotIdx <= 0) return false;
        var tbl = column.Substring(0, dotIdx).Trim('[', ']');
        var col = column.Substring(dotIdx + 1).Trim('[', ']');
        if (!ctx.Catalog.TableExists(tbl)) return false;
        var match = ctx.Catalog.GetColumns(tbl).FirstOrDefault(c =>
            string.Equals(c.ColumnName, col, System.StringComparison.OrdinalIgnoreCase));
        return match?.IsNullable == true;
    }
}
