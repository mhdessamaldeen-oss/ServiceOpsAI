namespace AnalystAgent.Abstractions;

/// <summary>
/// Internal LLM contract. The pipeline depends only on this; the host implementation lives in
/// <c>HostBridge/HostAiProviderLlmClient.cs</c> and wraps the host's IAiProviderFactory.
/// When/if this code moves to its own DLL, only the HostBridge implementation is replaced.
/// </summary>
public interface ILlmClient
{
    /// <summary>JSON-mode generation. The provider is asked to constrain output to JSON (Ollama format:json, OpenAI response_format:json_object, etc.).</summary>
    Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>Plain text generation. Used by the explainer to summarize result sets in natural language.</summary>
    Task<string> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plain text generation with optional per-call sampling overrides (temperature / seed) — the
    /// surface the execution-guided self-consistency path uses to draw DIVERSE candidates for one
    /// question. The DEFAULT interface implementation IGNORES <paramref name="sampling"/> and forwards
    /// to <see cref="GenerateTextAsync(string,string,CancellationToken)"/>, so every existing
    /// implementation and call site is untouched until an implementation opts in (only the two real
    /// host clients do). A null <paramref name="sampling"/> is identical to the legacy call.
    /// </summary>
    Task<string> GenerateTextAsync(string systemPrompt, string userPrompt,
        LlmSamplingOptions? sampling, CancellationToken cancellationToken = default)
        => GenerateTextAsync(systemPrompt, userPrompt, cancellationToken);
}
