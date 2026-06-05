namespace AnalystAgent.Tests.Schema;

using AnalystAgent.Configuration;
using AnalystAgent.Schema;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

/// <summary>Covers IAnalystSchemaAccessPolicy.IsTableQueryable — the gate that stops the grounding/value layer
/// from probing the copilot's OWN operational tables (the self-poisoning bug where the question matched its own
/// logged chat-session Title). IsTableQueryable must be STRICTER than IsTableAllowed: it also excludes the
/// RetrieverHidden set, while still allowing legitimate business/identity tables.</summary>
public class AccessPolicyTests
{
    private static IAnalystSchemaAccessPolicy Policy(AnalystOptions opts)
    {
        var semantic = new Mock<AnalystAgent.Semantic.ISemanticLayer>(MockBehavior.Loose);
        return new AnalystSchemaAccessPolicy(Options.Create(opts), semantic.Object);
    }

    [Fact]
    public void IsTableQueryable_ExcludesHiddenAndBlocked_AllowsBusinessTables()
    {
        var opts = new AnalystOptions
        {
            RetrieverHiddenTables = new() { "CopilotChatSessions", "SystemSettings" },
            RetrieverHiddenTablePatterns = new() { "Copilot*", "__*", "*ApiKey*" },
            BlockedTablePatterns = new() { "*Secret*" },
        };
        var p = Policy(opts);

        // The copilot's own operational tables — the bug source — are NOT queryable.
        Assert.False(p.IsTableQueryable("CopilotChatSessions"));   // exact hidden + Copilot* pattern
        Assert.False(p.IsTableQueryable("CopilotTraceHistories")); // Copilot* pattern
        Assert.False(p.IsTableQueryable("__EFMigrationsHistory")); // __* pattern
        Assert.False(p.IsTableQueryable("GeminiApiKeys"));         // *ApiKey* pattern
        Assert.False(p.IsTableQueryable("UserSecrets"));           // hard BlockedTablePatterns still apply

        // Legitimate business + identity tables ARE queryable (AspNet* is no longer hidden).
        Assert.True(p.IsTableQueryable("AspNetUsers"));
        Assert.True(p.IsTableQueryable("AspNetRoles"));
        Assert.True(p.IsTableQueryable("Bills"));
        Assert.True(p.IsTableQueryable("Assets"));
    }

    [Fact]
    public void IsTableQueryable_IsStricterThanIsTableAllowed()
    {
        // A retriever-hidden table is still "allowed" (the hard security gate) but NOT "queryable" (the
        // grounding gate). This is the exact gap that let the value-linker probe CopilotChatSessions.
        var opts = new AnalystOptions { RetrieverHiddenTables = new() { "CopilotChatSessions" } };
        var p = Policy(opts);
        Assert.True(p.IsTableAllowed("CopilotChatSessions"));    // not hard-blocked
        Assert.False(p.IsTableQueryable("CopilotChatSessions")); // but hidden → never a data source
    }
}
