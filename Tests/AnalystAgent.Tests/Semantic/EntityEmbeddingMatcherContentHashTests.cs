namespace AnalystAgent.Tests.Semantic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AnalystAgent.Abstractions;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;
using Xunit;

/// <summary>
/// Pins the content-hash cache key BACKPORTED to <see cref="EntityEmbeddingMatcher"/>: a semantic-layer
/// edit (here, an added synonym) must change the cache key so the entity vectors are re-primed against
/// the new label. Before the backport the key was model-name-only and a synonym edit silently kept the
/// stale vectors. Also pins ComputeEntitySetHash's stability + sensitivity directly.
/// No domain vocabulary — the entity uses placeholder names.
/// </summary>
public class EntityEmbeddingMatcherContentHashTests
{
    [Fact]
    public void ComputeEntitySetHash_Is_Stable_For_Same_Content_And_Order_Independent()
    {
        var a = Entity("Widgets", "wt", new[] { "gadget" });
        var b = Entity("Sprockets", "sp", new[] { "cog" });

        var h1 = EntityEmbeddingMatcher.ComputeEntitySetHash(new[] { a, b });
        var h2 = EntityEmbeddingMatcher.ComputeEntitySetHash(new[] { b, a });   // reordered

        Assert.Equal(h1, h2);   // order-independent (lines are sorted before hashing)
    }

    [Fact]
    public void ComputeEntitySetHash_Changes_When_A_Synonym_Is_Edited()
    {
        var before = EntityEmbeddingMatcher.ComputeEntitySetHash(new[] { Entity("Widgets", "wt", new[] { "gadget" }) });
        var after = EntityEmbeddingMatcher.ComputeEntitySetHash(new[] { Entity("Widgets", "wt", new[] { "gadget", "doohickey" }) });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task FindAsync_RePrimes_When_A_Synonym_Is_Added_Between_Calls()
    {
        // Shared singleton cache across both matcher instances (mirrors production IMemoryCache lifetime).
        var sharedCache = new MemoryCache(new MemoryCacheOptions());

        var embedder = new Mock<ITextEmbedder>();
        embedder.SetupGet(e => e.ModelName).Returns("fake-model");
        // Query embeds to a fixed vector; ANY entity label embeds to the same vector → cosine 1 (>= 0.65 default).
        embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { 1f, 0f, 0f });

        // First config: one entity, single synonym.
        var v1 = await FindOnce(sharedCache, embedder.Object, Entity("Widgets", "wt", new[] { "gadget" }));
        Assert.NotNull(v1);
        var labelV1 = EntityEmbeddingMatcher.BuildEntityLabel(Entity("Widgets", "wt", new[] { "gadget" }));
        embedder.Verify(e => e.EmbedAsync(labelV1, It.IsAny<CancellationToken>()), Times.Once);

        // Second config: SAME entity but with an added synonym → new label, new content hash → MUST re-prime.
        var v2 = await FindOnce(sharedCache, embedder.Object, Entity("Widgets", "wt", new[] { "gadget", "doohickey" }));
        Assert.NotNull(v2);
        var labelV2 = EntityEmbeddingMatcher.BuildEntityLabel(Entity("Widgets", "wt", new[] { "gadget", "doohickey" }));
        // The NEW label was embedded — proof the cache key changed and the vectors were re-primed.
        embedder.Verify(e => e.EmbedAsync(labelV2, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual(labelV1, labelV2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static EntityDefinition Entity(string name, string table, IEnumerable<string> synonyms) =>
        new() { Name = name, Table = table, Synonyms = new List<string>(synonyms) };

    private static async Task<EntityDefinition?> FindOnce(IMemoryCache cache, ITextEmbedder embedder, EntityDefinition entity)
    {
        var config = new SemanticLayerConfig { Entities = new List<EntityDefinition> { entity } };

        var semantic = new Mock<ISemanticLayer>(MockBehavior.Loose);
        semantic.SetupGet(s => s.Config).Returns(config);

        var catalog = new Mock<IEntityCatalog>(MockBehavior.Loose);
        catalog.Setup(c => c.TableExists(It.IsAny<string>())).Returns(true);

        var policy = new Mock<IAnalystSchemaAccessPolicy>(MockBehavior.Loose);
        policy.Setup(p => p.IsTableQueryable(It.IsAny<string>())).Returns(true);

        var matcher = new EntityEmbeddingMatcher(
            semantic.Object, catalog.Object, embedder, cache, policy.Object,
            NullLogger<EntityEmbeddingMatcher>.Instance);

        return await matcher.FindAsync("anything");
    }
}
