namespace SuperAdminCopilot.Configuration;

using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Retrieval;

/// <summary>
/// Config-backed source of truth for the raw-SQL escape valve's advanced-shape knowledge:
/// (1) the keyword grammar that routes a question to an advanced shape, and (2) the gold-SQL
/// worked example shown per shape. Externalized 2026-06-02 from <c>LlmDirectSqlEmitter</c> so a new
/// DB/schema can be targeted WITHOUT recompiling.
///
/// <para><b>Multilingual / paraphrase-robust detection (2026-06-02):</b>
/// <see cref="DetectShapeKeyAsync"/> is keyword-first, then EMBEDDING-similarity fallback. When no
/// keyword matches (a paraphrase, or a language with no keywords yet), it embeds the question and
/// each shape's keyword signature (via <see cref="ITextEmbedder"/>) and picks the best shape whose
/// cosine similarity exceeds <see cref="CopilotOptions.AdvancedShapeEmbeddingThreshold"/> — reusing
/// <see cref="VectorMath.Cosine"/>. This replaces English-keyword-only matching with semantics, so
/// Arabic and unseen phrasings still route to the right worked example. The sync
/// <see cref="DetectShapeKey"/> remains for the keyword-only fast path.</para>
///
/// <para><b>Behavior contract (mirrors <see cref="EmbeddingKeywordsOptions"/>):</b> when a file is
/// ABSENT the emitter falls back to its in-code defaults (byte-identical to the shipped files). When
/// present it is authoritative. Never throws; a malformed file or a failed embed degrades to "no
/// example" (graceful), identical to a keyword miss today.</para>
/// </summary>
public interface IAdvancedShapeCatalog
{
    /// <summary>True when <c>shape-examples.json</c> was loaded. When false, callers use the in-code fallback.</summary>
    bool ExamplesFilePresent { get; }

    /// <summary>The worked-SQL example for <paramref name="shapeKey"/> (an <c>AdvancedShape</c> enum name),
    /// or empty string when the file is present but lacks that shape.</summary>
    string ExampleFor(string shapeKey);

    /// <summary>True when <c>advanced-shape-keywords.json</c> was loaded. When false, callers use the in-code fallback.</summary>
    bool KeywordsFilePresent { get; }

    /// <summary>Keyword-only detection (ordered, first-match-wins). Returns null when nothing matches.</summary>
    string? DetectShapeKey(string question);

    /// <summary>Keyword-first, then embedding-similarity fallback. Returns the matching advanced-shape
    /// key for <paramref name="question"/>, or null when neither path is confident.</summary>
    Task<string?> DetectShapeKeyAsync(string question, CancellationToken cancellationToken = default);
}

