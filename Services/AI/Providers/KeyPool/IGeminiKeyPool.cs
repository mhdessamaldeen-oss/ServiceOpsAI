using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI.Providers.KeyPool;

/// <summary>
/// Owns the multi-key rotation strategy for Gemini. The provider asks the pool for a key,
/// makes the call, then reports the outcome — pool state updates so the next acquire skips
/// keys that 429'd or burned through their daily quota.
///
/// Why a pool: Gemini's free tier caps each API key at ~250 requests/day. If you have multiple
/// Google accounts, pooling keys multiplies your effective daily budget proportionally.
///
/// Quota visibility caveat: Google's API does NOT return remaining-quota headers — we estimate
/// usage by counting our own requests. The "remaining" number you see in the UI is OUR counter,
/// authoritative only when no other client uses the same key.
/// </summary>
public interface IGeminiKeyPool
{
    /// <summary>
    /// Pick the next available key for an outbound call. Returns null if every key is either
    /// inactive, rate-limited, or has exhausted its daily count.
    /// Selection rule: prefer the key with the LOWEST DailyRequestCount among non-blocked keys
    /// (so usage spreads evenly). Ties broken by SortOrder then LastUsedAtUtc (LRU).
    /// </summary>
    Task<GeminiApiKey?> AcquireAsync(CancellationToken ct = default);

    /// <summary>Successful call: increment counter, refresh LastUsedAt, clear last error and consecutive failures.</summary>
    Task ReportSuccessAsync(int keyId, CancellationToken ct = default);

    /// <summary>HTTP 429: park the key for <paramref name="retryAfterMs"/> (Google's retryDelay + buffer). Pool will skip it until then.</summary>
    Task ReportRateLimitedAsync(int keyId, int retryAfterMs, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>Other failure (auth, network, parse). Increments ConsecutiveFailures; auto-disables at 5 strikes so a dead key stops getting picked.</summary>
    Task ReportFailureAsync(int keyId, string errorMessage, CancellationToken ct = default);

    /// <summary>UI snapshot: every key with its computed status, used count, estimated remaining, last error.</summary>
    Task<List<GeminiKeyStatus>> ListStatusesAsync(CancellationToken ct = default);

    /// <summary>
    /// Add a new key to the pool. Returns the result so the caller can show "added" vs "duplicate" UX.
    /// Duplicate detection is by SHA-256 fingerprint — pasting the same key twice (even under a
    /// different label) is rejected.
    /// </summary>
    Task<AddKeyResult> AddAsync(string label, string apiKey, string? ownerEmail = null, CancellationToken ct = default);

    /// <summary>Remove a key permanently.</summary>
    Task RemoveAsync(int keyId, CancellationToken ct = default);

    /// <summary>Toggle IsActive. Use to "park" a key without deleting it.</summary>
    Task SetActiveAsync(int keyId, bool active, CancellationToken ct = default);

    /// <summary>Manual override for the daily counter — useful if our estimate drifted from reality.</summary>
    Task ResetCountAsync(int keyId, CancellationToken ct = default);

    /// <summary>Update the friendly label.</summary>
    Task RenameAsync(int keyId, string newLabel, CancellationToken ct = default);
}

/// <summary>
/// Per-key status snapshot for the Settings UI. Sensitive bits (the actual API key) are masked
/// before this leaves the server.
/// </summary>
public sealed class GeminiKeyStatus
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>e.g. "AIza...XYZ" — first 4 + last 4 chars; never the whole secret.</summary>
    public string MaskedKey { get; set; } = string.Empty;
    /// <summary>Operator-supplied Google account email; null if they didn't fill it in.</summary>
    public string? OwnerEmail { get; set; }
    public bool IsActive { get; set; }
    public int DailyRequestCount { get; set; }
    /// <summary>Free-tier cap (250) minus DailyRequestCount, never below 0. Inferred — Google doesn't tell us the real number.</summary>
    public int EstimatedRemainingToday { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RateLimitedUntilUtc { get; set; }
    public string? LastErrorMessage { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>One of: "ok", "rate_limited", "exhausted", "disabled", "dead".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>True when another key in the pool has the same OwnerEmail. Surfaced by the UI as
    /// "shares quota with another key" so the operator knows pooling those two gains nothing.</summary>
    public bool SharesQuotaWithSibling { get; set; }
}

/// <summary>Outcome of <see cref="IGeminiKeyPool.AddAsync"/>.</summary>
public sealed class AddKeyResult
{
    public bool Added { get; set; }
    public int? KeyId { get; set; }
    /// <summary>When Added=false because the same key already exists, this is the existing key's ID.</summary>
    public int? DuplicateOfId { get; set; }
    public string? DuplicateLabel { get; set; }
    public string? Message { get; set; }
}
