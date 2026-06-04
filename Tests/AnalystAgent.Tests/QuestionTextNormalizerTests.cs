namespace AnalystAgent.Tests;

using AnalystAgent.Internal;
using Xunit;

/// <summary>
/// Pins the surface-form-collapsing contract for QuestionTextNormalizer. Behaviour is
/// intentionally schema-agnostic — no table or column names appear in inputs.
///
/// <para>2026-06-01 — the normalizer was deliberately narrowed to whitespace-only cleanup.
/// Parenthesis / comma / separator handling moved to the LLM <c>StructuralCueParser</c>, which
/// understands the long tail of user-chosen punctuation far better than regex surgery (see the
/// normalizer's own summary). These tests now pin PRESERVATION of punctuation — the structural
/// cues survive into the question text so the cue-parser can read them.</para>
/// </summary>
public class QuestionTextNormalizerTests
{
    [Theory]
    [InlineData("users(name, email)", "users(name, email)")]
    [InlineData("users (name, email)", "users (name, email)")]
    [InlineData("users( name , email )", "users( name , email )")]
    [InlineData("users(name,email)", "users(name,email)")]
    [InlineData("users(name;email;phone)", "users(name;email;phone)")]
    public void Preserves_parentheses_and_separators(string input, string expected)
    {
        // Punctuation is intentionally preserved — the StructuralCueParser (LLM) reads it.
        // Only internal whitespace runs collapse; these inputs are already single-spaced.
        Assert.Equal(expected, QuestionTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("list of user( name ,id ,email ) with their role and ticket counts",
                "list of user( name ,id ,email ) with their role and ticket counts")]
    public void Preserves_live_column_request_input(string input, string expected)
    {
        Assert.Equal(expected, QuestionTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("show  me   open    tickets", "show me open tickets")]
    [InlineData("show\tme\nopen\ttickets", "show me open tickets")]
    [InlineData("users(name, email)  and  their  role", "users(name, email) and their role")]
    public void Collapses_whitespace_runs(string input, string expected)
    {
        Assert.Equal(expected, QuestionTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("show, , open tickets", "show, , open tickets")]
    [InlineData("a; ; b", "a; ; b")]
    public void Preserves_comma_and_semicolon_punctuation(string input, string expected)
    {
        // Commas/semicolons survive (only whitespace runs between them collapse to single spaces).
        Assert.Equal(expected, QuestionTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  \t")]
    public void Returns_empty_for_blank_input(string? input)
    {
        Assert.Equal(string.Empty, QuestionTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Preserves_arabic_text()
    {
        // Arabic input should pass through whitespace / paren normalization unchanged
        // for the actual letterforms — only structural cleanup applies.
        const string ar = "اعرض التذاكر المفتوحة";
        Assert.Equal(ar, QuestionTextNormalizer.Normalize(ar));
    }

    [Fact]
    public void Preserves_typos_and_informal_spelling()
    {
        // Typos like "tikect" and "ans there" stay — the embedder is cross-lingual and
        // tolerates misspellings; correcting them here would risk silently changing intent.
        const string input = "list users and there tikect counts";
        Assert.Equal(input, QuestionTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Idempotent()
    {
        const string input = "users(name, email)  and  their  role";
        var once = QuestionTextNormalizer.Normalize(input);
        var twice = QuestionTextNormalizer.Normalize(once);
        Assert.Equal(once, twice);
    }
}

/// <summary>
/// Pins the Arabic / English split for QuestionLanguageDetector. The detector underpins every
/// per-locale prompt and reply hint, so a misclassification here mis-routes users.
/// </summary>
public class QuestionLanguageDetectorTests
{
    [Theory]
    [InlineData("how many open tickets", "en")]
    [InlineData("Hello world", "en")]
    [InlineData("123 456 789", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void Returns_english_for_non_arabic(string? input, string expected)
    {
        Assert.Equal(expected, QuestionLanguageDetector.Detect(input));
    }

    [Theory]
    [InlineData("كم عدد التذاكر المفتوحة", "ar")]
    [InlineData("اعرض المستخدمين", "ar")]
    [InlineData("ما هي الفئات المتاحة", "ar")]
    public void Returns_arabic_for_arabic(string input, string expected)
    {
        Assert.Equal(expected, QuestionLanguageDetector.Detect(input));
    }

    [Fact]
    public void Mixed_majority_arabic_returns_arabic()
    {
        // Arabic question that mentions an English table name should still be classified Arabic.
        Assert.Equal("ar", QuestionLanguageDetector.Detect("اعرض جدول AspNetUsers"));
    }

    [Fact]
    public void Mixed_majority_english_returns_english()
    {
        // English question with a single Arabic word should remain English.
        Assert.Equal("en", QuestionLanguageDetector.Detect("show me the open مرحبا tickets and users today"));
    }
}

