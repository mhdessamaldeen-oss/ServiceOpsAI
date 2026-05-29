using System;

namespace ServiceOpsAI.Data.Seed;

/// <summary>
/// Guarantees seed rows land in every named temporal bucket — today, yesterday,
/// this-week, last-week, this-month, last-month, last-quarter, this-year,
/// last-year, two-years-ago — so questions like "tickets this week" or
/// "bills last month" always return data after a fresh seed.
///
/// Usage: pass the row index inside the entity's seed loop. The first
/// <c>Windows.Length × RowsPerBucket</c> rows are placed deterministically into
/// each bucket; remaining rows fall back to a uniform random offset.
/// </summary>
internal static class TemporalBuckets
{
    // Minimum rows guaranteed in each bucket. With modulo cycling, the first
    // Windows.Length × RowsPerBucket rows distribute one-per-bucket per round,
    // so every bucket is reached even when N is small (e.g. 30 outages).
    public const int RowsPerBucket = 3;

    // (MinDaysAgo, MaxDaysAgo) windows. Modulo-cycled by row index so a loop
    // of N >= Windows.Length rows hits every reporting period.
    private static readonly (int MinDays, int MaxDays)[] Windows =
    {
        (0,   1),   // today
        (1,   2),   // yesterday
        (2,   7),   // this week (other day)
        (7,   14),  // last week
        (14,  30),  // earlier this month
        (30,  60),  // last month
        (60,  120), // last quarter
        (120, 300), // this year (earlier)
        (300, 540), // last year
        (540, 730), // two years ago
    };

    public static int AnchoredRowCount => Windows.Length * RowsPerBucket;

    /// <summary>
    /// Returns a past UTC timestamp. The first <see cref="AnchoredRowCount"/>
    /// calls cycle through every bucket (modulo), so a loop of size
    /// <paramref name="rowIndex"/>=0..N is guaranteed to hit every bucket as
    /// soon as N ≥ <see cref="Windows"/>.Length. Subsequent calls fall back to
    /// a uniform random offset 1..<paramref name="maxDaysAgo"/> days.
    /// </summary>
    public static DateTime PickPast(Random rng, int rowIndex, int maxDaysAgo = 730)
    {
        var now = DateTime.UtcNow;
        int daysAgo;

        if (rowIndex < AnchoredRowCount)
        {
            var bucket = Windows[rowIndex % Windows.Length];   // modulo cycle
            var span = Math.Max(1, bucket.MaxDays - bucket.MinDays);
            daysAgo = bucket.MinDays + rng.Next(0, span);
        }
        else
        {
            daysAgo = rng.Next(1, Math.Max(2, maxDaysAgo));
        }

        // Anchor to the START of the target calendar day, then pick a random
        // time inside it. This prevents hour/minute jitter from accidentally
        // rolling the timestamp back into the previous day (which is why
        // "tickets yesterday" was returning 0 before).
        if (daysAgo == 0)
        {
            // Today — pick a time from midnight up to NOW so we don't write a
            // future timestamp.
            var secondsElapsedToday = Math.Max(1, (int)(now - now.Date).TotalSeconds);
            return now.Date.AddSeconds(rng.Next(0, secondsElapsedToday));
        }

        var targetDay = now.Date.AddDays(-daysAgo);
        return targetDay.AddSeconds(rng.Next(0, 86400));
    }

    /// <summary>
    /// Same distribution as <see cref="PickPast"/> but returns the offset only
    /// (positive integer = days ago). Useful when the caller manages its own
    /// reference date.
    /// </summary>
    public static int PickPastDaysAgo(Random rng, int rowIndex, int maxDaysAgo = 730)
    {
        if (rowIndex < AnchoredRowCount)
        {
            var bucket = Windows[rowIndex % Windows.Length];
            var span = Math.Max(1, bucket.MaxDays - bucket.MinDays);
            return bucket.MinDays + rng.Next(0, span);
        }
        return rng.Next(1, Math.Max(2, maxDaysAgo));
    }
}
