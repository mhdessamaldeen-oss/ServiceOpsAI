namespace SuperAdminCopilot.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Integration tests for the WriteIntentGuard safety gate. The guard refuses write-shaped
/// questions (delete / drop / update / insert / alter / truncate, multilingual + fuzzy)
/// before any LLM call. These tests pin the safety guarantee — they would catch a regex
/// regression that quietly let a "drop the users table" question reach the planner.
/// </summary>
public class WriteIntentGuardTests
{
    private static WriteIntentGuard BuildGuard(params (string Locale, string[] Patterns)[] languages)
    {
        var opts = new WriteIntentOptions
        {
            Languages = languages.Select(l => new WriteIntentLanguage
            {
                Locale = l.Locale,
                Verbs = l.Patterns.Select(p => new WriteIntentVerb { Pattern = p, Verb = p }).ToList()
            }).ToList()
        };
        var monitor = Mock.Of<IOptionsMonitor<WriteIntentOptions>>(m => m.CurrentValue == opts);
        return new WriteIntentGuard(monitor, NullLogger<WriteIntentGuard>.Instance);
    }

    [Theory]
    [InlineData("delete all tickets")]
    [InlineData("DELETE all the tickets please")]
    [InlineData("drop the users table")]
    [InlineData("update all ticket statuses to closed")]
    [InlineData("truncate the tickets table")]
    [InlineData("insert a new ticket into the table")]
    public void Refuses_english_write_verbs(string question)
    {
        var guard = BuildGuard(("en", new[] {
            @"\bdelete\b", @"\bdrop\b", @"\bupdate\b", @"\btruncate\b", @"\binsert\b"
        }));
        var result = guard.Check(question);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.Reason));
    }

    [Fact]
    public void Refuses_arabic_delete()
    {
        var guard = BuildGuard(("ar", new[] { @"احذف" }));
        var result = guard.Check("احذف جميع التذاكر");
        Assert.NotNull(result);
        Assert.Equal("ar", result!.Language);
    }

    [Theory]
    [InlineData("show me all tickets")]
    [InlineData("how many tickets are open")]
    [InlineData("list users created this month")]
    [InlineData("")]
    [InlineData("   ")]
    public void Passes_read_only_questions(string? question)
    {
        var guard = BuildGuard(("en", new[] {
            @"\bdelete\b", @"\bdrop\b", @"\bupdate\b"
        }));
        var result = guard.Check(question ?? "");
        Assert.Null(result);
    }

    [Fact]
    public void Empty_options_passes_everything()
    {
        // Empty WriteIntentOptions = no patterns to match → guard returns null for all input.
        // This is the safety-default behavior when the JSON config file is missing entirely.
        var guard = BuildGuard();
        Assert.Null(guard.Check("delete all tickets"));
    }
}
