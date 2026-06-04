namespace AnalystAgent.Abstractions;

/// <summary>
/// Internal semantic-search contract — the AnalystAgent's view of "find entities similar
/// to a seed (by natural key or by free text)." Generalized over entity type so the host can
/// register a search backend for Tickets, Orders, Articles, or any other indexed corpus.
/// The in-host build resolves this to <c>HostBridge.HostSemanticSearchBridge</c>, which today
/// wraps the host's ticket-specific <c>ISemanticSearchService</c>; a future host can replace
/// or augment that bridge without changing the rest of the copilot.
///
/// Kept minimal so the rest of the copilot doesn't depend on host AI types.
/// </summary>
public interface ISemanticSearch
{
    /// <summary>True when the host has a semantic-search service registered. When false,
    /// the SemanticSearchHandler falls through silently and the planner handles the question.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Find the top-K entities semantically similar to the seed identified by
    /// <paramref name="naturalKey"/> on entity <paramref name="entityType"/>
    /// (e.g. "Ticket" + "TCK-2026-00050"). Hosts that only support one entity type may ignore
    /// the parameter; multi-corpus hosts dispatch on it. Returns an empty list when the seed
    /// doesn't exist or the entity isn't indexed (a legitimate "no neighbours" answer); THROWS
    /// <see cref="SemanticSearchUnavailableException"/> when the search backend itself is down, so
    /// the caller can fall through to the planner instead of reporting a false "no similar records".
    /// </summary>
    Task<IReadOnlyList<SemanticSearchHit>> FindSimilarToEntityAsync(string entityType, string naturalKey, int topK, CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text semantic search. The embedder turns <paramref name="queryText"/> into a
    /// vector; the host cosine-ranks against its indexed corpus. <paramref name="entityType"/>
    /// optionally narrows the search to one entity ("Ticket", "Order"); null/empty searches
    /// every indexed corpus the host knows about.
    /// </summary>
    Task<IReadOnlyList<SemanticSearchHit>> SearchByTextAsync(string queryText, int topK, string? entityType = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Single semantic-search hit returned to the orchestrator. Domain-neutral — the EntityType
/// + NaturalKey + DisplayLabel triple identifies any indexable record (a Ticket, an Order, a
/// Knowledge-base Article, etc.) without the AnalystAgent pipeline depending on the
/// host's EF entity types.
///
/// <para>EntityKey is the surrogate primary key serialized as string. Most schemas use int
/// PKs but some use GUIDs, composite keys, or string IDs — stringifying keeps the contract
/// uniform and lets the bridge translate to/from whatever the host's storage uses.</para>
/// </summary>
public sealed record SemanticSearchHit(
    string EntityType,
    string EntityKey,
    string NaturalKey,
    string DisplayLabel,
    float Score);

/// <summary>Thrown by an <see cref="ISemanticSearch"/> implementation when the search backend
/// (embedder / vector service) is genuinely UNAVAILABLE — distinct from a successful search that
/// found no neighbours (which returns an empty list). The SemanticSearchHandler catches this and
/// falls through to the planner instead of surfacing a false "no similar records" answer.</summary>
public sealed class SemanticSearchUnavailableException : Exception
{
    public SemanticSearchUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
