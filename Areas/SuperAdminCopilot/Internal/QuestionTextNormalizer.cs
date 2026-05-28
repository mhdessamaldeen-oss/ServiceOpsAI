namespace SuperAdminCopilot.Internal;

using System.Text.RegularExpressions;

/// <summary>
/// Pure text hygiene: trim + collapse internal whitespace runs. Does NOT touch separators,
/// brackets, parens, dashes, commas, or any decoration — the StructuralCueParser handles those
/// via LLM, the right tool for the long tail of user-chosen punctuation.
/// </summary>
internal static class QuestionTextNormalizer
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;
        return WhitespaceRun.Replace(question, " ").Trim();
    }
}
