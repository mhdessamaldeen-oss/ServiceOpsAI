using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI.Copilot.Tools
{
    /// <summary>
    /// Central registry for Copilot tool definitions. Provides configured tools to the admin UI
    /// and active copilot surfaces.
    /// </summary>
    public class CopilotToolRegistry
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ILogger<CopilotToolRegistry> _logger;

        public CopilotToolRegistry(IDbContextFactory<ApplicationDbContext> contextFactory, ILogger<CopilotToolRegistry> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        /// <summary>Returns all enabled tool definitions for the router to include in its prompt.</summary>
        public async Task<List<CopilotToolDefinition>> GetEnabledToolsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.CopilotToolDefinitions
                .AsNoTracking()
                .Where(t => t.IsEnabled)
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Title)
                .ToListAsync();
        }

        /// <summary>Returns all tool definitions for the admin management UI.</summary>
        public async Task<List<CopilotToolDefinition>> GetAllToolsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.CopilotToolDefinitions
                .AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.Title)
                .ToListAsync();
        }

        public async Task<CopilotToolDefinition?> GetByKeyAsync(string toolKey)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.CopilotToolDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.ToolKey == toolKey);
        }

        public async Task<CopilotToolDefinition?> GetByIdAsync(int id)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.CopilotToolDefinitions.FindAsync(id);
        }

        public async Task SaveAsync(CopilotToolDefinition tool)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            if (tool.Id == 0)
            {
                tool.CreatedAt = DateTime.UtcNow;
                context.CopilotToolDefinitions.Add(tool);
                _logger.LogInformation("Registered new Copilot tool: {ToolKey} ({Title})", tool.ToolKey, tool.Title);
            }
            else
            {
                var existing = await context.CopilotToolDefinitions.FindAsync(tool.Id);
                if (existing == null) return;

                existing.ToolKey = tool.ToolKey;
                existing.Title = tool.Title;
                existing.Description = tool.Description;
                existing.ToolType = tool.ToolType;
                existing.CopilotMode = tool.CopilotMode;
                existing.IsEnabled = tool.IsEnabled;
                existing.EndpointUrl = tool.EndpointUrl;
                existing.KeywordHints = tool.KeywordHints;
                existing.QueryExtractionHint = tool.QueryExtractionHint;
                existing.ResponseFormatHint = tool.ResponseFormatHint;
                existing.TestPrompt = tool.TestPrompt;
                existing.SortOrder = tool.SortOrder;
                existing.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("Updated Copilot tool: {ToolKey}", tool.ToolKey);
            }

            await context.SaveChangesAsync();
        }

        public async Task ToggleAsync(int id, bool isEnabled)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var tool = await context.CopilotToolDefinitions.FindAsync(id);
            if (tool == null) return;

            tool.IsEnabled = isEnabled;
            tool.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();

            _logger.LogInformation("Copilot tool {ToolKey} is now {State}.", tool.ToolKey, isEnabled ? "ENABLED" : "DISABLED");
        }

        public async Task DeleteAsync(int id)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var tool = await context.CopilotToolDefinitions.FindAsync(id);
            if (tool == null) return;

            context.CopilotToolDefinitions.Remove(tool);
            await context.SaveChangesAsync();

            _logger.LogInformation("Deleted Copilot tool: {ToolKey}", tool.ToolKey);
        }

        /// <summary>
        /// Builds the dynamic tool description block for the LLM routing prompt.
        /// Enhanced with extraction hints to make the router "smarter".
        /// </summary>
        public async Task<string> BuildToolDescriptionsForPromptAsync()
        {
            var tools = await GetEnabledToolsAsync();
            if (!tools.Any()) return "[]";

            var promptModel = tools.Select(t => new
            {
                t.ToolKey,
                t.Title,
                t.Description,
                t.ToolType,
                t.CopilotMode,
                t.KeywordHints,
                t.QueryExtractionHint,
                t.ResponseFormatHint,
                t.TestPrompt,
                t.EndpointUrl
            });

            return JsonSerializer.Serialize(promptModel);
        }

        /// <summary>Gets keyword-to-tool mappings for deterministic routing.</summary>
        public async Task<Dictionary<string, string>> GetKeywordMappingsAsync()
        {
            var tools = await GetEnabledToolsAsync();
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in tools.Where(t => !string.IsNullOrWhiteSpace(t.KeywordHints)))
            {
                var keywords = tool.KeywordHints!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var keyword in keywords)
                {
                    mappings.TryAdd(keyword, tool.ToolKey);
                }
            }

            return mappings;
        }
    }
}
