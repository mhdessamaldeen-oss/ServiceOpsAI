namespace AnalystAgent.Semantic;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Tools;

/// <summary>
/// Embedding-based external-tool selector — the semantic twin of
/// <see cref="EntityEmbeddingMatcher"/> for the tool-routing path. Where the legacy
/// <c>ToolHandler.ScoreCandidates</c> matches the question against a tool's keyword hints with
/// stopword-filtered token overlap + fuzzy ratios, this selector embeds the question once and
/// cosine-ranks every enabled tool's NAME + DESCRIPTION + keyword hints. That makes routing driven
/// by the tool's MEANING (admin-editable DB rows), not by lexical keyword luck — a question that
/// matches a tool's purpose with zero overlapping keywords still routes correctly.
///
/// <para><b>No hardcoded vocabulary.</b> Every signal comes from the admin-editable
/// <see cref="ToolDefinition"/> rows (Title / Description / KeywordHints). There is no per-tool or
/// per-domain literal in this class — it is fully portable to another deployment whose tool set is
/// entirely different.</para>
///
/// <para><b>Caching.</b> Tool vectors are primed once and held via <see cref="IMemoryCache"/> (no
/// expiry). The cache key is <c>model name + a content hash of the enabled tool set</c>, so two
/// things invalidate the cache automatically: swapping the embedder model, and an admin editing any
/// tool's title/description/keywords/key (the content hash changes → fresh priming on next call).</para>
///
/// <para><b>Fail-open.</b> When the embedder is unavailable (<see cref="ITextEmbedder.EmbedAsync"/>
/// returns an empty vector) or no tools are enabled, <see cref="RankAsync"/> returns an empty list —
/// the caller (<c>ToolHandler</c>) falls back to the legacy lexical scorer unchanged.</para>
/// </summary>
public interface IToolSemanticSelector
{
    /// <summary>True when the embedder is available and at least one enabled tool exists.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Embed <paramref name="question"/> once and cosine-rank <paramref name="tools"/> by semantic
    /// closeness to each tool's label (title + description + keywords). Returns the tools ordered by
    /// descending cosine. Empty list when the embedder is down or no tools were supplied — callers
    /// MUST treat that as "no semantic signal" and fail open to the lexical path.
    /// </summary>
    Task<IReadOnlyList<(ToolDefinition Tool, float Score)>> RankAsync(
        string question, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default);
}

internal sealed class ToolSemanticSelector : IToolSemanticSelector
{
    private const string CachePrefix = "analyst-agent::tool-embeddings::";

    private readonly IToolRegistry _registry;
    private readonly ITextEmbedder _embedder;
    private readonly ILogger<ToolSemanticSelector> _logger;
    private readonly EmbeddingVectorCache<ToolDefinition> _vectorCache;

    public ToolSemanticSelector(
        IToolRegistry registry,
        ITextEmbedder embedder,
        IMemoryCache cache,
        ILogger<ToolSemanticSelector> logger)
    {
        _registry = registry;
        _embedder = embedder;
        _logger = logger;
        // Compose the shared prime+rank+fail-open cache. This selector keeps its own label builder
        // (BuildToolLabel) and cache-key builder (model name + content hash of the enabled tool set).
        _vectorCache = new EmbeddingVectorCache<ToolDefinition>(embedder, cache, logger);
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_embedder.ModelName) && _registry.IsAvailable;

    public async Task<IReadOnlyList<(ToolDefinition Tool, float Score)>> RankAsync(
        string question, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || tools is null || tools.Count == 0)
            return Array.Empty<(ToolDefinition, float)>();

        // Cache key folds in the embedder model name + a content hash of the enabled tool set, so an admin
        // edit (title / description / keyword / key) OR a model swap auto-invalidates — the next call re-primes.
        var cacheKey = CachePrefix + (_embedder.ModelName ?? "default") + "::" + ComputeToolSetHash(tools);

        // Shared prime + cosine-rank + fail-open. Returns scored pairs in input order; sort descending here.
        var scored = await _vectorCache.PrimeAndRankAsync(tools, BuildToolLabel, cacheKey, question, cancellationToken);
        if (scored.Count == 0) return Array.Empty<(ToolDefinition, float)>();

        var ordered = new List<(ToolDefinition Tool, float Score)>(scored);
        ordered.Sort((a, b) => b.Score.CompareTo(a.Score));
        return ordered;
    }

    /// <summary>Compose a single vector-friendly string from the tool. Description is the
    /// discriminative middle (the bare title/key is often just a brand word); keyword hints add the
    /// admin's hand-picked routing terms. Empty parts are skipped cleanly so a tool with no
    /// description still produces a usable label.</summary>
    internal static string BuildToolLabel(ToolDefinition t)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(t.Title)) parts.Add(t.Title.Trim());
        if (!string.IsNullOrWhiteSpace(t.Description)) parts.Add(t.Description.Trim());
        if (!string.IsNullOrWhiteSpace(t.KeywordHints)) parts.Add(t.KeywordHints.Trim());
        return string.Join(" — ", parts);
    }

    /// <summary>Stable content hash of the enabled tool set — concatenate each tool's
    /// key|title|description|keywords and SHA-256 it. Any admin edit changes the hash, which changes
    /// the cache key, which forces a re-prime. Order-independent of nothing else matters: the
    /// registry returns tools in a stable SortOrder, but to be safe we sort the per-tool lines so a
    /// pure re-ordering doesn't needlessly invalidate.</summary>
    private static string ComputeToolSetHash(IReadOnlyList<ToolDefinition> tools)
    {
        var lines = tools
            .Select(t => $"{t.ToolKey}|{t.Title}|{t.Description}|{t.KeywordHints}")
            .OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join("\n", lines);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes);
    }
}
