namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Data;
using ServiceOpsAI.Services.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;

/// <summary>
/// Bridges the AnalystAgent's generic <see cref="ISemanticSearch"/> to the host's
/// existing ticket-only <see cref="ISemanticSearchService"/>. The host service does the
/// heavy lifting: loads vectors from <c>TicketSemanticEmbeddings</c>, runs cosine + lexical
/// hybrid scoring, and respects the configured tuning thresholds.
///
/// <para>The generic contract takes an <c>entityType</c> parameter. This bridge currently
/// only knows how to search Tickets, so it accepts any of the ticket synonyms ("Ticket",
/// "Tickets", "ticket", or null/empty — treated as the host default) and routes those to
/// the ticket index. Other entityType values return an empty list. When the host gains a
/// second indexed corpus (Orders, KB articles, …), extend this bridge with a dispatch table
/// on <c>entityType</c> — the AnalystAgent pipeline doesn't need to change.</para>
///
/// THIS IS THE ONLY semantic-search file that depends on host types. When the
/// AnalystAgent extracts to a DLL, this class is replaced.
/// </summary>
internal sealed class HostSemanticSearchBridge : ISemanticSearch
{
    private const string TicketEntityType = "Ticket";

    private readonly ISemanticSearchService _hostService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<HostSemanticSearchBridge> _logger;

    public HostSemanticSearchBridge(
        ISemanticSearchService hostService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<HostSemanticSearchBridge> logger)
    {
        _hostService = hostService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<SemanticSearchHit>> FindSimilarToEntityAsync(string entityType, string naturalKey, int topK, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(naturalKey)) return Array.Empty<SemanticSearchHit>();
        if (!IsTicketEntity(entityType))
        {
            _logger.LogInformation("[HostSemanticSearchBridge] No indexed corpus for entityType '{EntityType}'; only 'Ticket' is supported.", entityType);
            return Array.Empty<SemanticSearchHit>();
        }

        // Resolve the natural-key (TicketNumber) to the integer Id the host service expects.
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var seedId = await ctx.Tickets
            .Where(t => t.TicketNumber == naturalKey && !t.IsDeleted)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (seedId is null)
        {
            _logger.LogInformation("[HostSemanticSearchBridge] Seed ticket {NaturalKey} not found.", naturalKey);
            return Array.Empty<SemanticSearchHit>();
        }

        try
        {
            var matches = await _hostService.GetRelatedTicketsAsync(
                ticketId: seedId.Value,
                count: topK,
                statusIds: null,
                restrictToTerminalStatuses: false,
                requirePrecisionThreshold: false,
                trace: null,
                cancellationToken: cancellationToken);

            return matches
                .Where(m => m.Ticket is not null)
                .Select(m => new SemanticSearchHit(
                    EntityType: TicketEntityType,
                    EntityKey: m.Ticket!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NaturalKey: m.Ticket.TicketNumber ?? "",
                    DisplayLabel: m.Ticket.Title ?? "",
                    Score: m.Score))
                .ToList();
        }
        catch (Exception ex)
        {
            // Genuine backend failure (embedder/vector service down) — NOT a legitimate empty result.
            // Surface it so the handler falls through to the planner instead of a false "no neighbours".
            _logger.LogWarning(ex, "[HostSemanticSearchBridge] Similarity search failed for {NaturalKey}.", naturalKey);
            throw new SemanticSearchUnavailableException($"similarity search failed for '{naturalKey}'", ex);
        }
    }

    public async Task<IReadOnlyList<SemanticSearchHit>> SearchByTextAsync(string queryText, int topK, string? entityType = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return Array.Empty<SemanticSearchHit>();
        // Host only indexes tickets today. A null/empty entityType means "default index" — also tickets.
        if (!string.IsNullOrEmpty(entityType) && !IsTicketEntity(entityType))
        {
            return Array.Empty<SemanticSearchHit>();
        }

        try
        {
            var matches = await _hostService.SearchSimilarTicketsByTextAsync(
                queryText: queryText,
                count: topK,
                statusIds: null,
                restrictToTerminalStatuses: false,
                requirePrecisionThreshold: false,
                trace: null,
                cancellationToken: cancellationToken);

            return matches
                .Where(m => m.Ticket is not null)
                .Select(m => new SemanticSearchHit(
                    EntityType: TicketEntityType,
                    EntityKey: m.Ticket!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NaturalKey: m.Ticket.TicketNumber ?? "",
                    DisplayLabel: m.Ticket.Title ?? "",
                    Score: m.Score))
                .ToList();
        }
        catch (Exception ex)
        {
            // Genuine backend failure — surface it (see FindSimilarToEntityAsync) so the handler
            // falls through to the planner rather than reporting a false empty result.
            _logger.LogWarning(ex, "[HostSemanticSearchBridge] Free-text semantic search failed for q='{Q}'.", queryText.Length > 60 ? queryText.Substring(0, 60) + "…" : queryText);
            throw new SemanticSearchUnavailableException("free-text semantic search failed", ex);
        }
    }

    /// <summary>Match the entity-type token against the supported ticket synonyms. Lower-case
    /// equality on a small set; null/empty defaults to the only indexed corpus (Ticket).</summary>
    private static bool IsTicketEntity(string? entityType)
    {
        if (string.IsNullOrEmpty(entityType)) return true;
        return entityType.Equals("Ticket", StringComparison.OrdinalIgnoreCase)
            || entityType.Equals("Tickets", StringComparison.OrdinalIgnoreCase);
    }
}
