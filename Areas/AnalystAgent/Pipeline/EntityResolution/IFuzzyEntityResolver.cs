namespace AnalystAgent.Pipeline.EntityResolution;

/// <summary>
/// Phase 6 — deterministic fuzzy matcher for entity VALUES in user questions. Resolves
/// typos and variations in proper nouns (district names, status labels, service-type names,
/// product codes) BEFORE the question reaches the LLM, so the prompt can carry "inferred:
/// 'Damascis' → 'Damascus'" as a hint instead of asking the LLM to guess.
///
/// <para><b>Pattern from CHESS (arXiv 2405.16755):</b> entity retrieval uses LSH against
/// the database's value set to handle typos and variations. Here we do the simpler trigram
/// + Levenshtein variant — easier to implement, easier to debug, sufficient for the
/// finite-size value sets you actually have (district names, lookup-table values, etc.).</para>
///
/// <para><b>No LLM call.</b> This stage is pure CPU — string comparison against a pre-built
/// index. The index is loaded once per process and refreshed on a schedule (e.g. nightly,
/// or on schema-introspection runs).</para>
///
/// <para><b>Live.</b> DI-registered; the trigram index is maintained by ValueIndexHostedService and ValueLinker calls ResolveAsync as a post-exact-match fallback (gated by AnalystOptions.EnableFuzzyValueLinking).</para>
/// </summary>
public interface IFuzzyEntityResolver
{
    /// <summary>Extract noun phrases from <paramref name="question"/> and fuzzy-match each
    /// against the loaded <see cref="IValueIndex"/>. Returns one resolved entity per
    /// successfully-matched phrase. Phrases that don't match (e.g. function words,
    /// numbers) are silently dropped.</summary>
    Task<IReadOnlyList<ResolvedEntity>> ResolveAsync(
        string question,
        CancellationToken cancellationToken = default);
}

/// <summary>One fuzzy-matched entity. <see cref="Surface"/> is what the user typed,
/// <see cref="Canonical"/> is what the database actually stores, <see cref="Column"/>
/// tells the downstream stages "filter on this column".</summary>
public sealed record ResolvedEntity(
    string Surface,
    string Canonical,
    string Table,
    string Column,
    double Similarity);
