namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>Hoist inline aggregate expressions ("SUM(x) AS y") out of select[] into aggregations[].</summary>
internal sealed class HoistInlineAggregatesPhase : ISpecRepairPhase
{
    private static readonly Regex InlineAggregate =
        new(@"^\s*(COUNT|SUM|AVG|MIN|MAX|COUNT_BIG)\s*\(\s*(DISTINCT\s+)?(.+?)\s*\)(?:\s+AS\s+\[?([A-Za-z_][A-Za-z0-9_]*)\]?)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingAlias =
        new(@"^\s*(.+?)\s+AS\s+\[?([A-Za-z_][A-Za-z0-9_]*)\]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "HoistInlineAggregates";
    public string Covers => "Inline SUM/COUNT/AVG in select[] move to aggregations[]";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Select.Count == 0) return;
        var keptSelect = new List<string>();
        var hoisted = 0;
        var aliasesStripped = 0;
        foreach (var item in spec.Select)
        {
            if (string.IsNullOrWhiteSpace(item)) { keptSelect.Add(item); continue; }
            var m = InlineAggregate.Match(item);
            if (m.Success)
            {
                spec.Aggregations.Add(new AggregateSpec
                {
                    Function = m.Groups[1].Value.ToUpperInvariant(),
                    Column = m.Groups[3].Value.Trim(),
                    Alias = m.Groups[4].Success ? m.Groups[4].Value : string.Empty,
                    Distinct = m.Groups[2].Success,
                });
                hoisted++;
                continue;
            }
            var am = TrailingAlias.Match(item);
            if (am.Success && !am.Groups[1].Value.Contains('('))
            {
                keptSelect.Add(am.Groups[1].Value.Trim());
                aliasesStripped++;
                continue;
            }
            keptSelect.Add(item);
        }
        if (hoisted == 0 && aliasesStripped == 0) return;
        spec.Select.Clear();
        spec.Select.AddRange(keptSelect);
        var parts = new List<string>();
        if (hoisted > 0) parts.Add($"hoisted {hoisted} aggregate(s)");
        if (aliasesStripped > 0) parts.Add($"stripped {aliasesStripped} alias(es)");
        ctx.Diagnostics.Add(new(Name, string.Join("; ", parts)));
    }
}
