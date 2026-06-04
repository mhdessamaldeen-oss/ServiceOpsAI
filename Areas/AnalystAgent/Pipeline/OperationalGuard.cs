namespace AnalystAgent.Pipeline;

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;

/// <summary>
/// §8 operational guards from the abstraction guide. Two responsibilities:
///   - <b>Kill switch</b>: a global flag (config-driven) that disables all LLM calls. Set to
///     true to stop the copilot dead in an incident.
///   - <b>Rate limit</b>: per-conversation (or global when no conversation) sliding-window cap
///     to prevent runaway scripted abuse. Cheap, in-memory; not multi-instance safe.
///
/// Both are checked at the very start of the orchestrator's pipeline so no LLM tokens or DB
/// connections are wasted when the system is locked down.
/// </summary>
public interface IOperationalGuard
{
    /// <summary>
    /// Returns null when the request is allowed; otherwise a refusal reason that the
    /// orchestrator surfaces as a clean response (no LLM call, no DB call).
    /// </summary>
    string? CheckOrRefuse(string? conversationId);
}

internal sealed class OperationalGuard : IOperationalGuard
{
    private readonly AnalystOptions _options;

    /// <summary>
    /// Sliding-window log of recent request timestamps per bucket key. Keyed by conversationId
    /// (or "global" when no id is supplied). One entry per request; we trim to the rate window
    /// on each check.
    /// </summary>
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);

    /// <summary>How often (in checks) to sweep idle buckets out of <see cref="_hits"/>.</summary>
    private const long SweepEveryNChecks = 200;
    private long _checkCount;

    public OperationalGuard(IOptions<AnalystOptions> options) => _options = options.Value;

    public string? CheckOrRefuse(string? conversationId)
    {
        if (_options.KillSwitch)
            return "the copilot is currently disabled by an operator (kill switch). Try again later.";

        if (_options.RateLimitMaxRequestsPerWindow <= 0 || _options.RateLimitWindowSeconds <= 0)
            return null; // rate limit disabled

        var key = string.IsNullOrEmpty(conversationId) ? "global" : conversationId!;
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSeconds);
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTime>());

        string? refusal = null;
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
                queue.Dequeue();

            if (queue.Count >= _options.RateLimitMaxRequestsPerWindow)
            {
                var retryIn = window - (now - queue.Peek());
                refusal = $"rate limit exceeded ({_options.RateLimitMaxRequestsPerWindow} requests per {_options.RateLimitWindowSeconds}s). Retry in ~{Math.Ceiling(retryIn.TotalSeconds)}s.";
            }
            else
            {
                queue.Enqueue(now);
            }
        }

        // Bound memory: every distinct conversationId would otherwise leave a Queue in _hits forever.
        // Periodically drop buckets whose window has fully elapsed (idle conversations).
        if (Interlocked.Increment(ref _checkCount) % SweepEveryNChecks == 0)
            SweepIdleBuckets(now, window);

        return refusal;
    }

    /// <summary>
    /// Removes buckets that have no live hits in the current window. The KeyValuePair overload of
    /// TryRemove only deletes when the value reference is unchanged, so a bucket repopulated
    /// concurrently is left intact (worst case: a single in-flight hit is lost on a tight race —
    /// a harmless under-count, never a crash or a leak).
    /// </summary>
    private void SweepIdleBuckets(DateTime now, TimeSpan window)
    {
        foreach (var kvp in _hits)
        {
            bool idle;
            lock (kvp.Value)
            {
                while (kvp.Value.Count > 0 && now - kvp.Value.Peek() > window)
                    kvp.Value.Dequeue();
                idle = kvp.Value.Count == 0;
            }
            if (idle)
                _hits.TryRemove(kvp);
        }
    }
}
