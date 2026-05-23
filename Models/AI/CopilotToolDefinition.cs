using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Defines a Copilot tool that can be dynamically configured by administrators.
    /// External tools call HTTP APIs; Internal tools map to built-in C# handlers.
    /// </summary>
    public class CopilotToolDefinition
    {
        public int Id { get; set; }

        /// <summary>
        /// Machine-readable identifier, e.g., "weather_today", "currency_convert".
        /// Used by the router and dispatcher for matching.
        /// </summary>
        [Required, MaxLength(100)]
        public string ToolKey { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable name shown in the admin UI, e.g., "Weather Forecast".
        /// </summary>
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Description of what this tool does. Injected into the LLM routing prompt
        /// so the AI can decide when to use it.
        /// </summary>
        [Required, MaxLength(500)]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// "External" = calls an HTTP API endpoint.
        /// "Internal" = maps to a built-in C# handler (cannot be created from config).
        /// </summary>
        [Required, MaxLength(20)]
        public string ToolType { get; set; } = "External";

        /// <summary>
        /// The CopilotChatMode this tool maps to.
        /// External tools always map to ExternalUtility.
        /// </summary>
        [Required, MaxLength(50)]
        public string CopilotMode { get; set; } = "ExternalUtility";

        /// <summary>
        /// Whether the admin has enabled this tool for Copilot use.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// For external tools: the API endpoint URL.
        /// Can contain {query} placeholder for dynamic substitution.
        /// </summary>
        [MaxLength(1000)]
        public string? EndpointUrl { get; set; }

        /// <summary>
        /// Comma-separated keywords that help the deterministic router
        /// match user queries to this tool (e.g., "weather,forecast,temperature").
        /// </summary>
        [MaxLength(500)]
        public string? KeywordHints { get; set; }

        /// <summary>
        /// Optional instruction for the LLM on how to extract the query parameter
        /// from the user's message (e.g., "Extract the city name from the question").
        /// </summary>
        [MaxLength(500)]
        public string? QueryExtractionHint { get; set; }

        /// <summary>
        /// Optional template for formatting the API response into a user-friendly answer.
        /// Can reference JSON path variables from the API response.
        /// </summary>
        [MaxLength(2000)]
        public string? ResponseFormatHint { get; set; }

        /// <summary>
        /// A sample prompt that can be used to test this tool's functionality.
        /// If empty, it can be auto-generated based on the description and keywords.
        /// </summary>
        [MaxLength(500)]
        public string? TestPrompt { get; set; }

        /// <summary>
        /// Display order in the admin UI.
        /// </summary>
        public int SortOrder { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
