namespace AnalystAgent.Pipeline.EntityResolution;

/// <summary>
/// Trigram (3-gram) similarity scorer used as the fuzzy-match metric inside
/// <see cref="IValueIndex"/>. Same algorithm PostgreSQL's <c>pg_trgm</c> extension uses,
/// chosen because it:
/// <list type="bullet">
///   <item>Handles typos better than pure Levenshtein for short strings (district / status names).</item>
///   <item>Works equally well on Arabic and Latin scripts — character-level, not language-aware.</item>
///   <item>Is symmetric and bounded (0.0-1.0), so similarity thresholds are intuitive.</item>
///   <item>Is cheap — O(n+m) per pair after one-time trigram extraction.</item>
/// </list>
///
/// <para><b>Score formula:</b> Jaccard similarity over the trigram sets:
/// <c>|trigrams(a) ∩ trigrams(b)| / |trigrams(a) ∪ trigrams(b)|</c>. Both strings are
/// padded with two leading and two trailing spaces before extracting trigrams, so prefix
/// and suffix matches contribute (matches PostgreSQL's behaviour).</para>
/// </summary>
public static class TrigramSimilarity
{
    /// <summary>Compute Jaccard similarity over trigram sets. Returns 0.0 for empty input,
    /// 1.0 for identical strings (after case folding).</summary>
    public static double Compute(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;

        var aTrigrams = ExtractTrigrams(a);
        var bTrigrams = ExtractTrigrams(b);
        if (aTrigrams.Count == 0 || bTrigrams.Count == 0) return 0.0;

        var intersection = 0;
        foreach (var trigram in aTrigrams)
        {
            if (bTrigrams.Contains(trigram)) intersection++;
        }

        var union = aTrigrams.Count + bTrigrams.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>Pre-extract trigrams once for an index entry, so per-query lookup avoids
    /// re-tokenising the candidate side. The <see cref="IValueIndex"/> implementation
    /// stores trigram sets alongside each value.</summary>
    public static HashSet<string> ExtractTrigrams(string text)
    {
        var trigrams = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return trigrams;

        // Pad with two spaces on each side — matches pg_trgm. Lets prefix/suffix matches
        // contribute their share without the user having to type the leading/trailing chars
        // exactly.
        var padded = "  " + text.ToLowerInvariant() + "  ";
        for (var i = 0; i <= padded.Length - 3; i++)
        {
            trigrams.Add(padded.Substring(i, 3));
        }
        return trigrams;
    }
}
