namespace AnalystAgent.Retrieval;

/// <summary>
/// Single source of truth for vector math used by the semantic retrievers and verified-query
/// stores. Previously the same 8-line cosine-similarity dot-product was duplicated across
/// 5 sites (Schema/Column/Entity retrievers + PastQuestionStore + VerifiedQueryStore). Any
/// edge-case fix had to be applied 5x; this helper makes it one edit.
///
/// <para>Kept deliberately tiny and dependency-free so it can be inlined by JIT — the inner
/// loop runs millions of times per second across the ~440-column corpus and the cost matters.</para>
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// Cosine similarity between two equal-length vectors. Returns 0f on length mismatch,
    /// empty inputs, or either vector being zero-magnitude — same fail-safe semantics as the
    /// previous inline copies, so behavior is byte-identical to the pre-refactor state.
    /// </summary>
    public static float Cosine(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0) return 0f;
        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        if (na == 0f || nb == 0f) return 0f;
        var cos = (float)(dot / (System.Math.Sqrt(na) * System.Math.Sqrt(nb)));
        // NaN/Inf guard: a single NaN component (the Ollama embedder has been emitting NaN) would
        // otherwise poison the score and propagate through the whole ranking. Treat as no-similarity.
        return float.IsNaN(cos) || float.IsInfinity(cos) ? 0f : cos;
    }
}
