namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Filter eq on a searchable name column → like '%value%' (partial-name matching for "Houri" / "Aleppo").</summary>
internal sealed class ConvertNameFilterToLikePhase : ISpecRepairPhase
{
    public string Name => "ConvertNameFilterToLike";
    public string Covers => "Eq on a free-text name column where user typed a partial name";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        var searchableCols = BuildSearchableColumnSet(ctx);
        if (searchableCols.Count == 0) return;

        int mutated = 0;
        foreach (var f in spec.Filters.NotNull())
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            if (!string.Equals(f.Op, "eq", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!searchableCols.Contains(f.Column)) continue;
            var valueStr = f.Value?.ToString();
            if (string.IsNullOrWhiteSpace(valueStr)) continue;
            if (valueStr.Contains('%') || valueStr.Contains('_')) continue;
            if (valueStr.Length > 80) continue;

            f.Op = "like";
            f.Value = $"%{valueStr}%";
            mutated++;
        }
        if (mutated > 0)
            ctx.Diagnostics.Add(new(Name, $"converted {mutated} eq→like on searchable column(s)"));
    }

    private static System.Collections.Generic.HashSet<string> BuildSearchableColumnSet(SpecRepairContext ctx)
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var entities = ctx.SemanticLayer.Config?.Entities;
        if (entities is null) return set;
        foreach (var entity in entities)
        {
            if (string.IsNullOrEmpty(entity.Table)) continue;
            if (entity.SearchableColumns is null) continue;
            foreach (var col in entity.SearchableColumns)
                if (!string.IsNullOrWhiteSpace(col)) set.Add($"{entity.Table}.{col}");
        }
        return set;
    }
}
