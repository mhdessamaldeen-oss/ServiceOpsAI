namespace AnalystAgent.Pipeline.EntityResolution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Concrete <see cref="IFuzzyEntityResolver"/>. Tokenises the question into word, bigram,
/// and trigram phrases and queries <see cref="IValueIndex"/> for each. Returns the
/// highest-scoring resolved entities, de-duplicated by canonical value.
///
/// <para><b>Why bigrams and trigrams, not just words:</b> proper nouns are often
/// multi-word ("South Damascus", "Critical Priority"). Single-word tokens would miss
/// these. Capping at trigrams keeps the candidate set bounded — for an N-word question,
/// total candidates ≈ 3N, well under the index's linear scan budget.</para>
/// </summary>
internal sealed class FuzzyEntityResolver : IFuzzyEntityResolver
{
    private static readonly char[] WordSplitChars = { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '،', '؛', '؟' };

    private readonly IValueIndex _index;
    private readonly IOptionsMonitor<ValueIndexOptions> _options;
    private readonly ILogger<FuzzyEntityResolver> _logger;

    public FuzzyEntityResolver(
        IValueIndex index,
        IOptionsMonitor<ValueIndexOptions> options,
        ILogger<FuzzyEntityResolver> logger)
    {
        _index = index;
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyList<ResolvedEntity>> ResolveAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return Task.FromResult<IReadOnlyList<ResolvedEntity>>(Array.Empty<ResolvedEntity>());

        var opts = _options.CurrentValue;
        var phrases = ExtractPhrases(question);
        if (phrases.Count == 0)
            return Task.FromResult<IReadOnlyList<ResolvedEntity>>(Array.Empty<ResolvedEntity>());

        // For each phrase, query the index. Aggregate results then de-dup by canonical value
        // so the same DB value reached via different surface phrases isn't double-counted.
        var resolved = new Dictionary<string, ResolvedEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in phrases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = _index.Lookup(phrase, opts.TopK, opts.MinSimilarity);
            foreach (var hit in hits)
            {
                var key = $"{hit.Table}.{hit.Column}|{hit.Value}";
                // Keep the highest-similarity surface form for each canonical value.
                if (!resolved.TryGetValue(key, out var existing) || hit.Similarity > existing.Similarity)
                {
                    resolved[key] = new ResolvedEntity(
                        Surface: phrase,
                        Canonical: hit.Value,
                        Table: hit.Table,
                        Column: hit.Column,
                        Similarity: hit.Similarity);
                }
            }
        }

        var ordered = resolved.Values
            .OrderByDescending(r => r.Similarity)
            .ToList();

        if (ordered.Count > 0)
        {
            _logger.LogDebug("[FuzzyEntity] question '{Q}' → {N} entities (best: '{Best}'→'{Canon}' @ {Sim:F2})",
                question.Length > 50 ? question[..50] + "…" : question,
                ordered.Count, ordered[0].Surface, ordered[0].Canonical, ordered[0].Similarity);
        }

        return Task.FromResult<IReadOnlyList<ResolvedEntity>>(ordered);
    }

    // Extracts words + bigrams + trigrams from the question. Punctuation is stripped at the
    // word-split boundary; sub-word characters (including Arabic letters and combining marks)
    // are preserved within each token so "اسم-مركّب" stays one token if the user typed it that way.
    private static List<string> ExtractPhrases(string question)
    {
        var words = question
            .Split(WordSplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length >= 2) // skip 1-char stop-tokens
            .ToList();

        var phrases = new List<string>(words.Count * 3);
        // Single words
        phrases.AddRange(words);
        // Bigrams
        for (var i = 0; i < words.Count - 1; i++)
            phrases.Add($"{words[i]} {words[i + 1]}");
        // Trigrams
        for (var i = 0; i < words.Count - 2; i++)
            phrases.Add($"{words[i]} {words[i + 1]} {words[i + 2]}");

        return phrases;
    }
}
