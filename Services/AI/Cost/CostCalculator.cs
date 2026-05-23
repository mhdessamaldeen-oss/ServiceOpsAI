using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ServiceOpsAI.Services.AI.Cost
{
    /// <summary>
    /// Computes USD cost for an LLM call from a (Provider, Model) tuple plus the call's
    /// prompt + completion token counts. Rates are loaded from the <see cref="ModelPricing"/>
    /// table and cached in-memory; the cache invalidates after <see cref="CacheTtlSeconds"/>
    /// so admin edits land without an app restart.
    /// <para>Cost = <c>(prompt/1000) * InputPer1K + (completion/1000) * OutputPer1K</c>. When
    /// no pricing row exists, returns 0 with <see cref="ResolvedRate"/> = null — useful for
    /// the chat UI to show "—" instead of a misleading $0.00 (which could be a free local
    /// model OR a missing config).</para>
    /// </summary>
    public interface ICostCalculator
    {
        Task<CostEstimate> EstimateAsync(string provider, string? model, int promptTokens, int completionTokens, CancellationToken cancellationToken = default);

        /// <summary>Drop every cached pricing row. Called by the admin pricing CRUD page
        /// after a Save / Delete so the next cost lookup sees the fresh rate without waiting
        /// for the 60-second TTL.</summary>
        void InvalidateAll();
    }

    public sealed record CostEstimate(decimal CostUsd, ModelPricing? ResolvedRate);

    internal sealed class CostCalculator : ICostCalculator
    {
        private const int CacheTtlSeconds = 60;
        // Monotonic generation counter that gets bumped on every InvalidateAll(); the cache
        // key embeds the current generation so old entries simply never get hit again. Cheaper
        // than iterating the cache to evict (IMemoryCache doesn't expose enumeration).
        // <para><b>Note on scope:</b> the field is <c>static</c> on a service registered as
        // scoped. This is intentional — pricing edits affect the entire process, not just one
        // request scope. The trade-off: if two hosting scenarios (live server + integration
        // test) share an AppDomain, a price edit in one bumps the counter for the other. Not
        // a functional bug (everyone re-reads from DB on next access), just a non-obvious
        // global side effect of admin actions.</para>
        private static long _generation;
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CostCalculator> _logger;

        public CostCalculator(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            IMemoryCache cache,
            ILogger<CostCalculator> logger)
        {
            _contextFactory = contextFactory;
            _cache = cache;
            _logger = logger;
        }

        public void InvalidateAll()
        {
            // Bump the generation; future ResolveRateAsync calls will compute new cache keys
            // and miss the cache, forcing a fresh DB read. Old entries age out at their TTL.
            Interlocked.Increment(ref _generation);
        }

        public async Task<CostEstimate> EstimateAsync(string provider, string? model, int promptTokens, int completionTokens, CancellationToken cancellationToken = default)
        {
            var rate = await ResolveRateAsync(provider, model, cancellationToken);
            if (rate is null) return new CostEstimate(0m, null);
            var cost =
                ((decimal)promptTokens / 1000m) * rate.InputPer1K
              + ((decimal)completionTokens / 1000m) * rate.OutputPer1K;
            return new CostEstimate(cost, rate);
        }

        private async Task<ModelPricing?> ResolveRateAsync(string provider, string? model, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(provider)) return null;
            var gen = Interlocked.Read(ref _generation);
            var key = $"modelpricing::g{gen}::{provider.ToLowerInvariant()}::{(model ?? "").ToLowerInvariant()}";
            if (_cache.TryGetValue(key, out ModelPricing? cached)) return cached;

            try
            {
                using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
                // Single round-trip: pull every active row for this provider that's a candidate
                // (exact model OR wildcard / empty fallback), then prefer exact in memory. The
                // provider+model index makes this one seek; the candidate list is at most a
                // handful per provider.
                var modelKey = model ?? string.Empty;
                var candidates = await ctx.ModelPricings.AsNoTracking()
                    .Where(p => p.IsActive
                                && p.Provider == provider
                                && (p.Model == modelKey || p.Model == "*" || p.Model == ""))
                    .ToListAsync(cancellationToken);

                // Prefer exact match; fall back to wildcard. OrderBy on a small in-memory list
                // is cheaper than a second query.
                var hit = candidates
                    .OrderBy(p => p.Model == modelKey ? 0 : 1)
                    .FirstOrDefault();

                _cache.Set(key, hit, TimeSpan.FromSeconds(CacheTtlSeconds));
                return hit;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CostCalculator] failed to resolve rate for {Provider}/{Model}; treating as 0.", provider, model);
                return null;
            }
        }
    }
}
