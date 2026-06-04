namespace AnalystAgent.Abstractions;

/// <summary>
/// Per-question embedding cache. The same question text gets embedded up to 5× per call
/// today — once each by VectorRetriever, SchemaSemanticRetriever, VerifiedQueryStore,
/// PastQuestionStore, plus the post-save embedding writeback. Each embedder call is
/// 200ms-5s on a cloud provider and the costliest single thing on the response path.
///
/// <para>This scope intercepts every <see cref="ITextEmbedder.EmbedAsync"/> call within
/// a question's async-local boundary and memoises by (text, model). First caller computes
/// the embedding; subsequent callers read from the dict at zero latency. Survives
/// <c>Task.WhenAll</c> fan-out (decomposer parallelism) via <c>AsyncLocal</c> propagation.</para>
///
/// <para>Scope lifetime = one user question. Re-used across sub-questions when the parent
/// orchestrator opens the scope; isolated from other concurrent questions. Disposes pop
/// the AsyncLocal pointer back to whatever was active before <c>Begin()</c>.</para>
/// </summary>
public sealed class QuestionEmbeddingScope : IDisposable
{
    private static readonly AsyncLocal<QuestionEmbeddingScope?> _current = new();
    private readonly QuestionEmbeddingScope? _parent;
    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public static QuestionEmbeddingScope? Current => _current.Value;

    public static QuestionEmbeddingScope Begin() => new();

    private QuestionEmbeddingScope()
    {
        _parent = _current.Value;
        _current.Value = this;
    }

    /// <summary>Look up an embedding by (text, model). Returns null on miss; caller must
    /// compute and call <see cref="Set"/>.</summary>
    public float[]? TryGet(string text, string model)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var key = Key(text, model);
        lock (_cache) return _cache.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Store an embedding for future retrieval. Cheap dictionary write; no eviction
    /// (scope dies with the question, ~10s typical lifetime).</summary>
    public void Set(string text, string model, float[] embedding)
    {
        if (string.IsNullOrEmpty(text) || embedding is null) return;
        var key = Key(text, model);
        lock (_cache) _cache[key] = embedding;
    }

    private static string Key(string text, string model) => $"{model}::{text}";

    public void Dispose()
    {
        if (_current.Value == this) _current.Value = _parent;
    }
}
