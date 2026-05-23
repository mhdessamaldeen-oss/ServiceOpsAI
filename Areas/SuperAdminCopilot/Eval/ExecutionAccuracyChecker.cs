namespace SuperAdminCopilot.Eval;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

/// <summary>
/// Industry-standard <b>Execution Accuracy (EX)</b> check used by the Assessment Lab and any
/// other consumer that needs to verify "did the copilot's SQL produce the same data as the
/// curated expected SQL?". Order-independent: rows are compared as a multiset (canonical-sort keys,
/// numeric rounding to 4 places, dates normalised to ISO-8601, nulls normalised).
/// </summary>
/// <remarks>
/// <para>Stronger than row-count or column-shape checks alone. Two queries can return the same
/// row count and same column list while producing entirely different data; this comparator
/// catches that.</para>
/// <para>Extracted from the now-retired <c>GoldenSetRunner</c> so it's reusable from the
/// Assessment Lab UI path, eval routes, ad-hoc admin tools, or test harnesses.</para>
/// </remarks>
public interface IExecutionAccuracyChecker
{
    /// <summary>
    /// Run <paramref name="expectedSql"/> against the read-only DB and compare its result set
    /// against <paramref name="copilotRows"/> as a multiset. Returns the verdict + the expected
    /// row count for reporting.
    /// </summary>
    Task<ExecutionAccuracyResult> CheckAsync(
        string expectedSql,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? copilotRows,
        CancellationToken cancellationToken = default);
}

/// <summary>Verdict + diagnostics. <see cref="Match"/> is null when the check couldn't run
/// (e.g. expected SQL threw, or copilot produced no rows to compare to).</summary>
public sealed record ExecutionAccuracyResult(
    bool? Match,
    int ExpectedRowCount,
    int CopilotRowCount,
    string? Error);

