using System.Security.Cryptography;
using System.Text;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI.Providers.KeyPool;

/// <summary>
/// EF-backed implementation of <see cref="IGeminiKeyPool"/>. All state lives in the GeminiApiKeys
/// table — no in-memory cache, so multiple app instances would share state correctly.
/// Daily counter resets are lazy (on each AcquireAsync) so we don't need a background job.
///
/// Thread safety note: SaveChangesAsync handles concurrent updates to the same row. Two parallel
/// requests grabbing the same lowest-count key will both succeed (one wins the increment race);
/// in the worst case we slightly over-use a key and slightly under-use its sibling — corrects
/// itself on the next pass.
/// </summary>
public sealed class GeminiKeyPool : IGeminiKeyPool
{
    private const int DailyRequestCeiling = 240; // safety margin under Google's ~250 RPD cap
    private const int DeadKeyThreshold = 5;      // consecutive non-429 failures → auto-disable
    private const int MaxErrorMessageLength = 380; // matches GeminiApiKey.LastErrorMessage [MaxLength(400)] with safety margin

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GeminiKeyPool> _logger;

    public GeminiKeyPool(IServiceProvider serviceProvider, ILogger<GeminiKeyPool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// The pool is registered as a Singleton (it carries no instance state of its own — all state
    /// is in the DB) but the DbContext is Scoped. We open a fresh scope per call to bridge the
    /// lifetime mismatch. Same pattern <see cref="GeminiAiProvider"/> uses to read SystemSettings.
    /// </summary>
    private (IServiceScope Scope, ApplicationDbContext Db) OpenDb()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (scope, db);
    }

    public async Task<GeminiApiKey?> AcquireAsync(CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var now = DateTime.UtcNow;

        var candidates = await db.GeminiApiKeys
            .Where(k => k.IsActive)
            .ToListAsync(ct);

        // Lazy daily reset — if our LastDailyResetUtc is from a previous UTC day, zero the counter.
        var resetCount = 0;
        foreach (var key in candidates)
        {
            if (key.LastDailyResetUtc.Date < now.Date)
            {
                key.DailyRequestCount = 0;
                key.LastDailyResetUtc = now.Date;
                resetCount++;
            }
        }
        if (resetCount > 0) await db.SaveChangesAsync(ct);

        var available = candidates
            .Where(k => k.RateLimitedUntilUtc == null || k.RateLimitedUntilUtc <= now)
            .Where(k => k.DailyRequestCount < DailyRequestCeiling)
            .OrderBy(k => k.DailyRequestCount)
            .ThenBy(k => k.SortOrder)
            .ThenBy(k => k.LastUsedAtUtc ?? DateTime.MinValue) // LRU tiebreaker
            .FirstOrDefault();

        if (available == null && candidates.Any())
        {
            _logger.LogWarning(
                "GeminiKeyPool: all {Count} active keys are rate-limited or exhausted. " +
                "Counts: [{Counts}]",
                candidates.Count,
                string.Join(", ", candidates.Select(k => $"{k.Label}={k.DailyRequestCount}")));
        }

        return available;
    }

    public async Task ReportSuccessAsync(int keyId, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        key.DailyRequestCount++;
        key.LastUsedAtUtc = DateTime.UtcNow;
        key.LastErrorMessage = null;
        key.ConsecutiveFailures = 0;
        // Successful call clears any stale rate-limit timestamp (Google may have lifted us early).
        key.RateLimitedUntilUtc = null;

        await db.SaveChangesAsync(ct);
    }

    public async Task ReportRateLimitedAsync(int keyId, int retryAfterMs, string? errorMessage = null, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        // Clamp absurd values: Google sometimes returns multi-hour delays which mean "daily quota"
        // — we still park the key but cap stored duration at 24h so the row reset doesn't get stuck.
        var safeMs = Math.Min(Math.Max(retryAfterMs, 1_000), 24 * 60 * 60 * 1000);
        key.RateLimitedUntilUtc = DateTime.UtcNow.AddMilliseconds(safeMs);
        key.LastErrorMessage = TruncateError(errorMessage);
        // 429 doesn't count toward "dead key" because the key itself is fine.

        await db.SaveChangesAsync(ct);
    }

