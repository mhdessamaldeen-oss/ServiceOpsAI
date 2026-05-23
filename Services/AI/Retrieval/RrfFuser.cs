namespace ServiceOpsAI.Services.AI.Retrieval
{
    public sealed class RrfFuser : IRrfFuser
    {
        public IReadOnlyList<RrfResult> Fuse(
            IReadOnlyList<IReadOnlyList<(string Key, int Rank)>> rankings,
            int k)
        {
            if (rankings.Count == 0)
            {
                return Array.Empty<RrfResult>();
            }

            // k must be positive; the Cormack paper recommends ~60 as a default
            // because it dampens the rank-1 vs rank-2 gap on small candidate sets.
            var kSafe = Math.Max(1, k);
            var fused = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var ranking in rankings)
            {
                foreach (var (key, rank) in ranking)
                {
                    fused.TryGetValue(key, out var existing);
                    fused[key] = existing + (1.0 / (kSafe + rank));
                }
            }

            var max = fused.Count == 0 ? 0.0 : fused.Values.Max();
            if (max <= 0)
            {
                return fused.Select(kv => new RrfResult(kv.Key, 0f)).ToList();
            }

            return fused
                .Select(kv => new RrfResult(kv.Key, (float)Math.Clamp(kv.Value / max, 0.0, 1.0)))
                .OrderByDescending(r => r.Score)
                .ToList();
        }
    }
}
