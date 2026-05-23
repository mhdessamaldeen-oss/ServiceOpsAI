namespace SuperAdminCopilot.Pipeline;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

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
    private readonly CopilotOptions _options;

    /// <summary>
    /// Sliding-window log of recent request timestamps per bucket key. Keyed by conversationId
    /// (or "global" when no id is supplied). One entry per request; we trim to the rate window
    /// on each check.
    /// </summary>
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);

    public OperationalGuard(IOptions<CopilotOptions> options) => _options = options.Value;

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

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
                queue.Dequeue();

            if (queue.Count >= _options.RateLimitMaxRequestsPerWindow)
            {
                var retryIn = window - (now - queue.Peek());
                return $"rate limit exceeded ({_options.RateLimitMaxRequestsPerWindow} requests per {_options.RateLimitWindowSeconds}s). Retry in ~{Math.Ceiling(retryIn.TotalSeconds)}s.";
            }
            queue.Enqueue(now);
        }
        return null;
    }
}
