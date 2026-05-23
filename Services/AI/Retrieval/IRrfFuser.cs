namespace AISupportAnalysisPlatform.Services.AI.Retrieval
{
    /// <summary>
    /// Reciprocal Rank Fusion. Merges multiple ranked candidate lists (e.g. dense + BM25)
    /// into a single fused ranking without needing to calibrate the source-list scores
    /// against each other. Standard Cormack/Buettcher 2009 formula: score = Σ 1 / (k + rank_i).
    /// </summary>
    public interface IRrfFuser
    {
        /// <summary>
        /// Fuse multiple per-retriever rankings. Each ranking is a list of (Key, Rank)
        /// where Rank is 0-based. Returns the fused list sorted by descending fused
        /// score, normalized to [0,1] against the top fused score.
        /// </summary>
        IReadOnlyList<RrfResult> Fuse(
            IReadOnlyList<IReadOnlyList<(string Key, int Rank)>> rankings,
            int k);
    }

    public sealed record RrfResult(string Key, float Score);
}
