namespace AnalystAgent.Tests.Retrieval;

using System.Linq;
using AnalystAgent.Retrieval;
using Xunit;

/// <summary>
/// Pins the shared <see cref="EmbeddingRank.RankByCosine{T}"/> primitive that the disk-backed
/// retrievers (SchemaSemanticRetriever + VerifiedQueryMatcher) now call for their in-memory
/// cosine ranking. Three concerns, matching the inline loops it replaced:
///   (1) ranks each candidate by cosine to the query, IN INPUT ORDER (callers sort/threshold on top);
///   (2) FAILS OPEN (empty result) on an empty/null query vector;
///   (3) SKIPS candidates whose vector length differs from the query (never throws).
/// No domain vocabulary — items are plain strings.
/// </summary>
public class EmbeddingRankTests
{
    [Fact]
    public void RankByCosine_Scores_Each_Item_In_Input_Order()
    {
        // Query == "b"'s vector → b=1.0; a orthogonal → 0; c at 45° → ~0.707.
        var query = new[] { 0f, 1f, 0f };
        var candidates = new (string, float[])[]
        {
            ("a", new[] { 1f, 0f, 0f }),
            ("b", new[] { 0f, 1f, 0f }),
            ("c", new[] { 0.7f, 0.7f, 0f }),
        };

        var ranked = EmbeddingRank.RankByCosine(query, candidates);

        // Returned in INPUT order — no internal sort.
        Assert.Equal(new[] { "a", "b", "c" }, ranked.Select(r => r.Item).ToArray());
        Assert.Equal(0f, ranked[0].Score, 3);
        Assert.Equal(1f, ranked[1].Score, 3);
        Assert.Equal(0.707f, ranked[2].Score, 3);
    }

    [Fact]
    public void RankByCosine_Ranks_Descending_When_Caller_Sorts()
    {
        // The primitive itself does not sort; this asserts the score ordering a caller relies on.
        var query = new[] { 1f, 0f };
        var candidates = new (string, float[])[]
        {
            ("far", new[] { 0f, 1f }),       // cosine 0
            ("near", new[] { 1f, 0f }),      // cosine 1
            ("mid", new[] { 0.7f, 0.7f }),   // cosine ~0.707
        };

        var byScore = EmbeddingRank.RankByCosine(query, candidates)
            .OrderByDescending(r => r.Score)
            .Select(r => r.Item)
            .ToArray();

        Assert.Equal(new[] { "near", "mid", "far" }, byScore);
    }

    [Fact]
    public void RankByCosine_FailsOpen_On_Empty_Query_Vector()
    {
        var candidates = new (string, float[])[] { ("a", new[] { 1f, 0f }) };

        Assert.Empty(EmbeddingRank.RankByCosine(System.Array.Empty<float>(), candidates));
        Assert.Empty(EmbeddingRank.RankByCosine<string>(null, candidates));
    }

    [Fact]
    public void RankByCosine_Skips_Length_Mismatch_Candidates()
    {
        var query = new[] { 1f, 0f };
        var candidates = new (string, float[])[]
        {
            ("ok", new[] { 1f, 0f }),
            ("mismatch", new[] { 1f, 0f, 0f }),   // different length → skipped, no throw
        };

        var ranked = EmbeddingRank.RankByCosine(query, candidates);

        Assert.Single(ranked);
        Assert.Equal("ok", ranked[0].Item);
    }
}
