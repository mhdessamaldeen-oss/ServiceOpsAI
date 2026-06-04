namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using AnalystAgent.Tools;

/// <summary>
/// Bridges the new copilot's <see cref="IToolRegistry"/> to the host's
/// <c>CopilotToolDefinitions</c> EF table. Reads are cached for 5 minutes — the table is small
/// (~6 rows) and admins edit it via the existing UI which clears its own cache; we avoid hitting
/// the DB on every chat turn.
///
/// THIS IS THE ONLY FILE that imports the host's <c>CopilotToolDefinition</c> entity. The new
/// copilot's pipeline only sees the host-free <see cref="ToolDefinition"/> projection.
/// </summary>
internal sealed class HostToolRegistryBridge : IToolRegistry
{
    private const string CacheKey = "analyst-agent::tool-registry";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HostToolRegistryBridge> _logger;

    public HostToolRegistryBridge(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IMemoryCache cache,
        ILogger<HostToolRegistryBridge> logger)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            // Cheap optimistic check: if we have a cached non-empty list, the registry is live.
            // First call always returns true so we exercise the DB at least once per cache window
            // — admins enabling a tool shouldn't have to wait for cache eviction.
            if (_cache.TryGetValue<IReadOnlyList<ToolDefinition>>(CacheKey, out var cached))
                return cached is { Count: > 0 };
            return true;
        }
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue<IReadOnlyList<ToolDefinition>>(CacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await ctx.CopilotToolDefinitions
                .AsNoTracking()
                .Where(t => t.IsEnabled)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Title)
                .ToListAsync(cancellationToken);
            var list = rows.Select(Project).ToList();
            _cache.Set(CacheKey, (IReadOnlyList<ToolDefinition>)list, CacheDuration);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnalystAgent] Tool registry read failed; falling through.");
            return Array.Empty<ToolDefinition>();
        }
    }

    public async Task<ToolDefinition?> GetByKeyAsync(string toolKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolKey)) return null;
        var all = await GetEnabledAsync(cancellationToken);
        return all.FirstOrDefault(t => string.Equals(t.ToolKey, toolKey, StringComparison.OrdinalIgnoreCase));
    }

    private static ToolDefinition Project(CopilotToolDefinition t) => new(
        ToolKey: t.ToolKey,
        Title: t.Title,
        Description: t.Description,
        EndpointUrl: t.EndpointUrl,
        KeywordHints: t.KeywordHints,
        TestPrompt: t.TestPrompt);
}
