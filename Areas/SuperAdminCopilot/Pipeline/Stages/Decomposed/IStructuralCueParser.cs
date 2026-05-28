namespace SuperAdminCopilot.Pipeline.Stages.Decomposed;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Services.AI.Providers.Roles;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Phase 2b — extracts STRUCTURAL CUES from a question: which display columns were
/// explicitly requested (via brackets, "with their X" prose, etc.), in what order, and
/// any grouping hints. Does NOT touch the schema — this is pure NL parsing.
///
/// <para><b>Why this is its own stage:</b> the user's reported bracket failure
/// ("show me users [Id, Name] and their role") shows that the joint Spec-Extractor prompt
/// confuses structural-cue parsing with schema resolution. Splitting them gives each
/// LLM call a narrower contract that a small local model can actually meet. The
/// <see cref="AiRole.StructuralCueParser"/> binding lets you point this stage at a
/// tiny (3B) model — cost is dominated by token count, and the prompt fits in ~500 tokens.</para>
///
/// <para><b>Staged, dormant.</b> Not registered in DI in this commit. The
/// <see cref="QuerySpecComposer"/> path activation will register all three Phase-2 stages
/// together with one orchestrator wiring change.</para>
/// </summary>
public interface IStructuralCueParser
{
    Task<StructuralCueResult> ParseAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>What the user explicitly said about presentation. Empty <see cref="DisplayColumns"/>
/// + <see cref="HasExplicitColumnRequest"/>=false means "no explicit cue — let the schema
/// linker / composer pick defaults".</summary>
public sealed record StructuralCueResult
{
    public IReadOnlyList<DisplayColumnRequest> DisplayColumns { get; init; } = Array.Empty<DisplayColumnRequest>();
    public IReadOnlyList<string> GroupingHints { get; init; } = Array.Empty<string>();
    public bool HasExplicitColumnRequest { get; init; }
    public string? RawLlmOutput { get; init; }
    public string? Error { get; init; }
}

public sealed record DisplayColumnRequest
{
    public string Name { get; init; } = "";
    public int Order { get; init; }
    /// <summary>Where in the surface text this cue came from — useful for the trace sink.
    /// Values: <c>bracket-square</c>, <c>bracket-paren</c>, <c>bracket-arabic</c>,
    /// <c>post-bracket-and</c>, <c>with-their</c>, <c>implicit</c>.</summary>
    public string Source { get; init; } = "implicit";
}

internal sealed class StructuralCueParser : IStructuralCueParser
{
    private readonly IRoleBoundLlmClientFactory _llmFactory;
    private readonly IDecomposedPromptProvider _prompts;
    private readonly ILogger<StructuralCueParser> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public StructuralCueParser(
        IRoleBoundLlmClientFactory llmFactory,
        IDecomposedPromptProvider prompts,
        ILogger<StructuralCueParser> logger)
    {
        _llmFactory = llmFactory;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<StructuralCueResult> ParseAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new StructuralCueResult { Error = "empty question" };

        var (systemPrompt, userPromptTemplate) = _prompts.GetStructuralCueParser();
        var userPrompt = userPromptTemplate.Replace("{{question}}", question, StringComparison.Ordinal);

        string raw;
        try
        {
            var llm = _llmFactory.For(AiRole.StructuralCueParser);
            raw = await llm.GenerateJsonAsync(systemPrompt, userPrompt, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StructuralCueParser] LLM call failed for question '{Question}'", question);
            return new StructuralCueResult { Error = $"llm-failed: {ex.Message}" };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ParsedResponse>(raw, JsonOptions);
            return new StructuralCueResult
            {
                DisplayColumns = parsed?.DisplayColumns?
                    .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                    .Select((d, i) => new DisplayColumnRequest
                    {
                        Name = d.Name.Trim(),
                        Order = d.Order > 0 ? d.Order : i + 1,
                        Source = string.IsNullOrWhiteSpace(d.Source) ? "implicit" : d.Source,
                    })
                    .ToList() ?? new(),
                GroupingHints = parsed?.GroupingHints?
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim())
                    .ToList() ?? new(),
                HasExplicitColumnRequest = parsed?.HasExplicitColumnRequest ?? false,
                RawLlmOutput = raw,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[StructuralCueParser] failed to parse JSON. Raw: {Raw}", Truncate(raw, 200));
            return new StructuralCueResult { Error = "json-parse-failed", RawLlmOutput = raw };
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class ParsedResponse
    {
        [JsonPropertyName("displayColumns")] public List<ParsedDisplayColumn>? DisplayColumns { get; set; }
        [JsonPropertyName("groupingHints")] public List<string>? GroupingHints { get; set; }
        [JsonPropertyName("hasExplicitColumnRequest")] public bool HasExplicitColumnRequest { get; set; }
    }

    private sealed class ParsedDisplayColumn
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = "implicit";
    }
}
