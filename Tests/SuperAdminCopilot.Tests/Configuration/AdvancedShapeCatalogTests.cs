namespace SuperAdminCopilot.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards for the escape-valve advanced-shape detection: (1) the shipped <c>shape-examples.json</c>
/// reproduces the in-code fallback byte-for-byte (portability, behavior-neutral); (2) keyword
/// detection works for English AND Arabic; (3) the 2026-06-02 embedding-similarity fallback fires
/// when keywords miss (multilingual / paraphrase robustness). The byte-identity and English keyword
/// tests use a stub embedder (the keyword path short-circuits before embedding); the embedding
/// fallback is exercised deterministically with a controlled fake embedder over a temp config.
/// </summary>
public class AdvancedShapeCatalogTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────────────

    /// <summary>Never expected to be called on the keyword path; returns a zero vector if it is.</summary>
    private sealed class StubEmbedder : ITextEmbedder
    {
        public string ModelName => "stub";
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[8]);
    }

    /// <summary>Deterministic embedder: maps each text to a vector via an exact lookup, so cosine
    /// similarity is fully controlled for the mechanics test.</summary>
    private sealed class LookupEmbedder : ITextEmbedder
    {
        private readonly IReadOnlyDictionary<string, float[]> _map;
        public LookupEmbedder(IReadOnlyDictionary<string, float[]> map) => _map = map;
        public string ModelName => "lookup";
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(_map.TryGetValue(text, out var v) ? v : new float[] { 0.001f, 0f });
    }

    // ── Builders ─────────────────────────────────────────────────────────────────────

    private static IAdvancedShapeCatalog BuildCatalog(ITextEmbedder? embedder = null,
        string? keywordsPath = null, float threshold = 0.5f)
    {
        var opts = Options.Create(new CopilotOptions
        {
            ShapeExamplesPath = RepoConfigPath("shape-examples.json"),
            AdvancedShapeKeywordsPath = keywordsPath ?? RepoConfigPath("advanced-shape-keywords.json"),
            AdvancedShapeEmbeddingThreshold = threshold,
        });
        return new AdvancedShapeCatalog(opts, embedder ?? new StubEmbedder(), NullLogger<AdvancedShapeCatalog>.Instance);
    }

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "SuperAdminCopilot", "Configuration", file);
    }

    private static (Type EmitterType, Type ShapeEnum) Reflect()
    {
        var asm = typeof(ILlmDirectSqlEmitter).Assembly;
        var emitterType = asm.GetType("SuperAdminCopilot.Pipeline.Stages.LlmDirectSqlEmitter")
            ?? throw new InvalidOperationException("LlmDirectSqlEmitter type not found.");
        var shapeEnum = emitterType.GetNestedType("AdvancedShape", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AdvancedShape enum not found.");
        return (emitterType, shapeEnum);
    }

    // ── (1) byte-identity (portability, behavior-neutral) ──────────────────────────────

    [Fact]
    public void ShapeExamplesJson_IsByteIdenticalTo_InCodeFallback()
    {
        var catalog = BuildCatalog();
        Assert.True(catalog.ExamplesFilePresent, "shape-examples.json was not found / failed to load.");

        var (emitterType, shapeEnum) = Reflect();
        var fallback = emitterType.GetMethod("GetShapeExampleFallback", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetShapeExampleFallback not found.");

        foreach (var value in Enum.GetValues(shapeEnum))
        {
            var key = value.ToString()!;
            if (key == "None") continue;
            var expected = (string)fallback.Invoke(null, new[] { value })!;
            Assert.Equal(expected, catalog.ExampleFor(key));
        }
    }

    /// <summary>Every non-None shape must have a worked example (so the escape valve always has a
    /// pattern to show for a detected shape).</summary>
    [Fact]
    public void ShapeExamplesJson_HasNonEmptyExample_ForEveryShape()
    {
        var catalog = BuildCatalog();
        Assert.True(catalog.ExamplesFilePresent);
        var (_, shapeEnum) = Reflect();
        foreach (var value in Enum.GetValues(shapeEnum))
        {
            var key = value.ToString()!;
            if (key == "None") continue;
            Assert.False(string.IsNullOrWhiteSpace(catalog.ExampleFor(key)), $"shape '{key}' has no worked example.");
        }
    }

    // ── (2) keyword detection: English parity + Arabic ─────────────────────────────────

    [Fact]
    public void AdvancedShapeKeywordsJson_DetectionMatches_InCodeFallback_OverEnglishBank()
    {
        var catalog = BuildCatalog();
        Assert.True(catalog.KeywordsFilePresent);
        var (emitterType, _) = Reflect();
        var fallback = emitterType.GetMethod("DetectAdvancedShapeFallback", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DetectAdvancedShapeFallback not found.");

        foreach (var q in EnglishQuestionBank)
        {
            var expected = fallback.Invoke(null, new object[] { q })!.ToString();
            var actual = catalog.DetectShapeKey(q) ?? "None";
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("أعطني المجموع التراكمي للفواتير شهرياً", "WindowRunning")]
    [InlineData("رتب المناطق حسب الترتيب", "WindowRank")]
    [InlineData("التذاكر مقارنة بالشهر السابق", "WindowLag")]
    [InlineData("التسلسل الهرمي للمناطق", "Recursive")]
    [InlineData("تذاكر أخرى من نفس الزبون", "SelfJoin")]
    public void DetectShapeKey_MatchesArabicKeywords(string question, string expectedShape)
    {
        var catalog = BuildCatalog();
        Assert.Equal(expectedShape, catalog.DetectShapeKey(question));
    }

    // ── (3) embedding-similarity fallback mechanics (deterministic) ────────────────────

    [Fact]
    public async Task DetectShapeKeyAsync_FallsBackToEmbedding_WhenKeywordsMiss()
    {
        // Temp keyword config with two shapes whose signatures are distinctive markers.
        var tmp = Path.Combine(Path.GetTempPath(), $"shapekw-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp,
            "{\"shapes\":[{\"shape\":\"ShapeA\",\"keywords\":[\"AAAA\"]},{\"shape\":\"ShapeB\",\"keywords\":[\"BBBB\"]}]}");
        try
        {
            // Question "zzz" matches NO keyword, but the lookup embedder maps it to ShapeB's vector.
            var vecA = new float[] { 1f, 0f };
            var vecB = new float[] { 0f, 1f };
            var embedder = new LookupEmbedder(new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["zzz"] = vecB,      // the question
                ["AAAA"] = vecA,     // ShapeA signature
                ["BBBB"] = vecB,     // ShapeB signature
            });
            var catalog = BuildCatalog(embedder, keywordsPath: tmp, threshold: 0.5f);

            Assert.Null(catalog.DetectShapeKey("zzz"));                       // keyword path misses
            Assert.Equal("ShapeB", await catalog.DetectShapeKeyAsync("zzz")); // embedding path wins
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task DetectShapeKeyAsync_ReturnsNull_WhenBelowThreshold()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"shapekw-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, "{\"shapes\":[{\"shape\":\"ShapeA\",\"keywords\":[\"AAAA\"]}]}");
        try
        {
            // Question is orthogonal to the only shape signature → cosine 0 < threshold → null.
            var embedder = new LookupEmbedder(new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["zzz"] = new float[] { 0f, 1f },
                ["AAAA"] = new float[] { 1f, 0f },
            });
            var catalog = BuildCatalog(embedder, keywordsPath: tmp, threshold: 0.5f);
            Assert.Null(await catalog.DetectShapeKeyAsync("zzz"));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task DetectShapeKeyAsync_KeywordHit_ShortCircuits_BeforeEmbedding()
    {
        // If the keyword path hits, the embedder must not be consulted (and a throwing embedder proves it).
        var catalog = BuildCatalog(new ThrowingEmbedder());
        Assert.Equal("WindowRank", await catalog.DetectShapeKeyAsync("rank regions by ticket count"));
    }

    private sealed class ThrowingEmbedder : ITextEmbedder
    {
        public string ModelName => "throwing";
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("embedder must not be called on a keyword hit");
    }

    public static readonly string[] EnglishQuestionBank =
    {
        "show the running total of bills by month", "cumulative ticket count over time",
        "tickets month over month", "bills compared to the previous period", "revenue change vs last quarter",
        "rank regions by ticket count", "ranked customers by spend", "the median bill amount", "row_number over orders",
        "list the recursive hierarchy of regions", "all children of the north region", "the parent chain of ticket 10",
        "other tickets from the same customer as TKT-00020", "customers in the same region as Maya",
        "combined activity across tickets bills outages", "everything created today",
        "regions with more than 5 tickets and with more than 3 outages",
        "customers with at least 2 bills and at least 1 ticket",
        "regions with 5 tickets and more than 3 outages",
        "customers who have ever had a paid bill", "regions without any outages", "users who have at least one ticket",
        "how many tickets do we have", "list overdue bills", "top 5 customers by total billed amount", "tickets by status",
    };
}
