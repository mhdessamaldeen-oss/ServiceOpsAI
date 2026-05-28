namespace SuperAdminCopilot.Pipeline.Stages.Decomposed;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Services.AI.Providers.Roles;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Phase 2c — composes the final <see cref="QuerySpec"/> from the question plus the outputs
/// of <see cref="IStructuralCueParser"/> and <see cref="ISchemaLinker"/>. Where the existing
/// monolithic <see cref="Stages.SpecExtractor"/> tries to do all three jobs in one LLM call,
/// this composer receives PRE-RESOLVED inputs and only has to assemble the spec — a
/// dramatically narrower task that small local models can handle reliably.
///
/// <para><b>Why the composer still uses an LLM:</b> the question contains filter values,
/// aggregate verbs ("count", "average"), and ordering cues ("top 5", "newest first") that
/// don't fit neatly into either the structural-cue parser or the schema linker. The composer
/// sees them in their original context and produces a typed spec. Future versions could
/// replace this stage with deterministic logic once the spec language is rich enough.</para>
///
/// <para><b>Output format:</b> the composer emits raw JSON matching the existing
/// <see cref="QuerySpec"/> shape. The orchestrator (post-activation) parses this into the
/// existing <c>QuerySpec</c> type and feeds it to the existing <c>SqlCompiler</c> unchanged —
/// the new path is drop-in compatible with everything downstream.</para>
///
/// <para><b>Staged, dormant.</b> Not registered in DI in this commit.</para>
/// </summary>
public interface IDecomposedQuerySpecComposer
{
    Task<DecomposedSpecResult> ComposeAsync(
        string question,
        SchemaLinkResult schemaLink,
        StructuralCueResult structuralCues,
        CancellationToken cancellationToken = default);
}

/// <summary>Composer output. <see cref="QuerySpecJson"/> is the raw JSON the LLM produced —
/// the orchestrator deserialises it into the existing <c>QuerySpec</c> type. Kept as raw JSON
/// at this layer so this file doesn't have to take a hard dependency on the SpecConstants /
/// QuerySpec internal types.</summary>
public sealed record DecomposedSpecResult
{
    public string? QuerySpecJson { get; init; }
    public string? Error { get; init; }
    public string? Prompt { get; init; }
}

internal sealed class DecomposedQuerySpecComposer : IDecomposedQuerySpecComposer
{
    private readonly IRoleBoundLlmClientFactory _llmFactory;
    private readonly IDecomposedPromptProvider _prompts;
    private readonly ILogger<DecomposedQuerySpecComposer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public DecomposedQuerySpecComposer(
        IRoleBoundLlmClientFactory llmFactory,
        IDecomposedPromptProvider prompts,
        ILogger<DecomposedQuerySpecComposer> logger)
    {
        _llmFactory = llmFactory;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task<DecomposedSpecResult> ComposeAsync(
        string question,
        SchemaLinkResult schemaLink,
        StructuralCueResult structuralCues,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new DecomposedSpecResult { Error = "empty question" };

        var schemaLinkJson = JsonSerializer.Serialize(new
        {
            requiredTables = schemaLink.RequiredTables,
            requiredColumns = schemaLink.RequiredColumns,
            joinHints = schemaLink.JoinHints,
        }, JsonOptions);

        var structuralJson = JsonSerializer.Serialize(new
        {
            displayColumns = structuralCues.DisplayColumns,
            groupingHints = structuralCues.GroupingHints,
            hasExplicitColumnRequest = structuralCues.HasExplicitColumnRequest,
        }, JsonOptions);

        var (systemPrompt, userPromptTemplate) = _prompts.GetQuerySpecComposer();
        var userPrompt = userPromptTemplate
            .Replace("{{question}}", question, StringComparison.Ordinal)
            .Replace("{{schemaLinkJson}}", schemaLinkJson, StringComparison.Ordinal)
            .Replace("{{structuralJson}}", structuralJson, StringComparison.Ordinal);

        try
        {
            var llm = _llmFactory.For(AiRole.QuerySpecComposer);
            var raw = await llm.GenerateJsonAsync(systemPrompt, userPrompt, cancellationToken);
            return new DecomposedSpecResult { QuerySpecJson = raw, Prompt = userPrompt };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DecomposedQuerySpecComposer] LLM call failed for question '{Question}'", question);
            return new DecomposedSpecResult { Error = $"llm-failed: {ex.Message}", Prompt = userPrompt };
        }
    }
}
