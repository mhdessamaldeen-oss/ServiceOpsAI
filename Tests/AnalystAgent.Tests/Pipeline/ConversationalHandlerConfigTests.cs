namespace AnalystAgent.Tests.Pipeline;

using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 externalization of ConversationalHandler's detection patterns to
/// conversational-cues.json: (1) the SHIPPED config reproduces the in-code English fallback EXACTLY
/// (a missing file drives the fallback, the real file drives config, and they must agree on the
/// English corpus AND on non-conversational questions falling through); (2) an Arabic greeting,
/// present only in the config, fires — proving multilingual coverage is real.
/// </summary>
public class ConversationalHandlerConfigTests
{
    private static IConversationalHandler Handler(string path)
    {
        var text = new CopilotTextCatalog();
        var monitor = Mock.Of<IOptionsMonitor<CopilotTextCatalog>>(m => m.CurrentValue == text);
        return new ConversationalHandler(
            monitor,
            Options.Create(new AnalystOptions { ConversationalCuesPath = path }),
            NullLogger<ConversationalHandler>.Instance);
    }

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "AnalystAgent", "Configuration", file);
    }

    private static IConversationalHandler ConfigHandler() => Handler(RepoConfigPath("conversational-cues.json"));
    private static IConversationalHandler FallbackHandler() => Handler("Areas/AnalystAgent/Configuration/__no_such_conv_file__.json");

    [Theory]
    [InlineData("hi", ConversationalKind.Greeting)]
    [InlineData("hey there!", ConversationalKind.Greeting)]
    [InlineData("good morning", ConversationalKind.Greeting)]
    [InlineData("what can you do", ConversationalKind.Capabilities)]
    [InlineData("help", ConversationalKind.Capabilities)]
    [InlineData("who are you?", ConversationalKind.Capabilities)]
    [InlineData("give me some examples", ConversationalKind.Capabilities)]
    [InlineData("thanks", ConversationalKind.Thanks)]
    [InlineData("thank you", ConversationalKind.Thanks)]
    [InlineData("ty", ConversationalKind.Thanks)]
    [InlineData("bye", ConversationalKind.Farewell)]
    [InlineData("see you later", ConversationalKind.Farewell)]
    public void ShippedConfig_ReproducesEnglishFallback_Match(string q, ConversationalKind expected)
    {
        Assert.Equal(expected, FallbackHandler().TryHandle(q)?.Kind);
        Assert.Equal(expected, ConfigHandler().TryHandle(q)?.Kind);
    }

    [Theory]
    [InlineData("how many tickets do we have in total")]
    [InlineData("list all overdue bills for active customers")]
    [InlineData("show me tickets created last week by priority")]
    public void ShippedConfig_ReproducesEnglishFallback_NonConversationalFallsThrough(string q)
    {
        Assert.Null(FallbackHandler().TryHandle(q));
        Assert.Null(ConfigHandler().TryHandle(q));
    }

    [Fact]
    public void ArabicGreeting_Fires_FromConfigOnly()
    {
        Assert.Equal(ConversationalKind.Greeting, ConfigHandler().TryHandle("مرحبا")?.Kind);
        Assert.Null(FallbackHandler().TryHandle("مرحبا")); // English-only fallback can't see it
    }
}
