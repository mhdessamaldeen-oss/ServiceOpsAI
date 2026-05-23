using System.Text.RegularExpressions;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Canonical schema vocabulary for the approved copilot data catalog.
    /// The catalog chooses which values are enabled, while this schema defines which values are valid at all.
    /// </summary>
    public static class CopilotDataCatalogSchema
    {
        public static readonly string[] DefaultOutputShapes =
        [
            "Metric",
            "Table",
            "Detail",
            "Mixed"
        ];

        private static readonly HashSet<string> SupportedOutputShapes = new(DefaultOutputShapes, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SupportedFieldCapabilities = new(StringComparer.OrdinalIgnoreCase)
        {
            "filter",
            "sort",
            "group",
            "aggregate",
            "display"
        };

        private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "Count",
            "List",
            "Detail",
            "Breakdown",
            "Compare",
            "Rank"
        };

        private static readonly HashSet<string> SupportedOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "Equals",
            "Contains",
            "In",
            "Between",
            "GreaterThan",
            "LessThan"
        };

        private static readonly HashSet<string> SupportedAggregations = new(StringComparer.OrdinalIgnoreCase)
        {
            "Count",
            "Sum",
            "Avg",
            "Min",
            "Max"
        };

        private static readonly Regex SqlIdentifierPattern = new(
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsSupportedOutputShape(string? value)
            => !string.IsNullOrWhiteSpace(value) && SupportedOutputShapes.Contains(value);

        public static bool IsSupportedFieldCapability(string? value)
            => !string.IsNullOrWhiteSpace(value) && SupportedFieldCapabilities.Contains(value);

        public static bool IsSupportedOperation(string? value)
            => !string.IsNullOrWhiteSpace(value) && SupportedOperations.Contains(value);

        public static bool IsSupportedOperator(string? value)
            => !string.IsNullOrWhiteSpace(value) && SupportedOperators.Contains(value);

        public static bool IsSupportedAggregation(string? value)
            => !string.IsNullOrWhiteSpace(value) && SupportedAggregations.Contains(value);

        public static bool IsSafeIdentifier(string? value)
            => !string.IsNullOrWhiteSpace(value) && SqlIdentifierPattern.IsMatch(value);
    }
}
