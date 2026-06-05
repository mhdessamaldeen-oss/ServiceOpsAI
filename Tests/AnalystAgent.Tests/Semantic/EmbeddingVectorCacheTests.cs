namespace AnalystAgent.Tests.Semantic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AnalystAgent.Abstractions;
using AnalystAgent.Semantic;
using Xunit;

/// <summary>
/// Pins the shared <see cref="EmbeddingVectorCache{TItem}"/> that ToolSemanticSelector and
/// EntityEmbeddingMatcher now compose. Three concerns:
///   (1) it embeds the query once and returns each item with its cosine to the query (IN INPUT ORDER,
///       so callers apply their own sort / best-pick);
///   (2) it FAILS OPEN (empty result) when the embedder returns an empty vector or has no model;
///   (3) it PRIMES ONCE per cache key — a second call with the same key does not re-embed the items,
///       and a DIFFERENT cache key re-primes (this is the property the content-hash cache key relies on).
/// No domain vocabulary — items are plain strings.
/// </summary>
public class EmbeddingVectorCacheTests
{
    private static EmbeddingVectorCache<string> New(ITextEmbedder embedder, IMemoryCache? cache = null) =>
        new(embedder, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger.Instance);

    [Fact]
    public async Task PrimeAndRank_Scores_Each_Item_By_Cosine_In_Input_Order()
    {
        // Query vector == item "b"'s vector → b scores 1.0; a is orthogonal → 0; c is 45° → ~0.707.
        var items = new[] { "a", "b", "c" };
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync("query", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 0f, 1f, 0f });
        embedder.Setup(e => e.EmbedAsync("a", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f, 0f });
        embedder.Setup(e => e.EmbedAsync("b", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 0f, 1f, 0f });
        embedder.Setup(e => e.EmbedAsync("c", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 0.7f, 0.7f, 0f });

        var cache = New(embedder.Object);
        var ranked = await cache.PrimeAndRankAsync(items, x => x, "key1", "query");

        // Returned in INPUT order (no sort inside the cache).
        Assert.Equal(new[] { "a", "b", "c" }, ranked.Select(r => r.Item).ToArray());
        Assert.Equal(0f, ranked[0].Score, 3);
        Assert.Equal(1f, ranked[1].Score, 3);
        Assert.Equal(0.707f, ranked[2].Score, 3);
    }

    [Fact]
    public async Task PrimeAndRank_FailsOpen_When_Embedder_Returns_Empty_Query_Vector()
    {
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<float>());   // embedder down

        var cache = New(embedder.Object);
        var ranked = await cache.PrimeAndRankAsync(new[] { "a", "b" }, x => x, "key", "query");

        Assert.Empty(ranked);
    }

    [Fact]
    public async Task PrimeAndRank_FailsOpen_When_No_Model()
    {
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns(string.Empty);   // no embedder configured

        var cache = New(embedder.Object);
        var ranked = await cache.PrimeAndRankAsync(new[] { "a" }, x => x, "key", "query");

        Assert.Empty(ranked);
    }

    [Fact]
    public async Task PrimeAndRank_Skips_Items_With_Mismatched_Vector_Length()
    {
        // "b" embeds to a vector of a different length than the query → skipped, never throws.
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync("query", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f });
        embedder.Setup(e => e.EmbedAsync("a", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f });
        embedder.Setup(e => e.EmbedAsync("b", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f, 0f });

        var cache = New(embedder.Object);
        var ranked = await cache.PrimeAndRankAsync(new[] { "a", "b" }, x => x, "key", "query");

        Assert.Single(ranked);
        Assert.Equal("a", ranked[0].Item);
    }

    [Fact]
    public async Task PrimeAndRank_Primes_Once_Per_CacheKey_But_RePrimes_On_Different_Key()
    {
        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        embedder.Setup(e => e.EmbedAsync("query", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f, 0f });
        embedder.Setup(e => e.EmbedAsync("a", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { 1f, 0f, 0f });

        var sharedCache = new MemoryCache(new MemoryCacheOptions());
        var cache = New(embedder.Object, sharedCache);

        await cache.PrimeAndRankAsync(new[] { "a" }, x => x, "key-v1", "query");
        await cache.PrimeAndRankAsync(new[] { "a" }, x => x, "key-v1", "query");   // same key → reuse primed vectors

        // Item "a" embedded exactly ONCE across the two same-key calls (the query is embedded each call).
        embedder.Verify(e => e.EmbedAsync("a", It.IsAny<CancellationToken>()), Times.Once);

        await cache.PrimeAndRankAsync(new[] { "a" }, x => x, "key-v2", "query");   // different key → re-prime
        embedder.Verify(e => e.EmbedAsync("a", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
