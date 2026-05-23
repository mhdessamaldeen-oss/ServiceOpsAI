using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// Pricing for an LLM model identified by (Provider, Model). Stored in the database
    /// rather than appsettings because prices change without warning and need to be edited
    /// by admins without a deploy. The (Provider, Model) tuple is unique. Local / self-hosted
    /// models can be priced at 0 — the token counts still flow through, which is useful for
    /// latency forecasting and "what would this cost on a cloud model" comparisons.
    /// <para>Per-token pricing is stored as USD per 1,000 tokens (the industry-standard
    /// quote unit) using <c>decimal(18,8)</c> so a $0.000075 / 1K rate isn't rounded.</para>
    /// </summary>
    [Index(nameof(Provider), nameof(Model), IsUnique = true, Name = "IX_ModelPricing_Provider_Model")]
    public class ModelPricing
    {
        public int Id { get; set; }

        /// <summary>Logical provider name (e.g. "gemini", "openai", "groq", "ollama",
        /// "cloud", "docker", "local"). Matches <see cref="AISupportAnalysisPlatform.Services.AI.Providers.AiProviderType"/>
        /// stringified, but kept as a free string so new providers don't require an enum migration.</summary>
        [Required, StringLength(50)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>The provider's model identifier as it appears in API calls
        /// (e.g. "gemini-2.0-flash", "gpt-4o-mini", "qwen2.5-coder:7b", "llama-3.1-8b").</summary>
        [Required, StringLength(150)]
        public string Model { get; set; } = string.Empty;

        /// <summary>Optional human-readable label shown in the admin UI when "gemini-2.0-flash"
        /// isn't friendly enough. Falls back to <see cref="Model"/> when null.</summary>
        [StringLength(150)]
        public string? DisplayName { get; set; }

        /// <summary>USD cost per 1,000 input (prompt) tokens. Default 0 for local models.</summary>
        [Column(TypeName = "decimal(18,8)")]
        public decimal InputPer1K { get; set; }

        /// <summary>USD cost per 1,000 output (completion) tokens.</summary>
        [Column(TypeName = "decimal(18,8)")]
        public decimal OutputPer1K { get; set; }

        /// <summary>Three-letter currency code stored alongside the rate so a future
        /// multi-currency setup doesn't require a schema migration. Default USD.</summary>
        [StringLength(3)]
        public string Currency { get; set; } = "USD";

        /// <summary>Context-window size in tokens. Informational — surfaced in the admin UI
        /// alongside the rate so the operator can tell "qwen 7b: 8K context, free" at a glance.
        /// Not used for cost math.</summary>
        public int? ContextTokens { get; set; }

        /// <summary>True for local / self-hosted models that don't actually charge per call.
        /// The cost calculator still applies the per-token rate (which can be 0 or a shadow
        /// price set by the operator for cloud-equivalence comparison), but the chat UI may
        /// label these "free" when both rates are 0.</summary>
        public bool IsLocal { get; set; }

        /// <summary>Soft-disable a row without deleting it (e.g. a deprecated model the
        /// operator wants to keep for historical cost queries). Pricing lookups skip
        /// inactive rows.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Free-text notes for the admin (e.g. "promotional rate through Q3-2026",
        /// "internal estimate — provider doesn't publish a list price").</summary>
        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
