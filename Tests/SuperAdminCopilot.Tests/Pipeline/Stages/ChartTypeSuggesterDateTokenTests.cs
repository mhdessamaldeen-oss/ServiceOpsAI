namespace SuperAdminCopilot.Tests.Pipeline.Stages;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 externalization of ChartTypeSuggester's date-column tokens to
/// linguistic-cues.json: (1) the shipped config reproduces the in-code English fallback EXACTLY
/// (behavior-neutral — chart hints are advisory and must not shift), and (2) an Arabic token,
/// present only in the config, fires — proving multilingual coverage is real.
/// </summary>
public class ChartTypeSuggesterDateTokenTests
{
    private static ChartTypeSuggester ConfigSuggester() =>
        new(new LinguisticCuesProvider(NullLogger<LinguisticCuesProvider>.Instance));

    private static ChartTypeSuggester FallbackSuggester() =>
        new(Mock.Of<ILinguisticCuesProvider>(p => p.Compiled == CompiledLinguisticCues.Empty));

    [Theory]
    [InlineData("CreatedAt", true)]
    [InlineData("UpdatedAt", true)]
    [InlineData("OrderDate", true)]
    [InlineData("IssueMonth", true)]
    [InlineData("FiscalYear", true)]
    [InlineData("WeekNumber", true)]
    [InlineData("CustomerName", false)]
    [InlineData("TotalAmount", false)]
    [InlineData("Status", false)]
    [InlineData("Priority", false)]
    public void ShippedConfig_ReproducesEnglishFallback(string column, bool expected)
    {
        Assert.Equal(expected, FallbackSuggester().IsDateLike(column));
        Assert.Equal(expected, ConfigSuggester().IsDateLike(column));
    }

    [Fact]
    public void ArabicDateToken_Fires_FromConfigOnly()
    {
        // 'تاريخ' (date) lives only in linguistic-cues.json, never in the in-code fallback.
        Assert.True(ConfigSuggester().IsDateLike("تاريخ_الإنشاء"));
        Assert.False(FallbackSuggester().IsDateLike("تاريخ_الإنشاء"));
    }
}
