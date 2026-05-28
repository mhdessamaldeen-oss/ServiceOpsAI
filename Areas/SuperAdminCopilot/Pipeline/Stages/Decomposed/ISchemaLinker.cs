namespace SuperAdminCopilot.Pipeline.Stages.Decomposed;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Services.AI.Providers.Roles;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Schema;

/// <summary>
/// Phase 2a — explicit schema-linking stage. Takes the question plus a candidate schema
/// slice (already retrieved by the existing <see cref="ISchemaSemanticRetriever"/>) and asks
/// the LLM to pick the MINIMAL subset of tables, columns, and joins needed to answer the
/// question.
///
/// <para><b>SOTA reference:</b> DIN-SQL (arXiv 2304.11015) and CHESS (arXiv 2405.16755) both
/// treat schema-linking as its own stage and report substantial accuracy gains over
/// monolithic prompts. The reason is that schema-linking is a *classification* problem
/// (which columns?) while SQL generation is a *generation* problem; mixing them confuses
/// smaller models.</para>
///
/// <para><b>Staged, dormant.</b> Not registered in DI in this commit.</para>
/// </summary>
public interface ISchemaLinker
{
    Task<SchemaLinkResult> LinkAsync(
        string question,
        IReadOnlyList<RetrievedTableSummary> candidateTables,
        CancellationToken cancellationToken = default);
}

/// <summary>A retrieved candidate table summary — kept local to this stage so the
/// SchemaLinker doesn't depend on the existing retriever's record types directly.
/// The orchestrator adapts whatever retriever output it has into this shape.</summary>
public sealed record RetrievedTableSummary(
    string Table,
    IReadOnlyList<string> Columns,
    string? Description);

public sealed record SchemaLinkResult
{
    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RequiredColumn> RequiredColumns { get; init; } = Array.Empty<RequiredColumn>();
    public IReadOnlyList<JoinHint> JoinHints { get; init; } = Array.Empty<JoinHint>();
    public string? RawLlmOutput { get; init; }
    public string? Error { get; init; }
}

public sealed record RequiredColumn(string Table, string Column, string Reason);
public sealed record JoinHint(string From, string To, string Via);

internal sealed class SchemaLinker : ISchemaLinker
{
    private readonly IRoleBoundLlmClientFactory _llmFactory;
    private readonly IDecomposedPromptProvider _prompts;
    private readonly ILogger<SchemaLinker> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SchemaLinker(
        IRoleBoundLlmClientFactory llmFactory,
        IDecomposedPromptProvider prompts,
        ILogger<SchemaLinker> logger)
    {
        _llmFactory = llmFactory;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<SchemaLinkResult> LinkAsync(
        string question,
        IReadOnlyList<RetrievedTableSummary> candidateTables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new SchemaLinkResult { Error = "empty question" };
        if (candidateTables.Count == 0)
            return new SchemaLinkResult { Error = "no candidate tables to link against" };

        var schemaSlice = BuildSchemaSlice(candidateTables);
        var (systemPrompt, userPromptTemplate) = _prompts.GetSchemaLinker();
        var userPrompt = userPromptTemplate
            .Replace("{{question}}", question, StringComparison.Ordinal)
            .Replace("{{schemaSlice}}", schemaSlice, StringComparison.Ordinal);

        string raw;
        try
        {
            var llm = _llmFactory.For(AiRole.SchemaLinker);
            raw = await llm.GenerateJsonAsync(systemPrompt, userPrompt, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SchemaLinker] LLM call failed for question '{Question}'", question);
            return new SchemaLinkResult { Error = $"llm-failed: {ex.Message}" };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ParsedResponse>(raw, JsonOptions);
            return new SchemaLinkResult
            {
                RequiredTables = parsed?.RequiredTables?
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToList() ?? new(),
                RequiredColumns = parsed?.RequiredColumns?
                    .Where(c => !string.IsNullOrWhiteSpace(c.Table) && !string.IsNullOrWhiteSpace(c.Column))
                    .Select(c => new RequiredColumn(c.Table.Trim(), c.Column.Trim(), c.Reason?.Trim() ?? ""))
                    .ToList() ?? new(),
                JoinHints = parsed?.JoinHints?
                    .Where(j => !string.IsNullOrWhiteSpace(j.From) && !string.IsNullOrWhiteSpace(j.To))
                    .Select(j => new JoinHint(j.From.Trim(), j.To.Trim(), j.Via?.Trim() ?? ""))
                    .ToList() ?? new(),
                RawLlmOutput = raw,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[SchemaLinker] failed to parse JSON. Raw: {Raw}", Truncate(raw, 200));
            return new SchemaLinkResult { Error = "json-parse-failed", RawLlmOutput = raw };
        }
    }

    private static string BuildSchemaSlice(IReadOnlyList<RetrievedTableSummary> tables)
    {
        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            sb.Append("- ").Append(t.Table).Append('(');
            sb.Append(string.Join(", ", t.Columns));
            sb.Append(')');
            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                sb.Append(" — ").Append(t.Description);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class ParsedResponse
    {
        [JsonPropertyName("requiredTables")] public List<string>? RequiredTables { get; set; }
        [JsonPropertyName("requiredColumns")] public List<ParsedRequiredColumn>? RequiredColumns { get; set; }
        [JsonPropertyName("joinHints")] public List<ParsedJoinHint>? JoinHints { get; set; }
    }

    private sealed class ParsedRequiredColumn
    {
        [JsonPropertyName("table")] public string Table { get; set; } = "";
        [JsonPropertyName("column")] public string Column { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }

    private sealed class ParsedJoinHint
    {
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("to")] public string To { get; set; } = "";
        [JsonPropertyName("via")] public string Via { get; set; } = "";
    }
}
