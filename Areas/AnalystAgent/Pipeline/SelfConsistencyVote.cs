namespace AnalystAgent.Pipeline;

using System.Globalization;
using AnalystAgent.Models;

/// <summary>
/// Pure (no I/O) fingerprint + majority-vote logic for execution-guided self-consistency. Each
/// candidate's RESULT SET is reduced to a stable string fingerprint; the candidates are then bucketed
/// by fingerprint and the largest bucket wins — provided it reaches the configured agreement floor.
/// Deterministic and schema-agnostic: it inspects only row counts and cell values, never table/column
/// names or domain vocabulary, so it is portable to any schema. Kept separate from
/// <see cref="DirectAnalystPath"/> so the vote can be unit-tested without the LLM/DB stack.
/// </summary>
internal static class SelfConsistencyVote
{
    /// <summary>Outcome of a vote. <see cref="WinnerIndex"/> is the index (into the candidate list) of a
    /// member of the winning bucket — null when no bucket reached the agreement floor (abstain).
    /// <see cref="Agreement"/> is the winning bucket's size; <see cref="Tally"/> is every distinct
    /// fingerprint with its count, for the trace.</summary>
    public sealed record VoteResult(int? WinnerIndex, int Agreement, IReadOnlyList<(string Fingerprint, int Count)> Tally);

    /// <summary>
    /// Reduce a (successful) execution result to a stable fingerprint string. A scalar / single-cell
    /// result fingerprints to its normalized cell; otherwise to the row count plus the ordered first row
    /// (columns sorted by name, each value normalized). This matches the design's intent: two candidates
    /// "agree" when they return the SAME answer shape and value(s), tolerant of float noise.
    /// </summary>
    public static string Fingerprint(ExecutionResult exec, int numericTolerance)
    {
        if (exec is null) return "∅";
        var rows = exec.Rows;
        // Scalar / single 1×1 cell — the dominant COUNT/SUM/scalar shape. Fingerprint the cell alone so
        // "42" agrees with "42" regardless of the (possibly differently-aliased) column name.
        if (rows is { Count: 1 } && rows[0].Count == 1)
        {
            var only = rows[0].Values.First();
            return "1|" + NormalizeCell(only, numericTolerance);
        }
        if (rows is null || rows.Count == 0)
            return "0|"; // empty result — a bucket of its own (and the loser in the non-empty tie-break)

        // Otherwise: row count + the ordered first row (column-name sorted, value normalized). Cheap and
        // discriminating — two candidates with the same count AND same lead row almost always returned the
        // same set; this avoids fingerprinting (and ordering) every row.
        var first = rows[0];
        var ordered = first.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => k + "=" + NormalizeCell(first[k], numericTolerance));
        return exec.RowCount.ToString(CultureInfo.InvariantCulture) + "|" + string.Join("", ordered);
    }

    /// <summary>Normalize a single cell value: invariant culture, trimmed, floats rounded to
    /// <paramref name="numericTolerance"/> decimals (so 4.0000 and 4.00004 collapse), nulls a uniform token.</summary>
    private static string NormalizeCell(object? value, int numericTolerance)
    {
        if (value is null || value is DBNull) return "\0null";
        switch (value)
        {
            case double d:  return RoundNum(d, numericTolerance);
            case float f:   return RoundNum(f, numericTolerance);
            case decimal m: return RoundNum((double)m, numericTolerance);
            default:
                var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                s = s.Trim();
                // A numeric-looking string is rounded too, so "4.0000" (text) agrees with 4.0 (double).
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    return RoundNum(parsed, numericTolerance);
                return s;
        }
    }

    private static string RoundNum(double d, int tolerance)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString(CultureInfo.InvariantCulture);
        var t = Math.Clamp(tolerance, 0, 15);
        return Math.Round(d, t, MidpointRounding.AwayFromZero).ToString("0." + new string('#', t), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Majority vote over candidate fingerprints. Returns the largest bucket's index when it reaches
    /// <paramref name="minAgreement"/>; otherwise abstains (WinnerIndex null). Ties between equal-size
    /// buckets break by, in order: (a) the bucket containing candidate 0 (the greedy attempt), (b) the
    /// bucket with the fewest lossy repairs, (c) a non-empty bucket over an empty one. All tie-break
    /// inputs are optional so the simple <c>Pick(fingerprints, minAgreement)</c> form is testable.
    /// </summary>
    public static VoteResult Pick(
        IReadOnlyList<string> fingerprints,
        int minAgreement,
        int? candidate0Index = null,
        IReadOnlyList<int>? lossyRepairCounts = null,
        IReadOnlyList<int>? rowCounts = null)
    {
        var tally = new List<(string Fingerprint, int Count)>();
        if (fingerprints is null || fingerprints.Count == 0)
            return new VoteResult(null, 0, tally);

        // Bucket candidate indices by fingerprint, preserving first-seen order for stable tallies.
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i < fingerprints.Count; i++)
        {
            var fp = fingerprints[i] ?? string.Empty;
            if (!buckets.TryGetValue(fp, out var members)) { buckets[fp] = members = new List<int>(); order.Add(fp); }
            members.Add(i);
        }
        foreach (var fp in order) tally.Add((fp, buckets[fp].Count));

        var maxSize = buckets.Values.Max(b => b.Count);
        if (maxSize < Math.Max(1, minAgreement))
            return new VoteResult(null, maxSize, tally);   // no bucket agreed enough → abstain

        // Candidate winning buckets (all of the max size), in first-seen order.
        var topBuckets = order.Where(fp => buckets[fp].Count == maxSize).ToList();
        string winningFp;
        if (topBuckets.Count == 1)
        {
            winningFp = topBuckets[0];
        }
        else
        {
            // (a) the bucket containing candidate 0.
            var withCand0 = candidate0Index is int c0
                ? topBuckets.FirstOrDefault(fp => buckets[fp].Contains(c0))
                : null;
            if (withCand0 is not null)
            {
                winningFp = withCand0;
            }
            else
            {
                // (b) fewest lossy repairs (min across the bucket's members), then
                // (c) non-empty over empty (an empty result fingerprints as "0|" or "1|<null>"-free → use rowCounts).
                winningFp = topBuckets
                    .OrderBy(fp => MinLossy(buckets[fp], lossyRepairCounts))
                    .ThenByDescending(fp => MaxRows(buckets[fp], rowCounts))
                    .ThenBy(fp => order.IndexOf(fp))   // final stable tiebreak: first-seen
                    .First();
            }
        }

        var winnerIndex = buckets[winningFp][0];
        return new VoteResult(winnerIndex, maxSize, tally);
    }

    private static int MinLossy(IReadOnlyList<int> members, IReadOnlyList<int>? lossy)
    {
        if (lossy is null) return 0;
        var min = int.MaxValue;
        foreach (var idx in members)
            if (idx >= 0 && idx < lossy.Count) min = Math.Min(min, lossy[idx]);
        return min == int.MaxValue ? 0 : min;
    }

    private static int MaxRows(IReadOnlyList<int> members, IReadOnlyList<int>? rows)
    {
        if (rows is null) return 0;
        var max = 0;
        foreach (var idx in members)
            if (idx >= 0 && idx < rows.Count) max = Math.Max(max, rows[idx]);
        return max;
    }
}
