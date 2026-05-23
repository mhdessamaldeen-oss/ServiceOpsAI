namespace SuperAdminCopilot.Explanation;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline;

/// <summary>
/// LLM-driven explainer (§2 stage 8 of the abstraction guide). Asks the model to summarize the
/// query result in one short paragraph, including a citation list of the tables actually
/// referenced by the SQL. Falls back to a templated answer when the LLM call fails or the
/// result is trivial (1-row / 1-column scalar) — no point burning tokens on "Count: 42".
/// </summary>
internal sealed class LlmExplainer : IExplainer
{
    private readonly ILlmClient _llm;
    private readonly TemplatedExplainer _fallback;
    private readonly Pipeline.IRetryBudget _budget;
    private readonly CopilotOptions _options;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<LlmExplainer> _logger;

    private static readonly Regex TableNameRegex = new(@"\[([A-Za-z_][A-Za-z0-9_]*)\]",
        RegexOptions.Compiled);

    public LlmExplainer(
        ILlmClient llm,
        TemplatedExplainer fallback,
        Pipeline.IRetryBudget budget,
        IOptions<CopilotOptions> options,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<LlmExplainer> logger)
    {
        _llm = llm;
        _fallback = fallback;
        _budget = budget;
        _options = options.Value;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    public async Task<ExplainerResult> ExplainAsync(string question, ExecutionResult result, CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        if (!_options.UseLlmExplainer || !string.IsNullOrEmpty(result.Error) || result.RowCount == 0)
            return await _fallback.ExplainAsync(question, result, compiled, cancellationToken);

        // Small results: the templated answer is safer than a small local LLM paraphrase.
        // The explainer was observed rewriting natural keys incorrectly (for example TCK-2026
        // becoming TCK-2020) even while the table data was correct. For small result sets,
        // exact tabular output is the better answer.
        if (result.RowCount <= 3)
            return await _fallback.ExplainAsync(question, result, compiled, cancellationToken);

        // Budget gate — when the planner already burned the budget, fall back to the templated
        // explainer. The user still gets an answer (just without LLM polish) instead of an
        // error after a long wait. Different policy from the planner: explainer failure is
        // graceful, so we degrade rather than throw.
        if (!_budget.TryConsumeLlmCall("Explainer"))
        {
            _logger.LogInformation("[LlmExplainer] Retry budget exhausted; falling back to template.");
            return await _fallback.ExplainAsync(question, result, compiled, cancellationToken);
        }

        var subSteps = new List<PipelineStep>();
        var prepStart = DateTime.UtcNow;
        var citations = ExtractTableCitations(compiled.Sql);
        // Append a per-locale "Reply in <language>" hint so the LLM summary matches the
        // question's language. Detected from the question text; hint string comes from the
        // catalog (hot-reloadable, per-deployment overridable). English fallback when the
        // language can't be confidently identified.
        var lang = Internal.QuestionLanguageDetector.Detect(question);
        var langHint = lang == Internal.QuestionLanguageDetector.Arabic
            ? _textCatalog.CurrentValue.ExplainerLanguageHintAr
            : _textCatalog.CurrentValue.ExplainerLanguageHintEn;
        var systemPrompt = BuildSystemPrompt() + "\n\n" + langHint;
        var userPrompt = BuildUserPrompt(question, result, compiled, citations);
        subSteps.Add(new PipelineStep(
            "Prompt assembly", StageNames.StatusOk,
            ElapsedMs: (long)(DateTime.UtcNow - prepStart).TotalMilliseconds, StartedAt: prepStart,
            Detail: $"system: {systemPrompt.Length} chars, user: {userPrompt.Length} chars, citations: {citations.Count}"));

        var llmStart = DateTime.UtcNow;
        try
        {
            using var hint = SuperAdminCopilot.Abstractions.LlmCallStageHint.Use("Explainer");
            var reply = await _llm.GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);
            reply = (reply ?? "").Trim();
            var llmMs = (long)(DateTime.UtcNow - llmStart).TotalMilliseconds;

            // Emit the LLM-call substep with a typed payload so the investigation tree shows
            // both prompt and response side-by-side (same fidelity the planner already has).
            subSteps.Add(new PipelineStep(
                "LLM call (Explainer)", StageNames.StatusOk, llmMs, llmStart,
                Detail: $"response: {reply.Length} chars",
                TechnicalData: BuildLlmCallPayload(systemPrompt, userPrompt, reply),
                Kind: "llm-call"));

            if (string.IsNullOrEmpty(reply))
            {
                var fallback = await _fallback.ExplainAsync(question, result, compiled, cancellationToken);
                return new ExplainerResult(fallback.Reply, subSteps.Concat(fallback.SubSteps).ToList());
            }

            // Compose the final response: LLM summary + actual data table + citations.
            // The table is critical — small models routinely confuse column names in the prose
            // ("Status Name 'Medium'" when Medium is actually the priority), so we ALWAYS show
            // the raw rows so the user can verify against the LLM's interpretation.
            var sb = new StringBuilder();
            sb.AppendLine(reply);
            sb.AppendLine();
            sb.Append(BuildDataTable(result));
            if (citations.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Citations: ").Append(string.Join(", ", citations.Select(c => $"[{c}]")));
            }
            subSteps.Add(new PipelineStep(
                "Compose final reply", StageNames.StatusOk, ElapsedMs: 0, StartedAt: DateTime.UtcNow,
                Detail: $"reply + data table ({result.RowCount} rows) + {citations.Count} citation(s)"));
            return new ExplainerResult(sb.ToString(), subSteps);
        }
        catch (Exception ex)
        {
            subSteps.Add(new PipelineStep(
                "LLM call (Explainer)", StageNames.StatusFailed,
                ElapsedMs: (long)(DateTime.UtcNow - llmStart).TotalMilliseconds, StartedAt: llmStart,
                Detail: $"failed: {ex.Message} — falling back to templated"));
            _logger.LogWarning(ex, "[SuperAdminCopilot] LLM explainer failed, falling back to template.");
            var fallback = await _fallback.ExplainAsync(question, result, compiled, cancellationToken);
            return new ExplainerResult(fallback.Reply, subSteps.Concat(fallback.SubSteps).ToList());
        }
    }

