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
}
