namespace SuperAdminCopilot.Pipeline.SpecRepair;

using System.Collections.Generic;

/// <summary>
/// Null-safe iteration helpers for QuerySpec list fields. SpecExtractor or an upstream phase
/// can append a null entry to spec.Filters / spec.OrderBy / spec.Joins / etc. — phases that
/// dereference list elements without null-checking will NPE. Use <see cref="NotNull{T}"/> to
/// filter at the loop boundary.
///
/// Pattern: <c>foreach (var f in spec.Filters.NotNull()) { ... f.Column ... }</c>
/// </summary>
internal static class SpecListExtensions
{
    /// <summary>
    /// Iterate a list filtering out null elements and a null source. Safe for hot paths —
    /// uses a manual loop, no LINQ overhead.
    /// </summary>
    public static IEnumerable<T> NotNull<T>(this IEnumerable<T?>? source) where T : class
    {
        if (source is null) yield break;
        foreach (var item in source)
            if (item is not null) yield return item;
    }
}