    /// <summary>Build the typed <see cref="StepPayload"/> for an LLM-call sub-step. Investigation
    /// renderer picks this up and shows labeled Input / Output / Reason sections instead of
    /// dumping the raw text into a single Technical Data box.</summary>
    private static string BuildLlmCallPayload(string systemPrompt, string userPrompt, string raw) =>
        StepPayload.Of(StepPayloadKinds.LlmCall,
            input: userPrompt ?? "",
            output: raw ?? "",
            reason: "LLM call to summarise the result set into one short prose paragraph. The data table is appended to the reply separately so the user can verify the prose against the rows.",
            details: new
            {
                systemPromptLength = systemPrompt?.Length ?? 0,
                promptLength = userPrompt?.Length ?? 0,
                responseLength = raw?.Length ?? 0,
                providerSuccess = !string.IsNullOrEmpty(raw)
            });

    /// <summary>
    /// Renders the result as a markdown table. Caps at 50 rows in the visible table (with a
    /// "showing first 50 of N" footer) so a 1000-row response doesn't blow the chat panel.
    /// Mirrors what the templated explainer used to emit, so the UI doesn't lose its data view
    /// when the LLM explainer is enabled.
    /// </summary>
    private static string BuildDataTable(ExecutionResult result)
    {
        var sb = new StringBuilder();
        sb.Append("**Data (").Append(result.RowCount).Append(" row").Append(result.RowCount == 1 ? "" : "s").AppendLine("):**");
        if (result.Rows.Count == 0)
        {
            sb.AppendLine("_No rows._");
            return sb.ToString();
        }
        var firstRow = result.Rows[0];
        sb.Append("| ").Append(string.Join(" | ", firstRow.Keys.Select(TemplatedExplainer.MarkdownEscape))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", firstRow.Keys.Select(_ => "---"))).AppendLine(" |");
        foreach (var row in result.Rows.Take(50))
        {
            sb.Append("| ");
            sb.Append(string.Join(" | ", row.Values.Select(TemplatedExplainer.MarkdownCellValue)));
            sb.AppendLine(" |");
        }
        if (result.RowCount > 50)
            sb.AppendLine($"_(showing first 50 of {result.RowCount})_");
        return sb.ToString();
    }

    private static List<string> ExtractTableCitations(string sql)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (Match m in TableNameRegex.Matches(sql))
        {
            var name = m.Groups[1].Value;
            // Suppress column-name matches: a real table appears immediately after FROM/JOIN/INTO.
            // Cheap heuristic: include only names that are followed by " AS [" or appear before
            // " ON ". For the simple SQL the compiler emits, this catches the actual tables.
            if (sql.Contains($"FROM [{name}]", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains($"JOIN [{name}]", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(name)) ordered.Add(name);
            }
        }
        return ordered;
    }

    private static string BuildSystemPrompt() =>
@"You are a data-summary assistant. Given a user question, the SQL that ran, and a small sample
of the result rows, write ONE very short paragraph (1–2 sentences) in plain English that answers
the question.

Rules:
- Use only numbers and labels that appear in the result rows. Never invent values.
- COLUMN DISCIPLINE — when a row has multiple columns (e.g. ""StatusName"" AND ""PriorityName""),
  treat them as DISTINCT facts. Never merge a value from one column into the label of another
  (do NOT write ""Status 'Medium'"" when 'Medium' came from a Priority column).
- Be specific BUT SAFE: when uncertain about a column's meaning, say ""across N combinations"" or
  ""in N groups"" instead of guessing labels.
- The data table is shown to the user RIGHT AFTER your summary, so do NOT try to enumerate every
  row — the user can see them. Summarize at a high level: total count, top item, an obvious pattern.
- No code fences. No SQL. No bullet lists. No markdown. Just one short prose paragraph.
- If the result is empty, say so plainly in one sentence.
- Do not preface with 'Based on the data' or 'According to the query'.";

    private static string BuildUserPrompt(string question, ExecutionResult result, CompiledSql compiled, IReadOnlyList<string> citations)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question);
        if (citations.Count > 0)
        {
            sb.Append("Tables touched: ").AppendLine(string.Join(", ", citations));
        }
        sb.AppendLine("SQL:");
        sb.AppendLine(compiled.Sql);
        sb.Append("Row count: ").AppendLine(result.RowCount.ToString());
        sb.AppendLine("Result rows (first 20):");
        var rowsToShow = result.Rows.Take(20).ToList();
        if (rowsToShow.Count > 0)
        {
            sb.Append("| ").Append(string.Join(" | ", rowsToShow[0].Keys)).AppendLine(" |");
            foreach (var row in rowsToShow)
            {
                sb.Append("| ");
                sb.Append(string.Join(" | ", row.Values.Select(v => v?.ToString() ?? "(null)")));
                sb.AppendLine(" |");
            }
        }
        return sb.ToString();
    }
}
