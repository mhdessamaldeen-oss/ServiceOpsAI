namespace SuperAdminCopilot.Pipeline.SpecRepair;

/// <summary>Data-only config bound to "SpecRepair" section of spec-repair-rules.json.</summary>
public sealed class SpecRepairOptions
{
    public string AggregationIntentPattern { get; set; } = "";
    public string AggregationSqlPattern { get; set; } = "";
    public string ListIntentPattern { get; set; } = "";
    public System.Collections.Generic.List<string> AntiJoinIntentPatterns { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> SqlComparisonOperatorMap { get; set; } = new();
    public System.Collections.Generic.List<string> QuotedValueDelimiters { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> AggregationFunctionAliases { get; set; } = new();
}
