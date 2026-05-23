namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

public interface IQuerySpecAccessPolicyValidator
{
    string? Check(QuerySpec spec);
}

internal sealed class QuerySpecAccessPolicyValidator : IQuerySpecAccessPolicyValidator
{
    private static readonly Regex QualifiedRefPattern = new(
        @"(?<![\w])\[?(?<table>[A-Za-z_][A-Za-z0-9_]*)\]?\s*\.\s*\[?(?<column>[A-Za-z_][A-Za-z0-9_]*)\]?",
        RegexOptions.Compiled);
    private static readonly Regex FunctionCallPattern = new(
        @"\b(?<fn>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex UnsafeSqlKeywordPattern = new(
        @"\b(select|from|join|apply|union|intersect|except|insert|update|delete|merge|drop|alter|create|exec|execute|truncate|into|openrowset|openquery)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedExpressionFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Date / time
        "DATEDIFF", "DATEADD", "GETDATE", "GETUTCDATE", "YEAR", "MONTH", "DAY",
        "FORMAT", "DATEFROMPARTS", "DATETIMEFROMPARTS", "EOMONTH", "DATEPART", "DATENAME",
        // Type conversion / null handling
        "ABS", "COALESCE", "CAST", "CONVERT", "ISNULL", "NULLIF", "IIF",
        // Aggregates
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // Window functions
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
        "FIRST_VALUE", "LAST_VALUE", "OVER", "PARTITION",
        // Text / string helpers
        "LEN", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "LEFT", "RIGHT",
        "SUBSTRING", "CHARINDEX", "PATINDEX", "REPLACE", "CONCAT", "CONCAT_WS",
        // List / set operators that look like function calls to the regex
        "IN", "NOT_IN",
    };

    private readonly ICopilotSchemaAccessPolicy _policy;

    public QuerySpecAccessPolicyValidator(ICopilotSchemaAccessPolicy policy)
    {
        _policy = policy;
    }

    public string? Check(QuerySpec spec)
    {
        if (spec is null) return "empty query spec";

        if (!string.IsNullOrWhiteSpace(spec.Root) && !_policy.IsTableAllowed(spec.Root))
            return $"root table '{spec.Root}' is blocked by Copilot settings";

        foreach (var join in spec.Joins ?? new())
        {
            if (!string.IsNullOrWhiteSpace(join.Table) && !_policy.IsTableAllowed(join.Table))
                return $"join table '{join.Table}' is blocked by Copilot settings";
        }

        foreach (var item in spec.Select ?? new())
        {
            var issue = CheckColumnReference(item, "select");
            if (issue is not null) return issue;
        }

        foreach (var filter in spec.Filters ?? new())
        {
            if (string.Equals(filter.Op, "text_search", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(filter.Column)
                && !filter.Column.Contains('.', StringComparison.Ordinal)
                && !_policy.IsTableAllowed(filter.Column))
                return $"text_search table '{filter.Column}' is blocked by Copilot settings";

            var issue = CheckColumnReference(filter.Column, "filter");
            if (issue is not null) return issue;
        }

        foreach (var group in spec.GroupBy ?? new())
        {
            var issue = CheckColumnReference(group, "groupBy");
            if (issue is not null) return issue;
        }

        foreach (var order in spec.OrderBy ?? new())
        {
            var issue = CheckColumnReference(order.Column, "orderBy");
            if (issue is not null) return issue;
        }

        foreach (var agg in spec.Aggregations ?? new())
        {
            var aliasIssue = CheckAlias(agg.Alias, "aggregation alias");
            if (aliasIssue is not null) return aliasIssue;
            if (string.Equals(agg.Column, "*", StringComparison.Ordinal)) continue;
            if (agg.Column?.StartsWith("expr:", StringComparison.OrdinalIgnoreCase) == true)
            {
                var exprIssue = CheckExpressionSafety(agg.Column["expr:".Length..], "aggregation expression");
                if (exprIssue is not null) return exprIssue;
            }
            var issue = CheckColumnReference(agg.Column, "aggregation");
            if (issue is not null) return issue;
        }

        foreach (var having in spec.Having ?? new())
        {
            if (string.Equals(having.Column, "*", StringComparison.Ordinal)) continue;
            var issue = CheckColumnReference(having.Column, "having");
            if (issue is not null) return issue;
        }

        foreach (var computed in spec.Computed ?? new())
        {
            var aliasIssue = CheckAlias(computed.Alias, "computed alias");
            if (aliasIssue is not null) return aliasIssue;
            var exprIssue = CheckExpressionSafety(computed.Expression, "computed expression");
            if (exprIssue is not null) return exprIssue;
            var issue = CheckColumnReference(computed.Expression, "computed");
            if (issue is not null) return issue;
        }

        return null;
    }

    private static string? CheckAlias(string? alias, string context)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;
        if (alias.Contains(']') || alias.Contains('[') || alias.Contains(';') ||
            alias.Contains("--", StringComparison.Ordinal) ||
            alias.Contains("/*", StringComparison.Ordinal) ||
            alias.Contains("*/", StringComparison.Ordinal))
        {
            return $"{context} contains unsupported identifier characters";
        }

        return null;
    }

    private static string? CheckExpressionSafety(string? expression, string context)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        if (expression.Contains(';') ||
            expression.Contains("--", StringComparison.Ordinal) ||
            expression.Contains("/*", StringComparison.Ordinal) ||
            expression.Contains("*/", StringComparison.Ordinal) ||
            expression.Contains("@@", StringComparison.Ordinal))
        {
            return $"{context} contains unsupported SQL control syntax";
        }

        if (UnsafeSqlKeywordPattern.IsMatch(expression))
            return $"{context} contains unsupported SQL query/control keyword";

        foreach (Match match in FunctionCallPattern.Matches(expression))
        {
            var fn = match.Groups["fn"].Value;
            if (!AllowedExpressionFunctions.Contains(fn))
                return $"{context} uses unsupported function '{fn}'";
        }

        return null;
    }

    private string? CheckColumnReference(string? reference, string context)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("dimension:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!reference.Contains('.', StringComparison.Ordinal)) return null;

        foreach (Match match in QualifiedRefPattern.Matches(reference))
        {
            var table = match.Groups["table"].Value;
            var column = match.Groups["column"].Value;

            if (!_policy.IsTableAllowed(table))
                return $"{context} references blocked table '{table}'";
            if (!_policy.IsColumnAllowed(table, column))
                return $"{context} references blocked column '{table}.{column}'";
        }

        return null;
    }
}
