using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models.AI;

/// <summary>
/// One Groq API key in the pool. Groq is OpenAI-compatible and exposes real-time quota state via
/// `x-ratelimit-remaining-*` response headers — unlike Gemini where we have to estimate. We persist
/// the latest snapshot of those headers per key so the UI can show authoritative remaining-quota,
/// not just our own counter.
/// </summary>
public class GroqApiKey
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Label { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>SHA-256 hex fingerprint of <see cref="ApiKey"/> for fast duplicate detection.</summary>
    [MaxLength(64)]
    public string? KeyFingerprint { get; set; }

    /// <summary>Operator-supplied account email for UI display and "shares quota" detection.</summary>
    [MaxLength(120)]
    public string? OwnerEmail { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Our own counter — used for fairness rotation. Authoritative quota lives in the headers below.</summary>
    public int DailyRequestCount { get; set; }
    public DateTime LastDailyResetUtc { get; set; } = DateTime.UtcNow.Date;

    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>Set on HTTP 429; pool selector skips this key until UtcNow > this value.</summary>
    public DateTime? RateLimitedUntilUtc { get; set; }

    [MaxLength(400)]
    public string? LastErrorMessage { get; set; }

    public int ConsecutiveFailures { get; set; }

    // ── Real quota snapshot (from Groq's response headers — captured on every successful call) ──

    /// <summary>Remaining requests in the current minute window (from `x-ratelimit-remaining-requests`).</summary>
    public int? RemainingRequests { get; set; }

    /// <summary>Remaining tokens in the current minute window (from `x-ratelimit-remaining-tokens`).</summary>
    public long? RemainingTokens { get; set; }

    /// <summary>Per-minute request ceiling Groq advertised on the last call (from `x-ratelimit-limit-requests`). Helps the UI show "X / Y left".</summary>
    public int? LimitRequests { get; set; }

    /// <summary>Per-minute token ceiling Groq advertised on the last call (from `x-ratelimit-limit-tokens`).</summary>
    public long? LimitTokens { get; set; }

    /// <summary>UTC instant when <see cref="RemainingRequests"/> was captured — so the UI can fade stale data.</summary>
    public DateTime? QuotaSnapshotAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
