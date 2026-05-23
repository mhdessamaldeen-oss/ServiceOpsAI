namespace SuperAdminCopilot.Validation;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using SuperAdminCopilot.Tools;

public enum CopilotConfigurationIssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record CopilotConfigurationIssue(
    CopilotConfigurationIssueSeverity Severity,
    string Code,
    string Message);

public interface ICopilotConfigurationValidator
{
    Task<IReadOnlyList<CopilotConfigurationIssue>> ValidateAsync(CancellationToken cancellationToken = default);
}

internal sealed class CopilotConfigurationValidator : ICopilotConfigurationValidator
{
    private static readonly Regex ColumnReference = new(
        @"\b(?<table>[A-Za-z_][A-Za-z0-9_]*)\.(?<column>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled);

    private readonly IEntityCatalog _catalog;
    private readonly ISchemaMetadataMap _metadataMap;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IToolRegistry _toolRegistry;

    public CopilotConfigurationValidator(
        IEntityCatalog catalog,
        ISchemaMetadataMap metadataMap,
        ISemanticLayer semanticLayer,
        IToolRegistry toolRegistry)
    {
        _catalog = catalog;
        _metadataMap = metadataMap;
        _semanticLayer = semanticLayer;
        _toolRegistry = toolRegistry;
    }

    public async Task<IReadOnlyList<CopilotConfigurationIssue>> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<CopilotConfigurationIssue>();
        ValidateEntities(issues);
        ValidateMetrics(issues);
        ValidateDimensions(issues);
        ValidateValueSynonyms(issues);
        await ValidateToolsAsync(issues, cancellationToken);
        return issues;
    }

    private void ValidateEntities(List<CopilotConfigurationIssue> issues)
    {
        var mappedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrWhiteSpace(entity.Table))
            {
                Add(issues, CopilotConfigurationIssueSeverity.Error, "entity-table-empty",
                    $"Department '{entity.Name}' has no table mapping.");
                continue;
            }

            mappedTables.Add(entity.Table);
            if (!_catalog.TableExists(entity.Table))
            {
                Add(issues, CopilotConfigurationIssueSeverity.Error, "entity-table-missing",
                    $"Department '{entity.Name}' maps to missing table '{entity.Table}'.");
                continue;
            }

