namespace SuperAdminCopilot.Execution;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// §10 Layer 2 of the abstraction guide. Wraps the inner executor with a SHA-256-keyed cache
/// over (compiled SQL + sorted parameter set). Errors are NEVER cached — only successful
/// result sets. TTL is configurable (default 60s, suitable for "today's data" questions).
///
/// This is registered as the <see cref="IExecutor"/> the orchestrator sees; the underlying
/// <see cref="ReadOnlyExecutor"/> is registered as a concrete type for the wrapper to call.
/// </summary>
internal sealed class CachingExecutor : IExecutor
{
    private readonly ReadOnlyExecutor _inner;
    private readonly IMemoryCache _cache;
    private readonly CopilotOptions _options;
    private readonly ILogger<CachingExecutor> _logger;

    private static readonly JsonSerializerOptions ParamHashOpts = new()
    {
        WriteIndented = false,
    };

    public CachingExecutor(
        ReadOnlyExecutor inner,
        IMemoryCache cache,
        IOptions<CopilotOptions> options,
        ILogger<CachingExecutor> logger)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableResultCache || _options.ResultCacheTtlSeconds <= 0)
        {
            var bypass = await _inner.ExecuteAsync(compiled, cancellationToken);
            return bypass with { CacheHit = false };
        }

        var key = ComputeCacheKey(compiled);
        if (_cache.TryGetValue(key, out ExecutionResult? cached) && cached is not null)
        {
            _logger.LogDebug("[SuperAdminCopilot] Result-cache HIT for key {Key}.", key[..16]);
            // Stamp CacheHit=true so the orchestrator's sql-execution typed payload shows the hit.
            return cached with { CacheHit = true };
        }

        var result = await _inner.ExecuteAsync(compiled, cancellationToken);

        // Don't cache errors — caller can retry, and a transient connection failure should not
        // stick for the TTL window. Empty result sets ARE cached (they represent a real answer).
        // Don't cache HUGE results — a million-row "show me all tickets" would dominate the
        // shared IMemoryCache and evict useful entries. Skip cache when the row count exceeds
        // a sensible threshold; the next caller pays the SQL cost but the cache stays useful
        // for the typical 5-500 row business questions.
        const int MaxCachedRowCount = 10_000;
        if (string.IsNullOrEmpty(result.Error) && result.RowCount <= MaxCachedRowCount)
        {
            _cache.Set(key, result, TimeSpan.FromSeconds(_options.ResultCacheTtlSeconds));
        }
        else if (result.RowCount > MaxCachedRowCount)
        {
            _logger.LogDebug(
                "[CachingExecutor] Skipping cache for large result ({Rows} rows > {Cap}). " +
                "Large queries are cheaper to re-run than to evict useful smaller results.",
                result.RowCount, MaxCachedRowCount);
        }
        return result with { CacheHit = false };
    }

    private static string ComputeCacheKey(CompiledSql compiled)
    {
        var sb = new StringBuilder(compiled.Sql.Length + 256);
        sb.Append(compiled.Sql);
        sb.Append('|');
        // Sort by key so {a:1,b:2} and {b:2,a:1} hash identically.
        foreach (var kv in compiled.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=');
            sb.Append(JsonSerializer.Serialize(kv.Value, ParamHashOpts));
            sb.Append(';');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
