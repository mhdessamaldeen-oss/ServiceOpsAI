namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// Post-Explainer verification pass. Catches the class of failures where the pipeline
/// COMPLETED but the answer doesn't fully address the user's question — e.g. a compound
/// question where the decomposer didn't split, or the LLM picked a wrong column so the
/// result data doesn't carry the asked-for dimension.
/// </summary>
/// <remarks>
/// <para>Entity-agnostic: the verifier is just an LLM that reads (question, SQL, result
/// columns + first row, reply) and emits either <c>COMPLETE</c> or a list of question
/// aspects that the answer fails to address. No per-pattern rules, no per-entity logic.</para>
/// <para><b>Configurability</b>: the system prompt is sourced from
/// <see cref="CopilotTextCatalog.CoverageCheckerSystemPrompt"/> (hot-reloadable). Operators
/// can override per deployment without recompiling — and the default prompt uses schema-agnostic
/// placeholders (<c>&lt;X&gt;</c>, <c>&lt;Y&gt;</c>) so it works for any database.</para>
/// <para><b>Budget</b>: separate per-question counter (not shared with planner/explainer retry
/// budget). The auditor is non-critical and runs at most <see cref="CopilotOptions.CoverageCheckMaxCallsPerQuestion"/>
/// times per question; default 1. Keeps verification reliable on hard questions where the main
/// budget might already be exhausted.</para>
/// </remarks>
public interface ICoverageChecker
{
    /// <summary>Returns null on success (COMPLETE); a <see cref="CoverageGap"/> when the
    /// LLM identifies missing aspects of the question.</summary>
    Task<CoverageGap?> CheckAsync(
        string question,
        string? sql,
        ExecutionResult? result,
        string reply,
        CancellationToken cancellationToken = default);
}

/// <summary>Coverage failure description. <see cref="Missing"/> is the human-readable list
/// of aspects the answer fails to address. <see cref="Prompt"/> / <see cref="RawLlmOutput"/>
/// are kept for trace observability.</summary>
public sealed record CoverageGap(string Missing, string? Prompt, string? RawLlmOutput);

internal sealed class CoverageChecker : ICoverageChecker
{
    private readonly ILlmClient _llm;
    private readonly IOptions<CopilotOptions> _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<CoverageChecker> _logger;

    /// <summary>
    /// Per-request call counter — entirely separate from <see cref="Pipeline.IRetryBudget"/>.
    /// Reset implicitly because <see cref="CoverageChecker"/> is registered Scoped, so a new
    /// instance is constructed per HTTP request. Field counter is sufficient — sub-questions
    /// run in parallel but each gets its own DI scope.
    /// </summary>
    private int _callsThisQuestion;

    public CoverageChecker(
        ILlmClient llm,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<CoverageChecker> logger)
    {
        _llm = llm;
        _options = options;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    public async Task<CoverageGap?> CheckAsync(
        string question,
        string? sql,
        ExecutionResult? result,
        string reply,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Value.EnableCoverageChecker) return null;
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(reply)) return null;

        // Separate budget — not shared with the main retry budget. Keeps verification reliable
        // on hard questions where the planner already burned its budget.
        var maxCalls = Math.Max(0, _options.Value.CoverageCheckMaxCallsPerQuestion);
        if (maxCalls == 0) return null;
        if (System.Threading.Interlocked.Increment(ref _callsThisQuestion) > maxCalls)
        {
            _logger.LogInformation("[CoverageChecker] Coverage budget exhausted ({Max} calls); skipping.", maxCalls);
            return null;
        }

        var systemPrompt = _textCatalog.CurrentValue.CoverageCheckerSystemPrompt;
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            _logger.LogWarning("[CoverageChecker] System prompt is empty in the text catalog; skipping coverage check.");
            return null;
        }

        var userPrompt = BuildUserPrompt(question, sql, result, reply);
        string raw;
        try
        {
            using var hint = LlmCallStageHint.Use("CoverageCheck");
            raw = (await _llm.GenerateTextAsync(systemPrompt, userPrompt, cancellationToken) ?? "").Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CoverageChecker] LLM call failed; treating as COMPLETE (graceful).");
            return null;
        }

        // Parse the strict-shape output. Anything that doesn't start with MISSING: is treated
        // as COMPLETE — err on the side of NOT degrading good answers.
        if (string.IsNullOrEmpty(raw)) return null;
        var line = raw.Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (line.StartsWith("MISSING:", StringComparison.OrdinalIgnoreCase))
        {
            var missing = line.Substring("MISSING:".Length).Trim();
            if (string.IsNullOrWhiteSpace(missing)) return null;
            return new CoverageGap(missing, userPrompt, raw);
        }
        return null; // COMPLETE (or unexpected — same effect)
    }

    private static string BuildUserPrompt(string question, string? sql, ExecutionResult? result, string reply)
    {
        var sb = new StringBuilder();
        sb.Append("QUESTION: ").AppendLine(question);
        sb.Append("SQL: ").AppendLine(string.IsNullOrEmpty(sql) ? "(none — non-SQL route)" : sql);
        if (result is not null)
        {
            sb.Append("ROW COUNT: ").AppendLine(result.RowCount.ToString());
            if (result.Rows is { Count: > 0 } rows)
            {
                var first = rows[0];
                sb.Append("COLUMNS: ").AppendLine(string.Join(", ", first.Keys));
                sb.Append("FIRST ROW: ").AppendLine(JsonSerializer.Serialize(first));
            }
        }
        sb.AppendLine("REPLY:");
        sb.AppendLine(reply.Length > 2000 ? reply.Substring(0, 2000) + "…" : reply);
        return sb.ToString();
    }
}