            CheckColumn(issues, entity.Table, entity.NaturalKeyColumn, $"entity '{entity.Name}' natural key");
            CheckColumn(issues, entity.Table, entity.LabelColumn, $"entity '{entity.Name}' label");
            CheckColumn(issues, entity.Table, entity.SoftDeleteColumn, $"entity '{entity.Name}' soft-delete");
            foreach (var column in entity.DisplayColumns)
                CheckColumn(issues, entity.Table, column, $"entity '{entity.Name}' display column");
            foreach (var column in entity.SearchableColumns)
                CheckColumn(issues, entity.Table, column, $"entity '{entity.Name}' searchable column");
            foreach (var column in entity.SensitiveColumns)
                CheckColumn(issues, entity.Table, column, $"entity '{entity.Name}' sensitive column");
            foreach (var role in entity.DateRoles)
                CheckColumn(issues, entity.Table, role.Value, $"entity '{entity.Name}' date role '{role.Key}'");
        }

        var unmapped = _metadataMap.Tables
            .Where(t => !mappedTables.Contains(t.Name))
            .Select(t => t.Name)
            .Take(25)
            .ToList();
        if (unmapped.Count > 0)
        {
            Add(issues, CopilotConfigurationIssueSeverity.Info, "table-unmapped",
                $"{unmapped.Count} database table(s) have no semantic entity mapping in the first 25 checked: {string.Join(", ", unmapped)}.");
        }
    }

    private void ValidateMetrics(List<CopilotConfigurationIssue> issues)
    {
        foreach (var metric in _semanticLayer.Config.Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Expression))
            {
                Add(issues, CopilotConfigurationIssueSeverity.Error, "metric-expression-empty",
                    $"Metric '{metric.Name}' has no expression.");
                continue;
            }

            CheckColumnReferences(issues, metric.Expression, $"metric '{metric.Name}' expression");
            foreach (var filter in metric.Filters)
                CheckQualifiedColumn(issues, filter.Column, $"metric '{metric.Name}' filter");
        }
    }

    private void ValidateDimensions(List<CopilotConfigurationIssue> issues)
    {
        foreach (var dimension in _semanticLayer.Config.Dimensions)
        {
            if (!string.IsNullOrWhiteSpace(dimension.Column))
                CheckQualifiedColumn(issues, dimension.Column, $"dimension '{dimension.Name}' column");
            if (!string.IsNullOrWhiteSpace(dimension.Expression))
                CheckColumnReferences(issues, dimension.Expression, $"dimension '{dimension.Name}' expression");
            if (string.IsNullOrWhiteSpace(dimension.Column) && string.IsNullOrWhiteSpace(dimension.Expression))
                Add(issues, CopilotConfigurationIssueSeverity.Error, "dimension-empty",
                    $"Dimension '{dimension.Name}' has neither a column nor an expression.");
        }
    }

    private void ValidateValueSynonyms(List<CopilotConfigurationIssue> issues)
    {
        foreach (var synonym in _semanticLayer.Config.Synonyms)
        {
            if (!string.Equals(synonym.Type, "value", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(synonym.Context))
            {
                Add(issues, CopilotConfigurationIssueSeverity.Warning, "value-synonym-context-empty",
                    $"Value synonym '{synonym.Canonical}' has no column context.");
                continue;
            }
            CheckQualifiedColumn(issues, synonym.Context, $"value synonym '{synonym.Canonical}' context");
        }
    }

    private async Task ValidateToolsAsync(List<CopilotConfigurationIssue> issues, CancellationToken cancellationToken)
    {
        var tools = await _toolRegistry.GetEnabledAsync(cancellationToken);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ToolKey))
                Add(issues, CopilotConfigurationIssueSeverity.Error, "tool-key-empty", $"Enabled tool '{tool.Title}' has no key.");
            if (string.IsNullOrWhiteSpace(tool.Title))
                Add(issues, CopilotConfigurationIssueSeverity.Warning, "tool-title-empty", $"Enabled tool '{tool.ToolKey}' has no title.");
            if (string.IsNullOrWhiteSpace(tool.EndpointUrl))
                Add(issues, CopilotConfigurationIssueSeverity.Error, "tool-endpoint-empty", $"Enabled tool '{tool.ToolKey}' has no endpoint URL.");
            if (string.IsNullOrWhiteSpace(tool.KeywordHints) && string.IsNullOrWhiteSpace(tool.Description) && string.IsNullOrWhiteSpace(tool.TestPrompt))
                Add(issues, CopilotConfigurationIssueSeverity.Warning, "tool-routing-text-empty",
                    $"Enabled tool '{tool.ToolKey}' has no description, keyword hints, or test prompt for dynamic routing.");
        }
    }

    private void CheckColumnReferences(List<CopilotConfigurationIssue> issues, string expression, string owner)
    {
        foreach (Match match in ColumnReference.Matches(expression))
            CheckColumn(issues, match.Groups["table"].Value, match.Groups["column"].Value, owner);
    }

    private void CheckQualifiedColumn(List<CopilotConfigurationIssue> issues, string qualifiedColumn, string owner)
    {
        var parts = qualifiedColumn.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            Add(issues, CopilotConfigurationIssueSeverity.Warning, "column-reference-unqualified",
                $"{owner} references '{qualifiedColumn}', expected Table.Column.");
            return;
        }
        CheckColumn(issues, parts[0], parts[1], owner);
    }

    private void CheckColumn(List<CopilotConfigurationIssue> issues, string table, string? column, string owner)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (!_catalog.TableExists(table))
        {
            Add(issues, CopilotConfigurationIssueSeverity.Error, "column-table-missing",
                $"{owner} references missing table '{table}'.");
            return;
        }
        if (!_catalog.ColumnExists(table, column))
        {
            Add(issues, CopilotConfigurationIssueSeverity.Error, "column-missing",
                $"{owner} references missing column '{table}.{column}'.");
        }
    }

    private static void Add(
        List<CopilotConfigurationIssue> issues,
        CopilotConfigurationIssueSeverity severity,
        string code,
        string message) =>
        issues.Add(new CopilotConfigurationIssue(severity, code, message));
}
