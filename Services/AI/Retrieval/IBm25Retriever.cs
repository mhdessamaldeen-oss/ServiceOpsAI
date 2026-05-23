namespace ServiceOpsAI.Services.AI.Retrieval
{
    /// <summary>
    /// Lexical BM25 scoring. Complements the dense (vector) retriever — recovers
    /// exact-token recall on terms like ticket IDs, CVE numbers, product names
    /// that get washed out in an embedding space.
    /// </summary>
    public interface IBm25Retriever
    {
        /// <summary>
        /// Score each document against the query terms. Returns one (Key, Score) per
        /// input document, in input order. Scores are normalized to [0,1] using the
        /// max-score-in-corpus so callers can mix them with cosine on equal footing.
        /// </summary>
        IReadOnlyList<Bm25Result> Score(
            IReadOnlyList<Bm25Document> documents,
            IReadOnlyCollection<string> queryTerms);
    }

    /// <summary>Opaque key + tokenized terms for one corpus document.</summary>
    public sealed record Bm25Document(string Key, IReadOnlyList<string> Terms);

    public sealed record Bm25Result(string Key, float Score);
}
