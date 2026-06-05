namespace AnalystAgent.Tests.Pipeline;

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.Stages;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 externalization of ConversationalHandler's detection patterns to
/// conversational-cues.json: (1) the SHIPPED config reproduces the in-code English fallback EXACTLY
/// (a missing file drives the fallback, the real file drives config, and they must agree on the
/// English corpus AND on non-conversational questions falling through); (2) an Arabic greeting,
/// present only in the config, fires — proving multilingual coverage is real.
///
/// Plus the SmallTalk tier (2026-06-05): pure greetings/thanks stay 0-LLM; small-talk matches a new cue
/// list and (when SmallTalkUseLlm is on) gets exactly ONE warm LLM reply, failing open to canned.
/// </summary>
public class ConversationalHandlerConfigTests
{
    private static IConversationalHandler Handler(
        string path, AnalystOptions? opts = null, IAiProviderFactory? factory = null)
    {
        var text = new CopilotTextCatalog();
        var textMon = Mock.Of<IOptionsMonitor<CopilotTextCatalog>>(m => m.CurrentValue == text);
        var o = opts ?? new AnalystOptions();
        o.ConversationalCuesPath = path;
        var optMon = Mock.Of<IOptionsMonitor<AnalystOptions>>(m => m.CurrentValue == o);
        return new ConversationalHandler(
            textMon,
            optMon,
            factory ?? Mock.Of<IAiProviderFactory>(),
            NullLogger<ConversationalHandler>.Instance);
    }

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "AnalystAgent", "Configuration", file);
    }

    private static IConversationalHandler ConfigHandler(AnalystOptions? opts = null, IAiProviderFactory? factory = null)
        => Handler(RepoConfigPath("conversational-cues.json"), opts, factory);
    private static IConversationalHandler FallbackHandler()
        => Handler("Areas/AnalystAgent/Configuration/__no_such_conv_file__.json");

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

    // ── SmallTalk tier ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("how are you")]
    [InlineData("how are you doing?")]
    [InlineData("what's up")]
    [InlineData("tell me a joke")]
    [InlineData("are you a robot?")]
    [InlineData("nice to meet you")]
    public void SmallTalk_Matches_InBothConfigAndFallback(string q)
    {
        Assert.Equal(ConversationalKind.SmallTalk, FallbackHandler().TryHandle(q)?.Kind);
        Assert.Equal(ConversationalKind.SmallTalk, ConfigHandler().TryHandle(q)?.Kind);
    }

    [Fact]
    public void ArabicSmallTalk_Fires_FromConfigOnly()
    {
        Assert.Equal(ConversationalKind.SmallTalk, ConfigHandler().TryHandle("كيف حالك")?.Kind);
    }

    [Fact]
    public void Thanks_StillWins_OverSmallTalk()
    {
        // "thanks" must resolve as Thanks (checked before SmallTalk), not get swallowed by small-talk.
        Assert.Equal(ConversationalKind.Thanks, ConfigHandler().TryHandle("thanks")?.Kind);
    }

    [Fact]
    public async Task SmallTalk_WithLlmOff_ReturnsCanned_NeverCallsModel()
    {
        var factory = new Mock<IAiProviderFactory>(MockBehavior.Strict); // any call would throw
        var h = ConfigHandler(new AnalystOptions { SmallTalkUseLlm = false }, factory.Object);

        var reply = await h.TryHandleAsync("how are you");

        Assert.NotNull(reply);
        Assert.Equal(ConversationalKind.SmallTalk, reply!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(reply.Reply));
        factory.Verify(f => f.GetProviderForWorkload(It.IsAny<AiWorkloadType>()), Times.Never);
    }

    [Fact]
    public async Task SmallTalk_WithLlmOn_ReturnsWarmModelReply()
    {
        var provider = new Mock<IAiProvider>();
        provider.SetupGet(p => p.ModelName).Returns("test-model");
        provider.Setup(p => p.GenerateAsync(It.IsAny<string>()))
                .ReturnsAsync(new AiProviderResult { Success = true, ResponseText = "  \"I'm doing wonderfully — thanks for asking!\"  " });
        var factory = new Mock<IAiProviderFactory>();
        factory.Setup(f => f.GetProviderForWorkload(AiWorkloadType.Classifier)).Returns(provider.Object);

        var reply = await ConfigHandler(new AnalystOptions { SmallTalkUseLlm = true }, factory.Object)
            .TryHandleAsync("how are you");

        Assert.Equal(ConversationalKind.SmallTalk, reply!.Kind);
        Assert.Equal("I'm doing wonderfully — thanks for asking!", reply.Reply); // sanitized: quotes + spaces stripped
    }

    [Fact]
    public async Task SmallTalk_WhenModelThrows_FailsOpenToCanned()
    {
        var factory = new Mock<IAiProviderFactory>();
        factory.Setup(f => f.GetProviderForWorkload(It.IsAny<AiWorkloadType>())).Throws(new InvalidOperationException("model down"));

        var reply = await ConfigHandler(new AnalystOptions { SmallTalkUseLlm = true }, factory.Object)
            .TryHandleAsync("tell me a joke");

        Assert.NotNull(reply);
        Assert.Equal(ConversationalKind.SmallTalk, reply!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(reply.Reply)); // canned fallback, not an error
    }

    [Fact]
    public async Task Greeting_NeverCallsModel_EvenWithLlmOn()
    {
        var factory = new Mock<IAiProviderFactory>(MockBehavior.Strict); // a call would throw the test
        var h = ConfigHandler(new AnalystOptions { SmallTalkUseLlm = true }, factory.Object);

        var reply = await h.TryHandleAsync("good morning");

        Assert.Equal(ConversationalKind.Greeting, reply!.Kind);
        factory.Verify(f => f.GetProviderForWorkload(It.IsAny<AiWorkloadType>()), Times.Never);
    }

    [Fact]
    public async Task NonConversational_ReturnsNull_FromAsyncToo()
    {
        Assert.Null(await ConfigHandler().TryHandleAsync("how many tickets are open"));
    }
}
