namespace AnalystAgent.Explanation;

using System.Text;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;
using AnalystAgent.Pipeline;

internal sealed class TemplatedExplainer : IExplainer
{
    public Task<ExplainerResult> ExplainAsync(string question, ExecutionResult result, CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        var subStep = new PipelineStep(
            "Template render", StageNames.StatusOk, ElapsedMs: 0, StartedAt: DateTime.UtcNow,
            Detail: $"templated explainer (no LLM): {result.RowCount} row(s)");
        var subSteps = new[] { subStep };

        if (!string.IsNullOrEmpty(result.Error))
            return Task.FromResult(new ExplainerResult($"Query failed: {result.Error}", subSteps));
        if (result.RowCount == 0)
            return Task.FromResult(new ExplainerResult("No rows matched the query.", subSteps));

        if (result.RowCount == 1 && result.Rows[0].Count == 1)
        {
            var only = result.Rows[0].First();
            return Task.FromResult(new ExplainerResult($"{only.Key}: {only.Value}", subSteps));
        }

        var sb = new StringBuilder();
        // J5 — when the executor stopped reading at MaxRows, the row count is a floor not a total;
        // tell the user the result is partial so they don't read a "50 rows" headline as "this is
        // everything". A second sentence prompts them to refine (filter / limit / pagination is
        // not yet supported).
        if (result.IsTruncated)
            sb.AppendLine($"Returned {result.RowCount} row(s) — result truncated at the MaxRows cap; refine the query to see the rest.").AppendLine();
        else
            sb.AppendLine($"Returned {result.RowCount} row(s).").AppendLine();
        var firstRow = result.Rows[0];
        sb.Append("| ").Append(string.Join(" | ", firstRow.Keys.Select(MarkdownEscape))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", firstRow.Keys.Select(_ => "---"))).AppendLine(" |");
        foreach (var row in result.Rows.Take(50))
        {
            sb.Append("| ");
            sb.Append(string.Join(" | ", row.Values.Select(MarkdownCellValue)));
            sb.AppendLine(" |");
        }
        if (result.RowCount > 50) sb.AppendLine($"_(showing first 50 of {result.RowCount})_");
        return Task.FromResult(new ExplainerResult(sb.ToString(), subSteps));
    }

    /// <summary>Escape a value for inclusion in a markdown table cell. Pipes split columns;
    /// newlines break rows. NULL renders as "(null)" so empty strings stay distinguishable.</summary>
    internal static string MarkdownCellValue(object? v) =>
        v is null ? "(null)" : MarkdownEscape(v.ToString() ?? "");

    internal static string MarkdownEscape(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("|", "\\|")
         .Replace("\r\n", " ")
         .Replace("\r", " ")
         .Replace("\n", " ");
}
