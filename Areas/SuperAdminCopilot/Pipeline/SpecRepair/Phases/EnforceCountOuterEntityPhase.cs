namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>"how many X have Y" → swap root to X, force COUNT(DISTINCT X.Id). LLM tends to pick Y and count Y.</summary>
internal sealed class EnforceCountOuterEntityPhase : ISpecRepairPhase
{
    private static readonly Regex HowManyHaveRegex = new(
        @"^\s*(?:how\s+many|count\s+(?:of\s+)?|number\s+of)\s+([a-zA-Z]+)\s+(?:have|with|that\s+have|that\s+contain|having|who\s+have|whose)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "EnforceCountOuterEntity";
    public string Covers => "'how many X have Y' counts Y instead of distinct X";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        var m = HowManyHaveRegex.Match(ctx.Question);
        if (!m.Success) return;

        var outerEntityName = m.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(outerEntityName)) return;

        var outerEntity = ctx.SemanticLayer.GetEntityByNameOrSynonym(outerEntityName);
        if (outerEntity is null || string.IsNullOrEmpty(outerEntity.Table)) return;
        var outerTable = outerEntity.Table;
        if (!ctx.Catalog.TableExists(outerTable)) return;
        if (string.Equals(spec.Root, outerTable, System.StringComparison.OrdinalIgnoreCase)) return;

        bool looksLikeCount =
            spec.Aggregations.Any(a => string.Equals(a.Function, "COUNT", System.StringComparison.OrdinalIgnoreCase))
            || spec.Select.Count == 0;
        if (!looksLikeCount) return;

        var previousRoot = spec.Root;
        spec.Root = outerTable;
        spec.Aggregations.Clear();
        spec.Select.Clear();
        spec.GroupBy.Clear();
        spec.Aggregations.Add(new AggregateSpec
        {
            Function = "COUNT",
            Column = $"{outerTable}.Id",
            Alias = $"{outerEntity.Name}Count",
            Distinct = true,
        });
        ctx.Diagnostics.Add(new(Name, $"swapped root {previousRoot}→{outerTable} + COUNT(DISTINCT)"));
    }
}
