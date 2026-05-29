namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// Phase 07.γ — deterministic anti-join detection.
///
/// <para>The local LLM often misses anti-join semantics: "customers without any tickets"
/// returned all 205 customers (no anti-join applied) instead of the small set of customers
/// with zero tickets. This phase scans the question for anti-join markers, identifies the
/// missing-entity table from the question vocabulary, and either:</para>
///
/// <list type="number">
///   <item>Adds the target table as a <see cref="JoinSpec"/> with <c>Kind = "anti"</c>
///         (compiler emits <c>LEFT JOIN … WHERE target.PK IS NULL</c>).</item>
///   <item>Removes any redundant <c>INNER</c> join the LLM added on the same table.</item>
/// </list>
///
/// <para>Trigger patterns (English + Arabic):</para>
/// <list type="bullet">
///   <item><c>without any (table)</c></item>
///   <item><c>with no (table)</c></item>
///   <item><c>that have no (table)</c></item>
///   <item><c>missing (a|any) (table)</c></item>
///   <item><c>never (verb related to entity)</c></item>
///   <item><c>بدون (table)</c></item>
///   <item><c>ليس لديهم (table)</c></item>
/// </list>
///
/// <para>Universal — no hardcoded entity names. The target table is found by scanning the
/// semantic layer's entity synonyms (the same list used by InferRootFromQuestionPhase).</para>
/// </summary>
internal sealed class InjectAntiJoinFromQuestionPhase : ISpecRepairPhase
{
    private readonly ILinguisticCuesProvider _cues;

    public InjectAntiJoinFromQuestionPhase(ILinguisticCuesProvider cues)
    {
        _cues = cues;
    }

    public string Name => "InjectAntiJoinFromQuestion";
    public string Covers =>
        "Detect 'X without any Y' / 'X with no Y' patterns (EN + AR) → emit Y as an anti-join " +
        "so the result is X's with zero matching Y rows.";

    // Tier window override: weak-model crutch.
    // Multilingual absence-cue regex (without any / with no / بدون). Useful safety net at Medium; not needed at Strong.
    public PlannerCapabilityTier MaxTierToRun => PlannerCapabilityTier.Medium;

    // Trigger regex BUILT FROM linguistic-cues.json's `antiJoin[]` arrays per locale, with
    // a noun-capture template appended. NO hardcoded triggers in this file.
    private Regex? _trigger;
    private readonly object _triggerLock = new();

    private Regex GetTrigger()
    {
        if (_trigger is not null) return _trigger;
        lock (_triggerLock)
        {
            if (_trigger is not null) return _trigger;
            var alternatives = new System.Collections.Generic.List<string>();
            if (_cues?.Compiled?.Locales is not null)
            {
                foreach (var (_, locale) in _cues.Compiled.Locales)
                {
                    if (locale?.AntiJoin is null) continue;
                    foreach (var phrase in locale.AntiJoin)
                    {
                        if (string.IsNullOrWhiteSpace(phrase)) continue;
                        alternatives.Add(Regex.Escape(phrase));
                    }
                }
            }
            if (alternatives.Count == 0)
            {
                _trigger = new Regex(@"(?!)", RegexOptions.Compiled); // match-nothing
                return _trigger;
            }
            // Sort longest-first so "without any" wins over "without" — regex alternation is
            // left-greedy on first-match. (\S+) captures whatever follows the trigger.
            alternatives.Sort((a, b) => b.Length.CompareTo(a.Length));
            var src = @"(?:" + string.Join("|", alternatives) + @")\s+(?<noun>\S+)";
            _trigger = new Regex(src, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return _trigger;
        }
    }

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = StripAnnotations(ctx.Question);
        var match = GetTrigger().Match(q);
        if (!match.Success) return;
        var noun = match.Groups["noun"].Success ? match.Groups["noun"].Value : null;
        if (string.IsNullOrEmpty(noun)) return;

        var nounLower = noun.ToLowerInvariant();

        // Resolve the noun to an entity by walking the semantic layer's synonym list.
        // Universal: no hardcoded entity names — the noun must match the canonical Name,
        // a synonym, or singular/plural of either.
        string? targetTable = null;
        foreach (var e in ctx.SemanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(e.Table)) continue;
            if (NameOrSynonymMatches(e, nounLower))
            {
                targetTable = e.Table;
                break;
            }
        }
        if (string.IsNullOrEmpty(targetTable)) return;
        if (string.Equals(targetTable, spec.Root, System.StringComparison.OrdinalIgnoreCase)) return;
        if (!ctx.Catalog.TableExists(targetTable)) return;

        // Already has an anti-join on this table? Idempotent.
        foreach (var j in spec.Joins)
        {
            if (j is null) continue;
            if (string.Equals(j.Table, targetTable, System.StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.Kind, "anti", System.StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Remove any pre-existing INNER/LEFT join on this table — they'd compete with the
        // anti-join semantics. (Compiler treats anti as LEFT + IS NULL; an INNER would
        // INTERSECT it to empty.)
        for (int i = spec.Joins.Count - 1; i >= 0; i--)
        {
            if (string.Equals(spec.Joins[i]?.Table, targetTable, System.StringComparison.OrdinalIgnoreCase))
                spec.Joins.RemoveAt(i);
        }

        spec.Joins.Add(new JoinSpec { Table = targetTable, Kind = "anti" });
        ctx.Diagnostics.Add(new(typeof(InjectAntiJoinFromQuestionPhase).Name,
            $"anti-join: matched '{noun}' → '{targetTable}'; injected {spec.Root} LEFT JOIN {targetTable} WHERE {targetTable}.Id IS NULL"));
    }

    private static bool NameOrSynonymMatches(Semantic.EntityDefinition e, string nounLower)
    {
        if (!string.IsNullOrEmpty(e.Name) && e.Name.ToLowerInvariant() == nounLower) return true;
        if (!string.IsNullOrEmpty(e.Table) && e.Table.ToLowerInvariant() == nounLower) return true;
        // Plural/singular tolerance (drop trailing s).
        if (!string.IsNullOrEmpty(e.Name))
        {
            var n = e.Name.ToLowerInvariant();
            if (n.EndsWith("s") && n[..^1] == nounLower) return true;
            if (n + "s" == nounLower) return true;
        }
        if (!string.IsNullOrEmpty(e.Table))
        {
            var n = e.Table.ToLowerInvariant();
            if (n.EndsWith("s") && n[..^1] == nounLower) return true;
        }
        if (e.Synonyms is { Count: > 0 })
        {
            foreach (var s in e.Synonyms)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (s.ToLowerInvariant() == nounLower) return true;
            }
        }
        return false;
    }

    private static string StripAnnotations(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        return dashIdx >= 0 ? q.Substring(0, dashIdx) : q;
    }
}
