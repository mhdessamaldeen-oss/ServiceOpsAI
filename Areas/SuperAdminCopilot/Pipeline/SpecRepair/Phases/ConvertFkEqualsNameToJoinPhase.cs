namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

/// <summary>FK eq string → JOIN target + eq on target's label column ("CustomerId='Houri'" → "Customers.FullNameEn='Houri'").</summary>
internal sealed class ConvertFkEqualsNameToJoinPhase : ISpecRepairPhase
{
    public string Name => "ConvertFkEqualsNameToJoin";
    public string Covers => "WHERE FK_Id = 'name' → join target + filter on target's label column";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (spec.Filters.Count == 0) return;
        int mutated = 0;
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrEmpty(f.Column)) continue;
            if (!string.Equals(f.Op, "eq", System.StringComparison.OrdinalIgnoreCase)) continue;
            var valueStr = f.Value?.ToString();
            if (string.IsNullOrWhiteSpace(valueStr)) continue;
            if (decimal.TryParse(valueStr, out _)) continue;
            if (System.Guid.TryParse(valueStr, out _)) continue;

            var dot = f.Column.IndexOf('.');
            if (dot <= 0) continue;
            var sourceTable = f.Column[..dot];
            var sourceColumn = f.Column[(dot + 1)..];

            var candidate = FindCandidate(ctx, sourceTable);
            if (candidate is null) continue;
            var fk = candidate.ForeignKeysOut?.FirstOrDefault(r =>
                string.Equals(r.Column, sourceColumn, System.StringComparison.OrdinalIgnoreCase));
            if (fk is null) continue;

            var targetEntity = ctx.SemanticLayer.GetEntityForTable(fk.Table);
            var labelCol = !string.IsNullOrEmpty(targetEntity?.LabelColumn)
                ? targetEntity!.LabelColumn!
                : null;
            if (labelCol is null) continue;
            if (!ctx.Catalog.ColumnExists(fk.Table, labelCol)) continue;

            f.Column = $"{fk.Table}.{labelCol}";
            mutated++;
            if (!spec.Joins.Any(j => string.Equals(j.Table, fk.Table, System.StringComparison.OrdinalIgnoreCase)))
                spec.Joins.Add(new JoinSpec { Table = fk.Table, Kind = "inner" });
        }
        if (mutated > 0)
            ctx.Diagnostics.Add(new(Name, $"redirected {mutated} FK eq filter(s) to target label column"));
    }

    private static InferredTable? FindCandidate(SpecRepairContext ctx, string tableName)
    {
        foreach (var t in ctx.CandidateTables)
            if (string.Equals(t.Name, tableName, System.StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
}
