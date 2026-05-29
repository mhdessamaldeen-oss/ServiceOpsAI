namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>Canonicalise filter/having values via SemanticLayer.ResolveSynonymValue ("urgent" → "Critical").</summary>
internal sealed class MapValueSynonymsPhase : ISpecRepairPhase
{
    public string Name => "MapValueSynonyms";
    public string Covers => "Casual filter phrasings ('urgent', 'pending payment') → canonical DB values";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        int mutated = 0;
        foreach (var f in spec.Filters.NotNull())
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            var current = f.Value?.ToString();
            if (string.IsNullOrEmpty(current)) continue;
            var canonical = ctx.SemanticLayer.ResolveSynonymValue(f.Column, current);
            if (!string.Equals(canonical, current, System.StringComparison.Ordinal)) { f.Value = canonical; mutated++; }
        }
        foreach (var h in spec.Having.NotNull())
        {
            if (string.IsNullOrEmpty(h.Column)) continue;
            var current = h.Value?.ToString();
            if (string.IsNullOrEmpty(current)) continue;
            var canonical = ctx.SemanticLayer.ResolveSynonymValue(h.Column, current);
            if (!string.Equals(canonical, current, System.StringComparison.Ordinal)) { h.Value = canonical; mutated++; }
        }
        if (mutated > 0) ctx.Diagnostics.Add(new(Name, $"mapped {mutated} value(s)"));
    }
}
