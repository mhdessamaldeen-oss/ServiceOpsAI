namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// When the question's main verb signals a specific lifecycle event ("created", "opened",
/// "resolved", "closed", "started", "ended", "issued", "paid", "signed up") AND the LLM
/// emitted a date filter on the WRONG role's column, swap to the verb-implied column.
///
/// <para>Catches the common silent-failure where the LLM picks a default date column instead
/// of the lifecycle-correct one. Example: A-CMP-008 "ticket volume this year vs last year" —
/// the LLM emitted <c>WHERE YEAR(ResolvedAt) = YEAR(GETDATE())</c>, but "volume" is a creation
/// metric, so the right column is <c>CreatedAt</c>.</para>
///
/// <para>Routing is semantic-layer-driven: each entity declares <c>dateRoles</c>
/// (created/resolved/started/ended/etc.). The phase looks up the verb's role on the spec's
/// root entity and swaps any filter/orderby/groupby reference from one role's column to
/// the verb-implied column. Skip when the question contains no verb cue or when the role
/// isn't declared on the entity.</para>
/// </summary>
internal sealed class SwapDateColumnByVerbPhase : ISpecRepairPhase
{
    public string Name => "SwapDateColumnByVerb";
    public string Covers => "Question verb implies a specific date role (resolved/closed/created/issued/etc.) → swap any date filter/orderby to the verb's column";

    // Tier window override: weak-model crutch.
    // Lifecycle verb → date role (resolved / closed / issued / paid). Strong NLU picks the right date column; Medium needs the hint.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Medium;

    // Maps verb keyword → semantic-layer role. "Volume" and "count" don't imply a role —
    // they default to creation, which most entities already use as default. We only fire on
    // explicit lifecycle verbs.
    private static readonly (Regex Pattern, string Role)[] VerbToRole = new[]
    {
        (new Regex(@"\b(?:resolved|fixed|completed|done)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "resolved"),
        (new Regex(@"\b(?:closed|closure|shut)\b",            RegexOptions.IgnoreCase | RegexOptions.Compiled), "closed"),
        (new Regex(@"\b(?:created|opened|filed|submitted|reported|raised|new(?:ly)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "created"),
        (new Regex(@"\b(?:started|began|launched|initiated|onset)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "started"),
        (new Regex(@"\b(?:ended|finished|stopped|terminated|cleared|restored)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ended"),
        (new Regex(@"\b(?:issued|billed|sent)\b",             RegexOptions.IgnoreCase | RegexOptions.Compiled), "issued"),
        (new Regex(@"\b(?:paid|settled)\b",                   RegexOptions.IgnoreCase | RegexOptions.Compiled), "paid"),
        (new Regex(@"\b(?:signed\s+up|registered|joined|enrolled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "signup"),
        (new Regex(@"\b(?:uploaded)\b",                       RegexOptions.IgnoreCase | RegexOptions.Compiled), "uploaded"),
    };

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;

        var q = ctx.Question;
        var dashIdx = q.IndexOf("\n--", System.StringComparison.Ordinal);
        if (dashIdx >= 0) q = q.Substring(0, dashIdx);

        string? matchedRole = null;
        foreach (var (rx, role) in VerbToRole)
        {
            if (rx.IsMatch(q)) { matchedRole = role; break; }
        }
        if (matchedRole is null) return;

        var targetCol = ctx.SemanticLayer.GetDateColumn(spec.Root, matchedRole);
        if (string.IsNullOrEmpty(targetCol)) return;
        if (!ctx.Catalog.ColumnExists(spec.Root, targetCol!)) return;
        var targetQualified = $"{spec.Root}.{targetCol}";

        // Enumerate the entity's other date columns. We swap filters/orderby/groupby that
        // reference one of those OTHER date columns to the target. We don't touch references
        // that already point to the target.
        var otherDateCols = ctx.Catalog.GetColumns(spec.Root)
            .Where(c =>
                IsDateLike(c.DataType)
                && !string.Equals(c.ColumnName, targetCol, System.StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ColumnName)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        if (otherDateCols.Count == 0) return;

        var swaps = 0;
        foreach (var f in spec.Filters.NotNull())
        {
            var (tbl, col) = SplitQualified(f.Column);
            if (!string.Equals(tbl, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!otherDateCols.Contains(col)) continue;
            f.Column = targetQualified;
            swaps++;
        }
        foreach (var o in spec.OrderBy.NotNull())
        {
            var (tbl, col) = SplitQualified(o.Column);
            if (!string.Equals(tbl, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!otherDateCols.Contains(col)) continue;
            o.Column = targetQualified;
            swaps++;
        }
        for (int i = 0; i < spec.GroupBy.Count; i++)
        {
            var (tbl, col) = SplitQualified(spec.GroupBy[i]);
            if (!string.Equals(tbl, spec.Root, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!otherDateCols.Contains(col)) continue;
            spec.GroupBy[i] = targetQualified;
            swaps++;
        }
        if (swaps > 0)
            ctx.Diagnostics.Add(new(Name, $"swapped {swaps} date-column reference(s) to '{targetQualified}' (verb '{matchedRole}' implied)"));
    }

    private static bool IsDateLike(string? sqlType)
    {
        if (string.IsNullOrEmpty(sqlType)) return false;
        var t = sqlType!.ToLowerInvariant();
        return t.StartsWith("date", System.StringComparison.Ordinal)
            || t.StartsWith("smalldatetime", System.StringComparison.Ordinal);
    }

    private static (string Table, string Column) SplitQualified(string? qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx <= 0) return ("", qualified.Trim('[', ']'));
        return (qualified.Substring(0, idx).Trim('[', ']'),
                qualified.Substring(idx + 1).Trim('[', ']'));
    }
}
