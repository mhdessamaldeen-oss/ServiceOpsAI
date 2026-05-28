namespace SuperAdminCopilot.Tests.Configuration;

using Microsoft.Extensions.Logging.Abstractions;
using SuperAdminCopilot.Configuration;
using Xunit;

/// <summary>
/// Day-1 test: validate that the actual <c>linguistic-cues.json</c> file ships parsable and
/// produces the expected number of compiled cues per locale. This is the QA gate that
/// prevents a broken JSON edit from silently disabling the cue pipeline.
/// </summary>
public sealed class LinguisticCuesProviderTests
{
    [Fact]
    public void Loads_RealConfigFile_WithoutErrors()
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        Assert.NotNull(provider.Compiled);
        Assert.NotEmpty(provider.Compiled.Locales);
    }

    [Fact]
    public void English_Locale_Defines_All_Sections()
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        Assert.True(provider.Compiled.Locales.TryGetValue("en", out var en));
        Assert.NotNull(en);
        Assert.NotEmpty(en!.Temporal);
        Assert.NotNull(en.AbsenceRegex);
        Assert.NotNull(en.AllTimeRegex);
        Assert.NotNull(en.DistinctRegex);
        Assert.NotNull(en.NegationRegex);
        Assert.NotNull(en.RecencyDescRegex);
        Assert.NotNull(en.RecencyAscRegex);
        Assert.NotNull(en.SuperlativeMaxRegex);
        Assert.NotEmpty(en.RangeBetween);
        Assert.NotEmpty(en.RangeGt);
    }

    [Fact]
    public void Arabic_Locale_Loads_With_Temporal_Plus_Absence()
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        Assert.True(provider.Compiled.Locales.TryGetValue("ar", out var ar));
        Assert.NotNull(ar);
        Assert.NotEmpty(ar!.Temporal);
        Assert.NotNull(ar.AbsenceRegex);
    }

    [Theory]
    [InlineData("today", "@today", "@tomorrow")]
    [InlineData("yesterday", "@yesterday", "@today")]
    [InlineData("this week", "@week_start", "@weeks:1")]
    [InlineData("last month", "@last_month_start", "@month_start")]
    [InlineData("Q1 of this year", "@q1_start", "@q1_end")]
    public void English_Temporal_Cues_Match_Expected_Tokens(string question, string expectedStart, string? expectedEnd)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        var matched = en.Temporal.FirstOrDefault(c => c.Pattern.IsMatch(question));
        Assert.NotNull(matched);
        Assert.Equal(expectedStart, matched!.Start);
        Assert.Equal(expectedEnd, matched.End);
    }

    [Theory]
    [InlineData("show me bills with no email", true)]
    [InlineData("tickets without an assignee", true)]
    [InlineData("customers with email", false)]
    public void English_Absence_Cue_Detects_Negative_Phrasing(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.NotNull(en.AbsenceRegex);
        Assert.Equal(shouldMatch, en.AbsenceRegex!.IsMatch(question));
    }

    [Theory]
    [InlineData("largest single bill ever", true)]
    [InlineData("of all time", true)]
    [InlineData("biggest bill this month", false)]
    public void English_AllTime_Cue_Detects_Ever_Phrasing(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.NotNull(en.AllTimeRegex);
        Assert.Equal(shouldMatch, en.AllTimeRegex!.IsMatch(question));
    }

    [Theory]
    [InlineData("how many distinct customers opened a ticket", true)]
    [InlineData("number of unique customers", true)]
    [InlineData("how many customers opened a ticket", false)]
    public void English_Distinct_Cue_Detects_Distinctness(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.NotNull(en.DistinctRegex);
        Assert.Equal(shouldMatch, en.DistinctRegex!.IsMatch(question));
    }

    [Theory]
    [InlineData("tickets not in Damascus", true)]
    [InlineData("bills excluding paid ones", true)]
    [InlineData("tickets in Damascus", false)]
    public void English_Negation_Cue_Detects_Negation(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.NotNull(en.NegationRegex);
        Assert.Equal(shouldMatch, en.NegationRegex!.IsMatch(question));
    }

    [Theory]
    [InlineData("most recent bill", true,  false)]
    [InlineData("newest 10 customers", true, false)]
    [InlineData("oldest tickets", false, true)]
    [InlineData("earliest outage", false, true)]
    public void English_Recency_Cues_Disambiguate_Asc_Desc(string question, bool expectDesc, bool expectAsc)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.Equal(expectDesc, en.RecencyDescRegex?.IsMatch(question) ?? false);
        Assert.Equal(expectAsc, en.RecencyAscRegex?.IsMatch(question) ?? false);
    }

    [Theory]
    [InlineData("bills over 50000", true)]
    [InlineData("at least 100 tickets", false)]      // gte, not gt
    [InlineData("more than 5 customers", true)]
    public void English_Range_Gt_Matches_Strictly_Greater(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        Assert.Equal(shouldMatch, en.RangeGt.Any(r => r.IsMatch(question)));
    }

    [Theory]
    [InlineData("bills between 5000 and 10000")]
    [InlineData("from 1000 to 50000")]
    public void English_Range_Between_Captures_Two_Bounds(string question)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var en = provider.Compiled.Locales["en"];
        var match = en.RangeBetween.Select(r => r.Match(question)).FirstOrDefault(m => m.Success);
        Assert.NotNull(match);
        Assert.True(match!.Success);
        Assert.Equal(3, match.Groups.Count);   // entire match + two capture groups
    }

    [Fact]
    public void Compile_From_Empty_Dto_Produces_Empty_Compiled()
    {
        var dto = new LinguisticCues { Version = 1, Locales = new() };
        var compiled = LinguisticCuesProvider.Compile(dto);
        Assert.Equal(1, compiled.Version);
        Assert.Empty(compiled.Locales);
    }

    [Fact]
    public void Compile_With_Malformed_Regex_Skips_Bad_Entry_But_Keeps_Rest()
    {
        var dto = new LinguisticCues
        {
            Version = 1,
            Locales = new()
            {
                ["en"] = new LocaleCues
                {
                    // First entry has unbalanced parens — should be skipped.
                    Temporal = new()
                    {
                        new TemporalCue { Pattern = "(unbalanced", Start = "@today", Label = "bad" },
                        new TemporalCue { Pattern = "\\btoday\\b", Start = "@today", End = "@tomorrow", Label = "Today" },
                    },
                },
            },
        };
        var compiled = LinguisticCuesProvider.Compile(dto);
        Assert.True(compiled.Locales.TryGetValue("en", out var en));
        Assert.Single(en!.Temporal);
        Assert.Equal("Today", en.Temporal[0].Label);
    }

    [Theory]
    [InlineData("اليوم", "@today")]
    [InlineData("أمس", "@yesterday")]
    [InlineData("هذا الشهر", "@month_start")]
    public void Arabic_Temporal_Cues_Match(string question, string expectedStart)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var ar = provider.Compiled.Locales["ar"];
        var match = ar.Temporal.FirstOrDefault(c => c.Pattern.IsMatch(question));
        Assert.NotNull(match);
        Assert.Equal(expectedStart, match!.Start);
    }

    [Theory]
    [InlineData("بدون فاتورة", true)]
    [InlineData("مع فاتورة", false)]
    public void Arabic_Absence_Cue_Detects_Without(string question, bool shouldMatch)
    {
        var provider = new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance);
        var ar = provider.Compiled.Locales["ar"];
        Assert.NotNull(ar.AbsenceRegex);
        Assert.Equal(shouldMatch, ar.AbsenceRegex!.IsMatch(question));
    }
}