    public async Task ReportFailureAsync(int keyId, string errorMessage, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        key.LastErrorMessage = TruncateError(errorMessage);
        key.ConsecutiveFailures++;
        if (key.ConsecutiveFailures >= DeadKeyThreshold)
        {
            // Auto-disable: an invalid/revoked key shouldn't keep getting picked.
            key.IsActive = false;
            _logger.LogWarning("GeminiKeyPool: key '{Label}' (Id={Id}) auto-disabled after {Count} consecutive failures.",
                key.Label, key.Id, key.ConsecutiveFailures);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<GeminiKeyStatus>> ListStatusesAsync(CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var now = DateTime.UtcNow;

        var keys = await db.GeminiApiKeys
            .OrderBy(k => k.SortOrder)
            .ThenBy(k => k.Id)
            .ToListAsync(ct);

        // Pre-compute which emails are shared by 2+ keys so the UI can flag them. Two keys from the
        // same Google account share the same daily 250 RPD bucket, so pooling them gains nothing —
        // the operator should know.
        var emailsWithDuplicates = keys
            .Where(k => !string.IsNullOrWhiteSpace(k.OwnerEmail))
            .GroupBy(k => k.OwnerEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys.Select(k => new GeminiKeyStatus
        {
            Id = k.Id,
            Label = k.Label,
            MaskedKey = MaskKey(k.ApiKey),
            OwnerEmail = k.OwnerEmail,
            IsActive = k.IsActive,
            DailyRequestCount = k.LastDailyResetUtc.Date < now.Date ? 0 : k.DailyRequestCount,
            EstimatedRemainingToday = Math.Max(0, DailyRequestCeiling - (k.LastDailyResetUtc.Date < now.Date ? 0 : k.DailyRequestCount)),
            LastUsedAtUtc = k.LastUsedAtUtc,
            RateLimitedUntilUtc = k.RateLimitedUntilUtc,
            LastErrorMessage = k.LastErrorMessage,
            ConsecutiveFailures = k.ConsecutiveFailures,
            Status = ComputeStatus(k, now),
            SharesQuotaWithSibling = !string.IsNullOrWhiteSpace(k.OwnerEmail) && emailsWithDuplicates.Contains(k.OwnerEmail!)
        }).ToList();
    }

    public async Task<AddKeyResult> AddAsync(string label, string apiKey, string? ownerEmail = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key cannot be empty.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(label)) label = $"Key {DateTime.UtcNow:yyyy-MM-dd HH:mm}";

        var trimmedKey = apiKey.Trim();
        var fingerprint = ComputeFingerprint(trimmedKey);

        var (_scope, db) = OpenDb();
        using var __scope = _scope;

        // Duplicate check by fingerprint — prevents the operator from accidentally adding the same
        // key twice (which would double-count its quota and waste a row).
        var existing = await db.GeminiApiKeys
            .Where(k => k.KeyFingerprint == fingerprint)
            .Select(k => new { k.Id, k.Label })
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            return new AddKeyResult
            {
                Added = false,
                DuplicateOfId = existing.Id,
                DuplicateLabel = existing.Label,
                Message = $"This key is already in the pool as \"{existing.Label}\". Adding it again would double-count its quota."
            };
        }

        var maxOrder = await db.GeminiApiKeys.MaxAsync(k => (int?)k.SortOrder, ct) ?? 0;

        var entry = new GeminiApiKey
        {
            Label = label.Trim(),
            ApiKey = trimmedKey,
            KeyFingerprint = fingerprint,
            OwnerEmail = string.IsNullOrWhiteSpace(ownerEmail) ? null : ownerEmail.Trim().ToLowerInvariant(),
            IsActive = true,
            SortOrder = maxOrder + 1,
            LastDailyResetUtc = DateTime.UtcNow.Date
        };
        db.GeminiApiKeys.Add(entry);
        await db.SaveChangesAsync(ct);
        return new AddKeyResult { Added = true, KeyId = entry.Id, Message = "Key added to the pool." };
    }

    /// <summary>SHA-256 hex fingerprint of the API key. Same input → same hash; never reversible.</summary>
    private static string ComputeFingerprint(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task RemoveAsync(int keyId, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        db.GeminiApiKeys.Remove(key);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int keyId, bool active, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        key.IsActive = active;
        if (active) key.ConsecutiveFailures = 0; // re-enabling implies operator expects it to work
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetCountAsync(int keyId, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        key.DailyRequestCount = 0;
        key.LastDailyResetUtc = DateTime.UtcNow.Date;
        key.RateLimitedUntilUtc = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(int keyId, string newLabel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        key.Label = newLabel.Trim();
        await db.SaveChangesAsync(ct);
    }

    private static string? TruncateError(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;
        return msg.Length <= MaxErrorMessageLength ? msg : msg.Substring(0, MaxErrorMessageLength - 3) + "...";
    }

    private static string MaskKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return "—";
        if (apiKey.Length <= 8) return new string('•', apiKey.Length);
        return $"{apiKey[..4]}…{apiKey[^4..]}";
    }

    private static string ComputeStatus(GeminiApiKey key, DateTime now)
    {
        if (!key.IsActive) return key.ConsecutiveFailures >= DeadKeyThreshold ? "dead" : "disabled";
        if (key.RateLimitedUntilUtc.HasValue && key.RateLimitedUntilUtc > now) return "rate_limited";
        var todayCount = key.LastDailyResetUtc.Date < now.Date ? 0 : key.DailyRequestCount;
        if (todayCount >= DailyRequestCeiling) return "exhausted";
        return "ok";
    }
}
