using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Services.AI.Providers.KeyPool;

/// <summary>
/// Multi-key rotation strategy for Groq, mirroring <see cref="IGeminiKeyPool"/>. Difference: Groq
/// returns real-time quota in `x-ratelimit-remaining-*` response headers — the pool persists those
/// snapshots so the UI can show authoritative remaining quota, not just our own counter.
/// </summary>
public interface IGroqKeyPool
{
    Task<GroqApiKey?> AcquireAsync(CancellationToken ct = default);

    /// <summary>Successful call — increment counter, refresh LastUsedAt, persist the rate-limit snapshot from response headers.</summary>
    Task ReportSuccessAsync(int keyId, GroqQuotaSnapshot? quotaSnapshot, CancellationToken ct = default);

    Task ReportRateLimitedAsync(int keyId, int retryAfterMs, string? errorMessage = null, CancellationToken ct = default);
    Task ReportFailureAsync(int keyId, string errorMessage, CancellationToken ct = default);
    Task<List<GroqKeyStatus>> ListStatusesAsync(CancellationToken ct = default);
    Task<AddKeyResult> AddAsync(string label, string apiKey, string? ownerEmail = null, CancellationToken ct = default);
    Task RemoveAsync(int keyId, CancellationToken ct = default);
    Task SetActiveAsync(int keyId, bool active, CancellationToken ct = default);
    Task ResetCountAsync(int keyId, CancellationToken ct = default);
    Task RenameAsync(int keyId, string newLabel, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of Groq's `x-ratelimit-*` response headers from a single API call. Persisted on the key
/// row so the UI can display current quota. All fields nullable — Groq may omit headers occasionally.
/// </summary>
public sealed class GroqQuotaSnapshot
{
    public int? RemainingRequests { get; set; }
    public long? RemainingTokens { get; set; }
    public int? LimitRequests { get; set; }
    public long? LimitTokens { get; set; }
}

public sealed class GroqKeyStatus
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string MaskedKey { get; set; } = string.Empty;
    public string? OwnerEmail { get; set; }
    public bool IsActive { get; set; }
    public int DailyRequestCount { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RateLimitedUntilUtc { get; set; }
    public string? LastErrorMessage { get; set; }
    public int ConsecutiveFailures { get; set; }

    public int? RemainingRequests { get; set; }
    public long? RemainingTokens { get; set; }
    public int? LimitRequests { get; set; }
    public long? LimitTokens { get; set; }
    public DateTime? QuotaSnapshotAtUtc { get; set; }

    /// <summary>One of: "ok", "rate_limited", "disabled", "dead".</summary>
    public string Status { get; set; } = "ok";

    public bool SharesQuotaWithSibling { get; set; }
}
