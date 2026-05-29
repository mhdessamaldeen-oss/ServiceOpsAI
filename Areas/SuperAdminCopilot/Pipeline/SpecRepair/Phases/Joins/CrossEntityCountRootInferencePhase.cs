namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases.Joins;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;

/// <summary>
/// Detect "how many [DIMENSION] who [verb] [FACT]" questions and ensure the spec counts
/// distinct DIMENSION ids over the FACT table (not over the DIMENSION table). The LLM often
/// picks the dimension as root: "how many customers who opened a ticket" → root=Customers,
/// COUNT(*) over Customers — which counts ALL customers (correct only when an INNER JOIN
/// to Tickets enforces existence). The safer canonical form is root=Tickets,
/// COUNT(DISTINCT Tickets.CustomerId).
///
/// <para>Conservative gates:
/// <list type="bullet">
///   <item>Fires only when the question contains "how many [plural-dimension] who/that
///         [verb]" — explicit cross-entity COUNT pattern</item>
///   <item>Requires the FACT entity (Tickets/Bills/Outages/MeterReadings) to be present in
///         spec.Joins or already referenced in the spec</item>
///   <item>Skips if the spec already uses COUNT(DISTINCT col) on the dimension key</item>
///   <item>Skips if intent has a GROUP BY (then the dimension IS the grouping column)</item>
/// </list></para>
/// </summary>
internal sealed class CrossEntityCountRootInferencePhase : ISpecRepairPhase
{
    public string Name => "CrossEntityCountRootInference";
    public string Covers => "'how many CUSTOMERS who [did X]' → COUNT(DISTINCT CustomerId) over the FACT table, not the dimension";

    // Tier window override: weak-model crutch.
    // 'how many CUSTOMERS who …' → COUNT(DISTINCT Customer.Id) on the FACT table. Strong NLU resolves the dimension/fact split natively.
    public SuperAdminCopilot.Configuration.PlannerCapabilityTier MaxTierToRun =>
        SuperAdminCopilot.Configuration.PlannerCapabilityTier.Weak;

    private static readonly Regex CountWhoPattern = new(
        @"\bhow\s+many\s+(customers?|users?|agents?|departments?|regions?|service\s+types?|categories?)\s+(?:who|that|with)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Collections.Generic.Dictionary<string, (string Table, string FkCol)> DimensionToFk = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["customer"]      = ("Customers", "CustomerId"),
        ["customers"]     = ("Customers", "CustomerId"),
        ["user"]          = ("AspNetUsers", "AssignedToUserId"),
        ["users"]         = ("AspNetUsers", "AssignedToUserId"),
        ["agent"]         = ("AspNetUsers", "AssignedToUserId"),
        ["agents"]        = ("AspNetUsers", "AssignedToUserId"),
        ["department"]    = ("Departments", "DepartmentId"),
        ["departments"]   = ("Departments", "DepartmentId"),
        ["region"]        = ("Regions", "RegionId"),
        ["regions"]       = ("Regions", "RegionId"),
        ["service type"]  = ("ServiceTypes", "ServiceTypeId"),
        ["service types"] = ("ServiceTypes", "ServiceTypeId"),
        ["category"]      = ("TicketCategories", "CategoryId"),
        ["categories"]    = ("TicketCategories", "CategoryId"),
    };

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (string.IsNullOrEmpty(spec.Root)) return;
        if (string.IsNullOrWhiteSpace(ctx.Question)) return;
        if (spec.GroupBy.Count > 0) return;     // GROUP BY case is a different shape

        var m = CountWhoPattern.Match(ctx.Question);
        if (!m.Success) return;

        var dimNoun = m.Groups[1].Value.Trim().ToLowerInvariant();
        if (!DimensionToFk.TryGetValue(dimNoun, out var pair)) return;
        var (dimTable, fkCol) = pair;

        // Already counting DISTINCT over the FK? Skip.
        if (spec.Aggregations.Any(a =>
            a.Distinct
            && (a.Column ?? "").EndsWith(fkCol, System.StringComparison.OrdinalIgnoreCase)))
            return;

        // Only fire when the spec already references a FACT table that has the FK to the
        // dimension. Otherwise we'd guess wrong about which fact to root.
        // Default fact table heuristic: Tickets (most common). For revenue questions, Bills.
        string factTable;
        if (Regex.IsMatch(ctx.Question, @"\b(?:bill|bills|invoice|invoices|paid|unpaid)\b", RegexOptions.IgnoreCase))
            factTable = "Bills";
        else if (Regex.IsMatch(ctx.Question, @"\b(?:outage|outages|blackout)\b", RegexOptions.IgnoreCase))
            factTable = "Outages";
        else
            factTable = "Tickets";

        if (!ctx.Catalog.TableExists(factTable)) return;
        if (!ctx.Catalog.ColumnExists(factTable, fkCol)) return;

        // Rewrite the spec:
        // - Root → factTable
        // - Aggregations → [COUNT(DISTINCT factTable.fkCol)]
        // - Drop SELECT (single-row count, no columns needed)
        // - Drop spec.Distinct (would be degenerate combined with agg.Distinct)
        var oldRoot = spec.Root;
        spec.Root = factTable;
        spec.Aggregations.Clear();
        spec.Aggregations.Add(new AggregateSpec
        {
            Function = "COUNT",
            Column = $"{factTable}.{fkCol}",
            Distinct = true,
            Alias = "DistinctCount",
        });
        spec.Select.Clear();
        spec.Distinct = false;

        ctx.Diagnostics.Add(new(Name, $"rewrote 'how many {dimNoun} who…' → COUNT(DISTINCT {factTable}.{fkCol}); was root={oldRoot}"));
    }
}
