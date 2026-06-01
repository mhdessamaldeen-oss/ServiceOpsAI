namespace SuperAdminCopilot.Application.Repair.Rules;

using System.Collections.Generic;
using System.Linq;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// Targets the AVG/SUM-on-text failure pattern uncovered by the 2026-05-30 deep-dive analyzer
/// (case B-SUB-009: "regions where outages exceed the regional average" → LLM emitted
/// <c>AVG(Outages.OutageNumber)</c>, but <c>OutageNumber</c> is nvarchar like "OUT-2026-04-001",
/// so SQL Server throws "Operand data type nvarchar is invalid for avg operator").
///
/// <para>The rule consults <see cref="Schema.ISchemaView.IsNumericColumn"/> for every AVG / SUM
/// aggregation in the spec; when the target column is non-numeric, the aggregation is REPLACED
/// with a COUNT (which is type-safe). Partial answer beats SQL error. The COUNT preserves the
/// shape of the SQL (still produces a single grouping value) — the LLM's GROUP BY / HAVING stay
/// intact and useful.</para>
///
/// <para>MIN / MAX are NOT touched — they work over strings (alphabetical min/max), which is
/// semantically a reasonable result even if not numeric. Only AVG and SUM are rejected because
/// SQL Server throws on those.</para>
/// </summary>
public sealed class NumericAggregationOnNonNumericRule : IRepairRule
{
    public RepairFaultKind FaultClass => RepairFaultKind.NumericAggregationOnNonNumeric;
    // 2026-06-01 — promoted Medium→Strong (audit). Type-safety LAW, not a crutch: AVG/SUM over an
    // nvarchar column throws a hard SQL error ("Operand data type nvarchar is invalid for avg").
    // Schema-driven (consults IsNumericColumn); fires at every tier to prevent the crash.
    // Predicate-guarded: NoFault unless an AVG/SUM targets a confirmed non-numeric column.
    public PlannerTier MaxTier => PlannerTier.Strong;
    public IReadOnlyList<RepairFaultKind> Requires { get; }
        = new[] { RepairFaultKind.MissingRoot, RepairFaultKind.DanglingColumnReference };

    public Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx)
    {
        if (spec.Aggregations.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);

        var swaps = new List<int>();
        for (int i = 0; i < spec.Aggregations.Count; i++)
        {
            var agg = spec.Aggregations[i];
            var fn = (agg.Function ?? "").ToUpperInvariant();
            if (fn is not ("AVG" or "SUM")) continue;
            if (string.IsNullOrEmpty(agg.Column) || agg.Column == "*") continue;

            var (table, col) = SplitQualified(agg.Column);
            if (string.IsNullOrEmpty(table)) continue;
            if (!ctx.Schema.ColumnExists(table, col)) continue;
            if (ctx.Schema.IsNumericColumn(table, col)) continue;

            swaps.Add(i);
        }

        if (swaps.Count == 0) return Result.Ok<Diagnosis, Fault>(Diagnosis.NoFault);
        var detail = string.Join(", ", swaps.Select(i => $"{spec.Aggregations[i].Function}({spec.Aggregations[i].Column})"));
        return Result.Ok<Diagnosis, Fault>(
            new Diagnosis(FaultClass,
                $"rewriting {swaps.Count} non-numeric aggregation(s) to COUNT(*): {detail}",
                Payload: swaps));
    }

    public QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis)
    {
        if (diagnosis.Payload is not List<int> indices || indices.Count == 0) return spec;
        foreach (var i in indices)
        {
            if (i < 0 || i >= spec.Aggregations.Count) continue;
            var original = spec.Aggregations[i];
            // Replace AVG/SUM(nvarchar-col) with COUNT(*). Preserve the alias so downstream
            // SELECT / ORDER BY / HAVING references that named the aggregation still bind.
            var alias = string.IsNullOrEmpty(original.Alias) ? "Count" : original.Alias;
            spec.Aggregations[i] = new AggregateSpec
            {
                Function = "COUNT",
                Column = "*",
                Alias = alias,
                Distinct = false,
            };
        }
        return spec;
    }

    private static (string Table, string Column) SplitQualified(string qualified)
    {
        if (string.IsNullOrEmpty(qualified)) return ("", "");
        var idx = qualified.IndexOf('.');
        if (idx < 0) return ("", qualified);
        return (qualified.Substring(0, idx), qualified.Substring(idx + 1));
    }
}
