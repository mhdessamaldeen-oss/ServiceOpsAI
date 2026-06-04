namespace AnalystAgent.Abstractions;

/// <summary>
/// Internal embedding interface — the AnalystAgent's view of "something that turns text into
/// a vector." The in-host build resolves this to <c>HostBridge.HostAiProviderEmbedder</c>, which
/// wraps the host's <c>IAiProviderFactory</c> at the workload configured for embeddings (Rag).
///
/// Kept deliberately minimal so the rest of the copilot does not depend on host AI types — when
/// this folder is extracted to a standalone DLL, only the bridge implementation is replaced.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Identifier for the model that produced vectors. Used for cache keys.</summary>
    string ModelName { get; }

    /// <summary>
    /// Returns the embedding vector for <paramref name="text"/>. Returns an empty array when the
    /// provider is unavailable or the text is empty — callers MUST treat empty as "embedding
    /// unavailable" and fall back to a non-vector path rather than crashing.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