internal sealed class ExecutionAccuracyChecker : IExecutionAccuracyChecker
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<ExecutionAccuracyChecker> _logger;

    public ExecutionAccuracyChecker(
        IDbConnectionFactory dbFactory,
        IOptions<CopilotOptions> options,
        ILogger<ExecutionAccuracyChecker> logger)
    {
        _dbFactory = dbFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<ExecutionAccuracyResult> CheckAsync(
        string expectedSql,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? copilotRows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSql))
            return new ExecutionAccuracyResult(null, 0, copilotRows?.Count ?? 0, "no expected SQL");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> expectedRows;
        try
        {
            expectedRows = await ExecuteExpectedSqlAsync(expectedSql, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExecutionAccuracy] expected SQL failed to execute");
            return new ExecutionAccuracyResult(null, 0, copilotRows?.Count ?? 0, ex.Message);
        }

        var copilot = copilotRows ?? Array.Empty<IReadOnlyDictionary<string, object?>>();
        var match = CompareResultSets(expectedRows, copilot);
        if (!match && (expectedRows.Count > 0 || copilot.Count > 0))
        {
            // Diagnostic: emit the first 3 canonical rows of each side so an operator can
            // see why the multiset compare returned false. Only fires on the unhappy path.
            var goldPreview = expectedRows.Take(3).Select(CanonicalizeRow);
            var copPreview = copilot.Take(3).Select(CanonicalizeRow);
            _logger.LogWarning(
                "[ExecutionAccuracy] MISMATCH gold={GoldCount}/copilot={CopilotCount}. gold[0..3]: {Gold} ;; copilot[0..3]: {Copilot}",
                expectedRows.Count, copilot.Count,
                string.Join(" || ", goldPreview),
                string.Join(" || ", copPreview));
        }
        return new ExecutionAccuracyResult(match, expectedRows.Count, copilot.Count, null);
    }

    /// <summary>
    /// Execute the curated expected SQL against the live DB using the same connection factory the
    /// copilot's read-only executor uses. Same MaxRows cap to keep the check fair.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteExpectedSqlAsync(
        string expectedSql, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var conn = _dbFactory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = expectedSql;
        cmd.CommandTimeout = _options.Value.CommandTimeoutSeconds;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= _options.Value.MaxRows) break;
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Multiset comparison with bidirectional column-subset tolerance. Matches when copilot's
    /// row values either contain all of gold's per-row values (copilot projects an extra
    /// column like Title), OR copilot's per-row values are all contained in gold's (gold
    /// projects an extra column like Id that the user didn't ask for). Each gold row must
    /// match exactly one copilot row (1:1 pairing preserved by `matched[]`), so a copilot
    /// result with 1 row of {5,"extra"} won't false-match a gold with 2 rows of {5}.
    /// </summary>
    private static bool CompareResultSets(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> gold,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> copilot)
    {
        if (gold.Count != copilot.Count) return false;
        if (gold.Count == 0) return true;

        // Exact-match fast path: if both sides have identical multisets of full-row signatures,
        // we're done. Common case — both gold and copilot have the same column count.
        var expectedBag = BuildCanonicalBag(gold);
        var copilotBag = BuildCanonicalBag(copilot);
        if (BagsEqual(expectedBag, copilotBag)) return true;

        // Subset paths: handle projection asymmetry. The user's question might be served by
        // gold SQL with more columns than copilot emits (gold projects Id+CreatedAt+Title+Number,
        // copilot projects CreatedAt+Title+Number — semantically the same answer), or vice
        // versa. Try copilot⊆gold first, then gold⊆copilot. The 1:1 pairing in `matched[]`
        // prevents a single copilot row from satisfying multiple distinct gold rows.
        var goldValues = gold.Select(SortedRowValues).ToList();
        var copilotValues = copilot.Select(SortedRowValues).ToList();

        // Path 1 — gold ⊆ copilot (copilot projects extra columns, e.g. added Title)
        if (TryMatchPairing(goldValues, copilotValues)) return true;

        // Path 2 — copilot ⊆ gold (gold projects extra columns the user didn't ask for —
        // e.g. gold has Id but copilot's projection is just Title+Number). This is the
        // common "realistic-suite gold is broader than what the user asked for" case.
        if (TryMatchPairing(copilotValues, goldValues)) return true;

        return false;
    }

    // For each `small` row, find an unmatched `large` row whose values contain `small`'s.
    // Returns true when every `small` is paired exactly once. O(N*M) worst-case; fine for
    // benchmark row counts (capped at MaxRows in expected-SQL execution).
    private static bool TryMatchPairing(
        IReadOnlyList<IReadOnlyList<string>> small,
        IReadOnlyList<IReadOnlyList<string>> large)
    {
        var matched = new bool[large.Count];
        foreach (var smallRow in small)
        {
            bool found = false;
            for (int j = 0; j < large.Count; j++)
            {
                if (matched[j]) continue;
                if (IsSubMultiset(smallRow, large[j])) { matched[j] = true; found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    private static bool BagsEqual(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, count) in a)
            if (!b.TryGetValue(key, out var other) || other != count) return false;
        return true;
    }

    // Returns row values normalized + sorted alphabetically. Used by the superset path.
    private static IReadOnlyList<string> SortedRowValues(IReadOnlyDictionary<string, object?> row)
    {
        return row.Values.Select(NormalizeValue).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // Is `small` a multiset-subset of `large`? Both are sorted ascending. Walk linearly.
    private static bool IsSubMultiset(IReadOnlyList<string> small, IReadOnlyList<string> large)
    {
        if (small.Count > large.Count) return false;
        int i = 0, j = 0;
        while (i < small.Count && j < large.Count)
        {
            var cmp = string.Compare(small[i], large[j], StringComparison.Ordinal);
            if (cmp == 0) { i++; j++; }
            else if (cmp > 0) { j++; }
            else return false; // small[i] is less than large[j] — won't appear later
        }
        return i == small.Count;
    }

    private static Dictionary<string, int> BuildCanonicalBag(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var bag = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var canonical = CanonicalizeRow(row);
            bag[canonical] = bag.TryGetValue(canonical, out var n) ? n + 1 : 1;
        }
        return bag;
    }

    private static string CanonicalizeRow(IReadOnlyDictionary<string, object?> row)
    {
        // Compare rows by VALUE multiset (per row), not by column-name keys. Two queries
        // returning the same data but using different aliases — gold `AS Count` vs copilot
        // `AS ActiveUserCount`, gold `AS Priority` vs copilot `AS Name` — must still match.
        // Per-row value sort preserves row identity (gold row {5, 'Open'} doesn't false-match
        // copilot row {3, 'Open'}); column count is implicit in the values list. Result:
        // alias-independent, position-independent, but value-faithful comparison.
        var values = row.Values
            .Select(NormalizeValue)
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join("|", values);
    }

    private static string NormalizeValue(object? value)
    {
        if (value is null or DBNull) return "__null__";

        // Gold rows arrive with native CLR types from the DB reader; copilot rows arrive as
        // strings (the bridge flattens StructuredRows via .ToString() for transport). To make
        // the multiset compare correctly, both sides must canonicalize to the SAME form. We
        // handle typed values first, then try to parse strings back into a known type so
        // gold's `DateTime 2026-05-14` and copilot's `"5/14/2026 12:00:00 AM"` both become
        // the same ISO-8601 string. Without this round-trip, every DateTime / numeric column
        // produces a false-negative multiset mismatch even when the SQL is identical.
        if (value is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto) return DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture);
        // Convert all fractional types to double then format — keeps the typed-gold side and the
        // string-copilot side identical. Mixing decimal's scale-preserving "G" ("20.2000") with
        // double's "G" ("20.2") was the B06 p95 root cause.
        if (value is decimal dec) return Math.Round((double)dec, 4).ToString("G", CultureInfo.InvariantCulture);
        if (value is double dbl) return Math.Round(dbl, 4).ToString("G", CultureInfo.InvariantCulture);
        if (value is float flt) return Math.Round((double)flt, 4).ToString("G", CultureInfo.InvariantCulture);
        if (value is bool b) return b ? "true" : "false";
        if (value is long or int or short or byte or sbyte or uint or ulong or ushort)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return "__null__";

        // Numeric FIRST — DateTime.TryParse is permissive and would happily parse "11.625"
        // as a date in year 625 (month=11, day=1). Always try numeric first; fall back to
        // date only if it's clearly not a number. Parse to double (not decimal) so the
        // canonical form matches the typed-double path on the gold side.
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedDbl))
            return Math.Round(parsedDbl, 4).ToString("G", CultureInfo.InvariantCulture);
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDec))
            return Math.Round((double)parsedDec, 4).ToString("G", CultureInfo.InvariantCulture);

        // Then date — bridge stringifies DateTime with culture-specific formatting like
        // "5/14/2026 12:00:00 AM". Strip the kind to Unspecified so the "O" round-trip
        // format does NOT append a timezone offset; SQL reader returns Kind=Unspecified for
        // DATE/DATETIME columns, and we need both sides to canonicalize identically.
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt))
            return DateTime.SpecifyKind(parsedDt, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsedDt))
            return DateTime.SpecifyKind(parsedDt, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture);

        return s.ToLowerInvariant();
    }
}
