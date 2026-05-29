namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>Fallback root inference: first qualified Table.Column ref found in aggregations/select/groupBy/filters.</summary>
internal sealed class InferRootFromColumnRefsPhase : ISpecRepairPhase
{
    private static readonly Regex TableHint = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\.[A-Za-z_]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "InferRootFromColumnRefs";
    public string Covers => "Fallback root inference from qualified column refs";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.Root)) return;
        string? hint = null;
        foreach (var a in spec.Aggregations.NotNull()) { hint ??= Extract(a.Column); if (hint is not null) break; }
        foreach (var s in spec.Select.NotNull()) { hint ??= Extract(s); if (hint is not null) break; }
        foreach (var g in spec.GroupBy.NotNull()) { hint ??= Extract(g); if (hint is not null) break; }
        foreach (var f in spec.Filters.NotNull()) { hint ??= Extract(f.Column); if (hint is not null) break; }
        if (hint is not null)
        {
            spec.Root = hint;
            ctx.Diagnostics.Add(new(Name, $"set root={hint} (from column ref)"));
        }
    }

    private static string? Extract(string? colRef)
    {
        if (string.IsNullOrWhiteSpace(colRef)) return null;
        var m = TableHint.Match(colRef);
        return m.Success ? m.Groups[1].Value : null;
    }
}
