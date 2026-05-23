namespace SuperAdminCopilot.Internal;

/// <summary>
/// Cheap O(n) language detector used to pick the right per-locale prompt / regex / reply for a
/// question. Currently distinguishes only Arabic vs. English (the two languages the copilot
/// targets); extending to more locales is a single switch on Unicode block ranges.
///
/// <para>Deliberately NOT using a heavy NLP / ML library — the host runs locally on commodity
/// hardware and the call site (orchestrator entry, IntentClassifier, Explainer) is hot path.
/// Mis-detection is recoverable: every stage that consumes the result has an English fallback.</para>
///
/// <para>Heuristic: count letters whose codepoint is inside the Arabic Unicode block
/// (U+0600..U+06FF — covers Modern Standard Arabic, the dialects, and most punctuation marks).
/// Return "ar" when ≥30% of the letters in the question are Arabic; otherwise "en". The
/// threshold tolerates code-switching (e.g. an Arabic question that mentions an English
/// table or column name).</para>
/// </summary>
internal static class QuestionLanguageDetector
{
    public const string Arabic = "ar";
    public const string English = "en";

    private const double ArabicShareThreshold = 0.30;

    /// <summary>Return the ISO-639-1 language code best matching <paramref name="text"/>.
    /// Empty / null input returns <see cref="English"/> (the safe default — every prompt
    /// in the catalog has an English version).</summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return English;

        int letters = 0;
        int arabicLetters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c >= 0x0600 && c <= 0x06FF) arabicLetters++;
        }

        if (letters == 0) return English;
        return ((double)arabicLetters / letters) >= ArabicShareThreshold ? Arabic : English;
    }
}
