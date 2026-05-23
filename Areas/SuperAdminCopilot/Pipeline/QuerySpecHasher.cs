namespace SuperAdminCopilot.Pipeline;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperAdminCopilot.Models;

/// <summary>
/// Stable structural hash of a <see cref="QuerySpec"/> — used by the orchestrator's retry
/// loop to detect convergence. If two consecutive SpecRefine attempts produce specs with
/// the same structural hash, the LLM has converged: further retries will produce the same
/// SQL and will fail/succeed the same way. The orchestrator stops retrying and accepts the
/// current outcome, saving an LLM round-trip + the retry-budget cost.
/// </summary>
/// <remarks>
/// <para>Why a custom hash instead of <c>JsonSerializer.Serialize(spec).GetHashCode()</c>:
/// JSON serialization preserves member declaration order, which makes the hash collision-free
/// across struct variants. We normalize collections (sort by canonical key) so reorderings
/// (e.g. <c>Select=["A","B"]</c> vs <c>["B","A"]</c>) hash the same — order is irrelevant
/// to the resulting SQL semantics.</para>
/// <para>SHA256 truncated to 16 hex chars (64 bits): collision probability for a per-question
/// retry sequence (≤6 attempts) is astronomically low; the short form keeps trace logs readable.</para>
/// </remarks>
public static class QuerySpecHasher
{
    public static string Hash(QuerySpec spec)
    {
        if (spec is null) return string.Empty;

        // Canonical projection — sort every collection by a stable string key so semantic
        // equivalents hash identically. Anonymous types serialize their members in declaration
        // order, which combined with sorted collections gives a deterministic canonical form.
        var canonical = new
        {
            spec.Intent,
            spec.Root,
            Select = spec.Select?.OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
            GroupBy = spec.GroupBy?.OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
            Aggregations = spec.Aggregations?
                .Select(a => new { a.Function, a.Column, a.Alias, a.Distinct })
                .OrderBy(a => a.Alias, StringComparer.Ordinal)
                .ThenBy(a => a.Column, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            Filters = spec.Filters?
                .Select(f => new { f.Column, f.Op, FilterValue = f.Value?.ToString() })
                .OrderBy(f => f.Column, StringComparer.Ordinal)
                .ThenBy(f => f.Op, StringComparer.Ordinal)
                .ThenBy(f => f.FilterValue, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            Having = spec.Having?
                .Select(h => new { h.Function, h.Column, h.Op, HavingValue = h.Value?.ToString() })
                .OrderBy(h => h.Column, StringComparer.Ordinal)
                .ThenBy(h => h.Function, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            OrderBy = spec.OrderBy?
                .Select(o => new { o.Column, o.Direction })
                .OrderBy(o => o.Column, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            Computed = spec.Computed?
                .Select(c => new { c.Alias, c.Expression })
                .OrderBy(c => c.Alias, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            Joins = spec.Joins?
                .Select(j => new { j.Table, j.Kind })
                .OrderBy(j => j.Table, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            PeriodComparisons = spec.PeriodComparisons?
                .Select(p => new
                {
                    p.Label,
                    Filters = p.Filters?
                        .Select(f => new { f.Column, f.Op, FilterValue = f.Value?.ToString() })
                        .OrderBy(f => f.Column, StringComparer.Ordinal)
                        .ThenBy(f => f.Op, StringComparer.Ordinal)
                        .ThenBy(f => f.FilterValue, StringComparer.Ordinal)
                        .ToArray() ?? Array.Empty<object>()
                })
                .OrderBy(p => p.Label, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<object>(),
            spec.Limit,
            spec.Distinct,
        };

        var json = JsonSerializer.Serialize(canonical);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        // 16 hex chars = 64 bits — plenty for ≤6-attempt retry sequences.
        return Convert.ToHexString(hash, 0, 8);
    }
}
