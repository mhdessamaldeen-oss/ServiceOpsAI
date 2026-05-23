namespace SuperAdminCopilot.Internal;

using System.Text.RegularExpressions;

/// <summary>
/// Schema-agnostic input cleanup applied to a question before it is handed to embedding,
/// classifier, or LLM stages. The goal is to make the same intent expressed in several
/// surface forms collapse to one canonical string the embedder + LLM can both handle.
///
/// <para><b>Why this exists.</b> Live failure: the input
/// <c>"list of user( name ,id ,email ) ans there role and there tikect counts"</c>
/// caused the Ollama embedder to return NaN, even though the same question without
/// parentheses worked fine. More broadly, parenthesised column lists like
/// <c>"users(name, email)"</c> are a common user shorthand that the LLM extractor was
/// never trained to parse — it would hallucinate or fall through to an empty result.</para>
///
/// <para><b>What it does NOT do.</b> No domain knowledge, no schema lookups, no per-table
/// rules. Adding new tables to the database does not require touching this file. Spell
/// correction is also out of scope — typos like "tikect" / "ans there" stay; the embedder
/// is cross-lingual and tolerates them.</para>
/// </summary>
internal static class QuestionTextNormalizer
{
    // word(a, b, c)  →  word a b c
    // Captures the word that immediately precedes the open paren, then drops the parens
    // and replaces internal commas with spaces. Tolerates extra whitespace anywhere.
    private static readonly Regex ParenColumnList = new(
        @"(\w+)\s*\(\s*([^()]{1,200})\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Runs of whitespace (incl. tabs / newlines) collapsed to a single space. Applied last.
    private static readonly Regex WhitespaceRun = new(
        @"\s+",
        RegexOptions.Compiled);

    // Runs of commas / semicolons with optional whitespace between, e.g. ", ,", ",,," → " "
    private static readonly Regex PunctRun = new(
        @"[,;]+(\s*[,;]+)*",
        RegexOptions.Compiled);

    /// <summary>Return the canonical form of <paramref name="question"/> suitable for
    /// embedding and LLM input. Always safe to call; returns empty for null/whitespace input.
    /// The original question should be retained separately for trace and UI display.</summary>
    public static string Normalize(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return string.Empty;

        var s = question;

        // Expand parenthesised column lists into space-separated tokens. This runs first so
        // the inner commas get visited by the comma-collapse step below.
        s = ParenColumnList.Replace(s, m =>
        {
            var head = m.Groups[1].Value;
            var inner = m.Groups[2].Value.Replace(',', ' ').Replace(';', ' ');
            return head + " " + inner;
        });

        // Collapse comma / semicolon runs to a single space — the LLM and embedder both treat
        // ",," and ", , ," as noise. Standalone commas between real words are also reduced to
        // whitespace here; the semantic separation survives via the surrounding tokens.
        s = PunctRun.Replace(s, " ");

        // Final whitespace collapse + trim.
        s = WhitespaceRun.Replace(s, " ").Trim();

        return s;
    }
}
