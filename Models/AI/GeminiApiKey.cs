using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models.AI;

/// <summary>
/// One Gemini API key in the pool. The pool service rotates across these so a single key's daily
/// quota (~250 RPD on free tier) doesn't bottleneck the whole copilot. We track our own request
/// count per key (Google doesn't expose remaining quota in headers); when our counter hits the
/// safety ceiling or Google returns 429, we mark the key rate-limited and pick the next one.
/// </summary>
public class GeminiApiKey
{
    public int Id { get; set; }

    /// <summary>Friendly label shown in the UI ("Personal", "Work-G3", etc.). Required for the operator to tell keys apart.</summary>
    [Required, MaxLength(80)]
    public string Label { get; set; } = string.Empty;

    /// <summary>The raw API key. Stored as-is — same as the legacy SystemSettings:Gemini:ApiKey storage. Encrypt later if you ship multi-tenant.</summary>
    [Required, MaxLength(200)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex of <see cref="ApiKey"/>. Indexed so we can detect "you pasted the same key twice"
    /// in O(log n). Storing the hash (not just relying on ApiKey equality) keeps duplicate checks fast
    /// even if we ever encrypt ApiKey at rest.
    /// </summary>
    [MaxLength(64)]
    public string? KeyFingerprint { get; set; }

    /// <summary>
    /// Operator-supplied Google account email this key belongs to. We can NOT derive this from the key —
    /// Google's API doesn't expose it. The user types it when adding so the UI can show "Personal G3 ·
    /// you@gmail.com" and flag two keys from the same account (they share a 250 RPD bucket — pooling them
    /// gains nothing).
    /// </summary>
    [MaxLength(120)]
    public string? OwnerEmail { get; set; }

    /// <summary>When false, the pool selector skips this key entirely. Use to rotate keys out without deleting them.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Manual ordering hint when multiple keys have the same usage count. Lower = preferred.</summary>
    public int SortOrder { get; set; }

    /// <summary>Number of requests we've sent on this key since LastDailyResetUtc. Reset lazily when a new UTC day begins.</summary>
    public int DailyRequestCount { get; set; }

    /// <summary>UTC date our DailyRequestCount tracks. Lazy reset compares this to UtcNow.Date.</summary>
    public DateTime LastDailyResetUtc { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Last time we successfully used this key. Useful for "least-recently-used" ordering and the UI.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>When set in the future, the pool selector skips this key until UtcNow > value. Set on 429 from Google's retryDelay.</summary>
    public DateTime? RateLimitedUntilUtc { get; set; }

    /// <summary>Stored to surface in the UI ("last error: HTTP 401: invalid key"). Cleared on next success.</summary>
    [MaxLength(400)]
    public string? LastErrorMessage { get; set; }

    /// <summary>Counter for consecutive non-429 failures. Auto-disables the key (sets IsActive=false) at 5 strikes so a dead key doesn't keep getting picked.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
