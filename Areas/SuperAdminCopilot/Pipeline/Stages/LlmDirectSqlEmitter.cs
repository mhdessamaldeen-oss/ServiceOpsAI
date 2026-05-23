namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

// Escape valve for question shapes the form-filling QuerySpec can't express: window
// functions (running totals, ranks, percentiles), recursive CTEs, complex multi-CTE
// analytics, etc. Activated as a LAST-RESORT fallback when the standard
// SpecExtractor -> Compiler -> Executor loop exhausts its retries without producing a
// runnable query. Output is raw T-SQL fed straight to the executor; the existing
// SqlAstValidator + ReadOnlyExecutor read-only guard remain the safety net (no DML,
// no multi-statement, no DDL — same guarantees as the form-filling path).
public interface ILlmDirectSqlEmitter
{
    Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken = default);
}

public sealed record DirectSqlResult(string? Sql, string? Error, string? Prompt, string? RawLlmOutput);

internal sealed class LlmDirectSqlEmitter : ILlmDirectSqlEmitter
{
    private readonly ILlmClient _llm;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<LlmDirectSqlEmitter> _logger;

    public LlmDirectSqlEmitter(
        ILlmClient llm,
        ISchemaKnowledge knowledge,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<LlmDirectSqlEmitter> logger)
    {
        _llm = llm;
        _knowledge = knowledge;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    public async Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new DirectSqlResult(null, "empty question", null, null);
        if (candidateTableNames is null || candidateTableNames.Count == 0)
            return new DirectSqlResult(null, "no candidate tables", null, null);

        var tables = candidateTableNames
            .Select(n => _knowledge.GetTable(n))
            .Where(t => t is not null)
            .Cast<InferredTable>()
            .ToList();
        if (tables.Count == 0)
            return new DirectSqlResult(null, "candidate tables not found in schema knowledge", null, null);

        var prompt = BuildPrompt(question, tables);
        string raw;
        try
        {
            using var hint = LlmCallStageHint.Use("LlmDirectSqlEmitter");
            var systemPrompt = _textCatalog.CurrentValue.DirectSqlSystemPrompt;
            raw = await _llm.GenerateTextAsync(systemPrompt, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LlmDirectSqlEmitter] LLM call failed");
            return new DirectSqlResult(null, ex.Message, prompt, null);
        }

        var sql = ExtractSql(raw);
        if (string.IsNullOrWhiteSpace(sql))
            return new DirectSqlResult(null, "no SQL extracted from LLM output", prompt, raw);

        return new DirectSqlResult(sql, null, prompt, raw);
    }

    // Build the user prompt: table list + question. Mirrors the SpecExtractor's table
    // rendering so the LLM sees the same schema slice.
    private static string BuildPrompt(string question, IReadOnlyList<InferredTable> tables)
    {
        var sb = new StringBuilder();
        sb.Append("Question: \"").Append(question).AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("Available tables:");
        foreach (var t in tables)
        {
            sb.Append("- ").Append(t.Name);
            if (!string.IsNullOrEmpty(t.Description)) sb.Append(" — ").Append(t.Description);
            sb.AppendLine();
            sb.AppendLine("  Columns:");
            foreach (var c in t.Columns)
            {
                sb.Append("    ").Append(t.Name).Append('.').Append(c.Name)
                    .Append(" (").Append(c.Type);
                if (c.Nullable) sb.Append(", nullable");
                sb.AppendLine(")");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Output ONLY the SELECT statement. No commentary, no markdown fence.");
        return sb.ToString();
    }

    // Strip code fences and explanation. Accept the first SELECT statement we can find.
    private static string? ExtractSql(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Strip ```sql ... ``` or ``` ... ``` fences if the model wraps despite the
        // system prompt's no-markdown instruction (qwen2.5-coder routinely does).
        var fence = Regex.Match(raw, "```(?:sql|tsql)?\\s*(.+?)\\s*```", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var candidate = fence.Success ? fence.Groups[1].Value : raw;

        // Trim leading prose. SELECT is the only valid start (executor rejects others).
        var selectIdx = candidate.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        if (selectIdx < 0) return null;
        var sql = candidate.Substring(selectIdx).Trim();

        // Drop any trailing prose after the final semicolon.
        var lastSemi = sql.LastIndexOf(';');
        if (lastSemi > 0 && lastSemi < sql.Length - 1)
            sql = sql.Substring(0, lastSemi + 1);

        return sql.Length > 0 ? sql : null;
    }
}
