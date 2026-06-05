namespace AnalystAgent.Retrieval;

using System.Collections.Generic;

/// <summary>
/// The in-memory cosine-rank + fail-open primitive shared by every embedding retriever.
/// Given a query vector and a set of <c>(item, vector)</c> candidates, score each candidate by
/// <see cref="VectorMath.Cosine"/> and return the <c>(item, score)</c> pairs IN INPUT ORDER.
///
/// <para><b>Scope.</b> This is ONLY the rank + fail-open math — deliberately not the priming,
/// caching, or persistence around it. The in-memory <see cref="Semantic.EmbeddingVectorCache{TItem}"/>
/// embeds-on-prime; the disk-backed retrievers (<see cref="SchemaSemanticRetriever"/>,
/// <see cref="VerifiedQueryMatcher"/>) LOAD their vectors from disk and keep their own
/// load/save/hash/regeneration logic. They differ in where the candidate vectors come from but
/// share this identical ranking loop, so only the loop is extracted here.</para>
///
/// <para><b>Fail-open + skip guards (byte-identical to the previous inline copies).</b>
/// An empty query vector yields an empty result (the embedder is down / unconfigured). A candidate
/// whose vector length differs from the query vector is skipped (never throws). Both
/// <see cref="VectorMath.Cosine"/> and this guard handle ragged input without exceptions.</para>
///
/// <para><b>No ordering, no threshold, no vocabulary.</b> Results come back in the same order as
/// <paramref name="candidates"/> — callers apply their own descending sort, similarity threshold,
/// top-K, aux-table penalty, or best-pick on top. This keeps each refactored call site identical
/// to its pre-refactor selection logic. The helper holds zero domain or per-deployment literals.</para>
/// </summary>
internal static class EmbeddingRank
{
    /// <summary>
    /// Cosine-rank <paramref name="candidates"/> against <paramref name="queryVec"/>, returning each
    /// item with its cosine score IN INPUT ORDER. Empty/null query vector → empty result (fail-open).
    /// Candidates whose vector length differs from the query are skipped.
    /// </summary>
    /// <typeparam name="T">The item type — opaque to the ranker.</typeparam>
    /// <param name="queryVec">The embedded query. Empty or null → empty result.</param>
    /// <param name="candidates">The <c>(item, vector)</c> pairs to score.</param>
    public static IReadOnlyList<(T Item, float Score)> RankByCosine<T>(
        float[]? queryVec,
        IEnumerable<(T Item, float[] Vec)> candidates)
    {
        var scored = new List<(T Item, float Score)>();
        if (queryVec is null || queryVec.Length == 0 || candidates is null) return scored;

        foreach (var (item, vec) in candidates)
        {
            if (vec is null || vec.Length != queryVec.Length) continue;
            scored.Add((item, VectorMath.Cosine(queryVec, vec)));
        }
        return scored;
    }
}
