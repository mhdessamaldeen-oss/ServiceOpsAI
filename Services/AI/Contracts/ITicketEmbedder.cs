namespace AISupportAnalysisPlatform.Services.AI;

/// <summary>
/// Generic text embedder. Library code uses this; consumers register their preferred provider
/// (Ollama, OpenAI embeddings, in-process model, etc.) at startup via DI. Tier 4.1 portability —
/// renamed from the legacy domain-suggestive <c>ITicketEmbedder</c>.
/// </summary>
public interface ITextEmbedder
{
    string ModelName { get; }
    float[] GenerateEmbedding(string text);
}

/// <summary>
/// Backward-compat alias for the renamed <see cref="ITextEmbedder"/>. Existing code that
/// injects <c>ITicketEmbedder</c> keeps working; new consumers should prefer
/// <see cref="ITextEmbedder"/>. Will be removed in a future major version.
/// </summary>
[System.Obsolete("Renamed to ITextEmbedder. Ticket-specific name was misleading; the interface is generic.")]
public interface ITicketEmbedder : ITextEmbedder { }
