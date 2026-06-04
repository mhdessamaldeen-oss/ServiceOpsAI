namespace AnalystAgent.Tools;

/// <summary>
/// Host-free abstraction over the tool registry. The in-host build resolves this to
/// <c>HostBridge.HostToolRegistryBridge</c>, which reads the host's DB-backed
/// <c>CopilotToolDefinitions</c>. The copilot deliberately doesn't take
/// a dependency on the host's <c>CopilotToolDefinition</c> shape — we only need the fields
/// the keyword router and HTTP dispatcher actually consume.
/// </summary>
public interface IToolRegistry
{
    /// <summary>True when the registry has at least one enabled tool. False short-circuits the
    /// ToolHandler stage so it's a free fall-through when admins haven't configured any.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns all enabled tool definitions, ordered by SortOrder then Title.</summary>
    Task<IReadOnlyList<ToolDefinition>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Lookup a single tool by its <see cref="ToolDefinition.ToolKey"/> (case-insensitive).</summary>
    Task<ToolDefinition?> GetByKeyAsync(string toolKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Subset of the host's <c>CopilotToolDefinition</c> the new copilot needs. KeywordHints stays a
/// raw comma-separated string (not a parsed list) so the bridge does no extra translation work —
/// the ToolHandler tokenizes when matching.
/// </summary>
public sealed record ToolDefinition(
    string ToolKey,
    string Title,
    string? Description,
    string? EndpointUrl,
    string? KeywordHints,
    string? TestPrompt);