internal sealed class AdvancedShapeCatalog : IAdvancedShapeCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lazy<ExamplesData> _examples;
    private readonly Lazy<KeywordsData> _keywords;
    private readonly ITextEmbedder _embedder;
    private readonly float _embeddingThreshold;
    private readonly ILogger<AdvancedShapeCatalog> _logger;

    // Per-shape signature embeddings, computed once on first embedding-fallback use.
    private volatile IReadOnlyDictionary<string, float[]>? _shapeEmbeddings;
    private readonly SemaphoreSlim _embedGate = new(1, 1);

    public AdvancedShapeCatalog(
        IOptions<CopilotOptions> options,
        ITextEmbedder embedder,
        ILogger<AdvancedShapeCatalog> logger)
    {
        _embedder = embedder;
        _logger = logger;
        _embeddingThreshold = options.Value.AdvancedShapeEmbeddingThreshold;
        _examples = new Lazy<ExamplesData>(() => LoadExamples(options.Value.ShapeExamplesPath, logger));
        _keywords = new Lazy<KeywordsData>(() => LoadKeywords(options.Value.AdvancedShapeKeywordsPath, logger));
    }

    public bool ExamplesFilePresent => _examples.Value.Present;

    public string ExampleFor(string shapeKey)
        => _examples.Value.ByShape.TryGetValue(shapeKey, out var sql) ? sql : "";

    public bool KeywordsFilePresent => _keywords.Value.Present;

    public string? DetectShapeKey(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        foreach (var rule in _keywords.Value.Rules)
        {
            if (rule.Regex is not null && rule.Regex.Matches(q).Count >= rule.RegexMinCount)
                return rule.Shape;
            foreach (var kw in rule.Keywords)
                if (q.Contains(kw, StringComparison.Ordinal))
                    return rule.Shape;
        }
        return null;
    }

    public async Task<string?> DetectShapeKeyAsync(string question, CancellationToken cancellationToken = default)
    {
        // 1. Fast, deterministic keyword path (handles the languages that have keywords).
        var keyworded = DetectShapeKey(question);
        if (keyworded is not null) return keyworded;

        // 2. Embedding-similarity fallback — catches paraphrases and languages without keywords.
        if (string.IsNullOrWhiteSpace(question) || _embeddingThreshold <= 0f) return null;
        if (_keywords.Value.Rules.Count == 0) return null;

        IReadOnlyDictionary<string, float[]> shapeVecs;
        try
        {
            shapeVecs = await EnsureShapeEmbeddingsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdvancedShapeCatalog] shape-signature embedding unavailable; skipping embedding fallback.");
            return null;
        }
        if (shapeVecs.Count == 0) return null;

        float[] questionVec;
        try
        {
            questionVec = await _embedder.EmbedAsync(question, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AdvancedShapeCatalog] question embedding failed; skipping embedding fallback.");
            return null;
        }

        string? best = null;
        var bestScore = _embeddingThreshold; // must strictly exceed the threshold to win
        foreach (var (shape, vec) in shapeVecs)
        {
            var score = VectorMath.Cosine(questionVec, vec);
            if (score > bestScore)
            {
                bestScore = score;
                best = shape;
            }
        }
        if (best is not null)
            _logger.LogDebug("[AdvancedShapeCatalog] embedding fallback matched shape {Shape} (cosine={Score:F3}).", best, bestScore);
        return best;
    }

    /// <summary>Embed each shape's keyword signature once (bilingual keywords → cross-lingual vector).
    /// Cached for process lifetime; an embed failure for one shape just omits it.</summary>
    private async Task<IReadOnlyDictionary<string, float[]>> EnsureShapeEmbeddingsAsync(CancellationToken ct)
    {
        var cached = _shapeEmbeddings;
        if (cached is not null) return cached;

        await _embedGate.WaitAsync(ct);
        try
        {
            if (_shapeEmbeddings is not null) return _shapeEmbeddings;
            var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var rule in _keywords.Value.Rules)
            {
                if (rule.Keywords.Count == 0) continue;
                var signature = string.Join(", ", rule.Keywords);
                try { map[rule.Shape] = await _embedder.EmbedAsync(signature, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AdvancedShapeCatalog] failed to embed signature for shape {Shape}; it won't be matchable by similarity.", rule.Shape);
                }
            }
            _shapeEmbeddings = map;
            return map;
        }
        finally
        {
            _embedGate.Release();
        }
    }

    // ── Loading ──────────────────────────────────────────────────────────────────────

    private static ExamplesData LoadExamples(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogInformation("[AdvancedShapeCatalog] {File} not found; using in-code shape examples.", path);
            return ExamplesData.Absent;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<ShapeExamplesFile>(stream, JsonOpts);
            var dict = file?.Examples ?? new Dictionary<string, string>();
            logger.LogInformation("[AdvancedShapeCatalog] Loaded {Count} shape example(s) from {File}.", dict.Count, path);
            return new ExamplesData(true, dict);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdvancedShapeCatalog] Failed to load {File}; using in-code fallback.", path);
            return ExamplesData.Absent;
        }
    }

    private static KeywordsData LoadKeywords(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogInformation("[AdvancedShapeCatalog] {File} not found; using in-code shape detection.", path);
            return KeywordsData.Absent;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<ShapeKeywordsFile>(stream, JsonOpts);
            var rules = new List<CompiledRule>();
            foreach (var s in file?.Shapes ?? new List<ShapeKeywordRule>())
            {
                Regex? rx = null;
                int minCount = 0;
                if (s.RegexAtLeastCount is not null && !string.IsNullOrEmpty(s.RegexAtLeastCount.Pattern))
                {
                    rx = new Regex(s.RegexAtLeastCount.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                    minCount = s.RegexAtLeastCount.MinCount;
                }
                rules.Add(new CompiledRule(s.Shape, s.Keywords ?? new List<string>(), rx, minCount));
            }
            logger.LogInformation("[AdvancedShapeCatalog] Loaded {Count} shape-keyword rule(s) from {File}.", rules.Count, path);
            return new KeywordsData(true, rules);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdvancedShapeCatalog] Failed to load {File}; using in-code fallback.", path);
            return KeywordsData.Absent;
        }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    // ── Internal shapes ──────────────────────────────────────────────────────────────

    private sealed record ExamplesData(bool Present, IReadOnlyDictionary<string, string> ByShape)
    {
        public static readonly ExamplesData Absent = new(false, new Dictionary<string, string>());
    }

    private sealed record KeywordsData(bool Present, IReadOnlyList<CompiledRule> Rules)
    {
        public static readonly KeywordsData Absent = new(false, Array.Empty<CompiledRule>());
    }

    private sealed record CompiledRule(string Shape, IReadOnlyList<string> Keywords, Regex? Regex, int RegexMinCount);

    // JSON DTOs
    private sealed class ShapeExamplesFile { public Dictionary<string, string> Examples { get; set; } = new(); }
    private sealed class ShapeKeywordsFile { public List<ShapeKeywordRule> Shapes { get; set; } = new(); }
    private sealed class ShapeKeywordRule
    {
        public string Shape { get; set; } = "";
        public List<string> Keywords { get; set; } = new();
        public RegexCountRule? RegexAtLeastCount { get; set; }
    }
    private sealed class RegexCountRule { public string Pattern { get; set; } = ""; public int MinCount { get; set; } }
}
