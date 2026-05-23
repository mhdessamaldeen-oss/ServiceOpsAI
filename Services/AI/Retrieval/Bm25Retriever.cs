namespace ServiceOpsAI.Services.AI.Retrieval
{
    /// <summary>
    /// Standard Okapi BM25 — the algorithm Elasticsearch / Lucene have shipped for
    /// 25 years. In-memory, single-pass implementation; deliberately not pluggable
    /// (k1/b are well-studied defaults and don't need UI exposure).
    /// </summary>
    public sealed class Bm25Retriever : IBm25Retriever
    {
        // Classical BM25 hyperparameters. k1 controls term-frequency saturation;
        // b controls length normalization. The 1.5 / 0.75 pairing is the original
        // Robertson/Walker recommendation and matches Lucene's defaults.
        private const float K1 = 1.5f;
        private const float B = 0.75f;

        public IReadOnlyList<Bm25Result> Score(
            IReadOnlyList<Bm25Document> documents,
            IReadOnlyCollection<string> queryTerms)
        {
            if (documents.Count == 0 || queryTerms.Count == 0)
            {
                return documents.Select(d => new Bm25Result(d.Key, 0f)).ToList();
            }

            var n = documents.Count;
            var docLengths = new int[n];
            double totalLen = 0;
            for (var i = 0; i < n; i++)
            {
                docLengths[i] = documents[i].Terms.Count;
                totalLen += docLengths[i];
            }
            var avgDocLen = totalLen / n;

            // Document frequency for each query term — how many docs contain at least one occurrence.
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in queryTerms)
            {
                if (df.ContainsKey(term)) continue;
                var count = 0;
                for (var i = 0; i < n; i++)
                {
                    if (documents[i].Terms.Contains(term))
                    {
                        count++;
                    }
                }
                df[term] = count;
            }

            // IDF using the BM25-standard "plus" smoothing so single-doc-frequency
            // terms still get a non-negative weight.
            var idf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (term, frequency) in df)
            {
                idf[term] = Math.Log(1.0 + ((n - frequency + 0.5) / (frequency + 0.5)));
            }

            var rawScores = new float[n];
            for (var i = 0; i < n; i++)
            {
                var tfMap = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var term in documents[i].Terms)
                {
                    tfMap.TryGetValue(term, out var existing);
                    tfMap[term] = existing + 1;
                }

                double score = 0;
                var docLen = docLengths[i];
                foreach (var term in queryTerms)
                {
                    if (!tfMap.TryGetValue(term, out var tf) || tf == 0) continue;
                    var termIdf = idf[term];
                    var numerator = tf * (K1 + 1);
                    var denominator = tf + (K1 * (1 - B + (B * docLen / Math.Max(1.0, avgDocLen))));
                    score += termIdf * (numerator / denominator);
                }

                rawScores[i] = (float)score;
            }

            // Normalize against the max in the candidate set so consumers can
            // compare to a cosine score (also in [0,1]) without per-corpus calibration.
            var maxScore = rawScores.Length == 0 ? 0f : rawScores.Max();
            var results = new List<Bm25Result>(n);
            for (var i = 0; i < n; i++)
            {
                var normalized = maxScore > 0 ? rawScores[i] / maxScore : 0f;
                results.Add(new Bm25Result(documents[i].Key, Math.Clamp(normalized, 0f, 1f)));
            }
            return results;
        }
    }
}
