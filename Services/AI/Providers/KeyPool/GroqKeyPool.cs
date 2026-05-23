using System.Security.Cryptography;
using System.Text;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Services.AI.Providers.KeyPool;

/// <summary>
/// EF-backed implementation of <see cref="IGroqKeyPool"/>. Selection rule: prefer the key with the
/// most remaining requests in its current minute window (per Groq's headers); fall back to lowest
/// DailyRequestCount when headers are stale. Mirrors GeminiKeyPool but uses Groq's real quota data.
/// </summary>
public sealed class GroqKeyPool : IGroqKeyPool
{
    private const int DeadKeyThreshold = 5;
    private const int MaxErrorMessageLength = 380;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GroqKeyPool> _logger;

    public GroqKeyPool(IServiceProvider serviceProvider, ILogger<GroqKeyPool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private (IServiceScope Scope, ApplicationDbContext Db) OpenDb()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (scope, db);
    }

    public async Task<GroqApiKey?> AcquireAsync(CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var now = DateTime.UtcNow;

        var candidates = await db.GroqApiKeys.Where(k => k.IsActive).ToListAsync(ct);

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

        // Available = active and not currently parked
        var available = candidates
            .Where(k => k.RateLimitedUntilUtc == null || k.RateLimitedUntilUtc <= now)
            .Where(k => k.RemainingRequests == null || k.RemainingRequests > 0)
            .OrderByDescending(k => k.RemainingRequests ?? int.MaxValue) // prefer keys with confirmed headroom
            .ThenBy(k => k.DailyRequestCount)
            .ThenBy(k => k.SortOrder)
            .ThenBy(k => k.LastUsedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();

        if (available == null && candidates.Any())
        {
            _logger.LogWarning(
                "GroqKeyPool: all {Count} active keys are rate-limited or out of quota.",
                candidates.Count);
        }

        return available;
    }

    public async Task ReportSuccessAsync(int keyId, GroqQuotaSnapshot? quotaSnapshot, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        key.DailyRequestCount++;
        key.LastUsedAtUtc = DateTime.UtcNow;
        key.LastErrorMessage = null;
        key.ConsecutiveFailures = 0;
        key.RateLimitedUntilUtc = null;

        if (quotaSnapshot != null)
        {
            key.RemainingRequests = quotaSnapshot.RemainingRequests;
            key.RemainingTokens = quotaSnapshot.RemainingTokens;
            key.LimitRequests = quotaSnapshot.LimitRequests;
            key.LimitTokens = quotaSnapshot.LimitTokens;
            key.QuotaSnapshotAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ReportRateLimitedAsync(int keyId, int retryAfterMs, string? errorMessage = null, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        var safeMs = Math.Min(Math.Max(retryAfterMs, 1_000), 24 * 60 * 60 * 1000);
        key.RateLimitedUntilUtc = DateTime.UtcNow.AddMilliseconds(safeMs);
        key.LastErrorMessage = TruncateError(errorMessage);

        await db.SaveChangesAsync(ct);
    }

    public async Task ReportFailureAsync(int keyId, string errorMessage, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;

        key.LastErrorMessage = TruncateError(errorMessage);
        key.ConsecutiveFailures++;
        if (key.ConsecutiveFailures >= DeadKeyThreshold)
        {
            key.IsActive = false;
            _logger.LogWarning("GroqKeyPool: key '{Label}' (Id={Id}) auto-disabled after {Count} consecutive failures.",
                key.Label, key.Id, key.ConsecutiveFailures);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<GroqKeyStatus>> ListStatusesAsync(CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var now = DateTime.UtcNow;

        var keys = await db.GroqApiKeys.OrderBy(k => k.SortOrder).ThenBy(k => k.Id).ToListAsync(ct);

        var emailsWithDuplicates = keys
            .Where(k => !string.IsNullOrWhiteSpace(k.OwnerEmail))
            .GroupBy(k => k.OwnerEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys.Select(k => new GroqKeyStatus
        {
            Id = k.Id,
            Label = k.Label,
            MaskedKey = MaskKey(k.ApiKey),
            OwnerEmail = k.OwnerEmail,
            IsActive = k.IsActive,
            DailyRequestCount = k.LastDailyResetUtc.Date < now.Date ? 0 : k.DailyRequestCount,
            LastUsedAtUtc = k.LastUsedAtUtc,
            RateLimitedUntilUtc = k.RateLimitedUntilUtc,
            LastErrorMessage = k.LastErrorMessage,
            ConsecutiveFailures = k.ConsecutiveFailures,
            RemainingRequests = k.RemainingRequests,
            RemainingTokens = k.RemainingTokens,
            LimitRequests = k.LimitRequests,
            LimitTokens = k.LimitTokens,
            QuotaSnapshotAtUtc = k.QuotaSnapshotAtUtc,
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

        var existing = await db.GroqApiKeys
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

        var maxOrder = await db.GroqApiKeys.MaxAsync(k => (int?)k.SortOrder, ct) ?? 0;

        var entry = new GroqApiKey
        {
            Label = label.Trim(),
            ApiKey = trimmedKey,
            KeyFingerprint = fingerprint,
            OwnerEmail = string.IsNullOrWhiteSpace(ownerEmail) ? null : ownerEmail.Trim().ToLowerInvariant(),
            IsActive = true,
            SortOrder = maxOrder + 1,
            LastDailyResetUtc = DateTime.UtcNow.Date
        };
        db.GroqApiKeys.Add(entry);
        await db.SaveChangesAsync(ct);
        return new AddKeyResult { Added = true, KeyId = entry.Id, Message = "Key added to the pool." };
    }

    public async Task RemoveAsync(int keyId, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        db.GroqApiKeys.Remove(key);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int keyId, bool active, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        key.IsActive = active;
        if (active) key.ConsecutiveFailures = 0;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetCountAsync(int keyId, CancellationToken ct = default)
    {
        var (_scope, db) = OpenDb();
        using var __scope = _scope;
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
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
        var key = await db.GroqApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key == null) return;
        key.Label = newLabel.Trim();
        await db.SaveChangesAsync(ct);
    }

    private static string ComputeFingerprint(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string MaskKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return "—";
        if (apiKey.Length <= 8) return new string('•', apiKey.Length);
        return $"{apiKey[..4]}…{apiKey[^4..]}";
    }

    private static string? TruncateError(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;
        return msg.Length <= MaxErrorMessageLength ? msg : msg.Substring(0, MaxErrorMessageLength - 3) + "...";
    }

    private static string ComputeStatus(GroqApiKey key, DateTime now)
    {
        if (!key.IsActive) return key.ConsecutiveFailures >= DeadKeyThreshold ? "dead" : "disabled";
        if (key.RateLimitedUntilUtc.HasValue && key.RateLimitedUntilUtc > now) return "rate_limited";
        return "ok";
    }
}
