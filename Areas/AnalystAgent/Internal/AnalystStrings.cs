namespace AnalystAgent.Internal;

/// <summary>
/// Small string utilities used across the copilot area. Pulled here so the half-dozen duplicate
/// private <c>Truncate</c> implementations across stages / handlers / retrievers stop drifting.
/// </summary>
internal static class AnalystStrings
{
    /// <summary>Truncate <paramref name="s"/> to at most <paramref name="max"/> characters,
    /// appending an ellipsis when truncated. Null / empty input returns empty.</summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
