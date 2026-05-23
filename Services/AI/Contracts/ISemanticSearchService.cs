using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Services.AI.Copilot.Diagnostics;

namespace AISupportAnalysisPlatform.Services.AI;

public interface ISemanticSearchService
{
    Task UpsertTicketEmbeddingAsync(int ticketId);

    /// <summary>
    /// Find tickets semantically similar to a seed ticket. Defaults are tuned for the
    /// conversational copilot (search the full corpus, no precision floor) so new callers
    /// don't get a surprise "0 matches" from hidden filtering. Legacy callers (the ticket
    /// investigation tool) explicitly opt IN to the precision-tight defaults.
    /// </summary>
    /// <param name="restrictToTerminalStatuses">When true, only Resolved/Closed tickets are
    /// considered candidates (the legacy "find solved precedents" behavior). Default: false.</param>
    /// <param name="requirePrecisionThreshold">When true, the hybrid score must clear the
    /// configured minimum (precision over recall — right for duplicate finding). When false,
    /// returns top-K by raw score regardless of confidence. Default: false.</param>
    /// <param name="trace">Optional sink — when provided, the service records sub-steps
    /// (embedder call, candidate query, scoring breakdown) into it. The handler drains the
    /// sink into its parent step so the Investigation page shows full detail.</param>
    Task<List<SemanticSearchMatch>> GetRelatedTicketsAsync(
        int ticketId,
        int count = 5,
        List<int>? statusIds = null,
        bool restrictToTerminalStatuses = false,
        bool requirePrecisionThreshold = false,
        CopilotTraceSink? trace = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find tickets semantically similar to a free-text query. Same default policy as
    /// <see cref="GetRelatedTicketsAsync"/> — see that doc-comment for parameter semantics.
    /// </summary>
    Task<List<SemanticSearchMatch>> SearchSimilarTicketsByTextAsync(
        string queryText,
        int count = 5,
        List<int>? statusIds = null,
        bool restrictToTerminalStatuses = false,
        bool requirePrecisionThreshold = false,
        CopilotTraceSink? trace = null,
        CancellationToken cancellationToken = default);

    Task<AISupportAnalysisPlatform.Models.AI.RetrievalTuningSettings> GetTuningSettingsAsync();
    Task UpdateTuningSettingsAsync(AISupportAnalysisPlatform.Models.AI.RetrievalTuningSettings settings);
}
