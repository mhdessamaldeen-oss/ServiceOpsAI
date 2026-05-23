using ServiceOpsAI.Enums;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// Unified result returned by all AI providers.
    /// Replaces the old engine-specific result shape with a provider-agnostic contract.
    /// </summary>
    public class AiProviderResult
    {
        public bool Success { get; set; }
        public string ResponseText { get; set; } = "";
        public string? Error { get; set; }

        /// <summary>
        /// The provider type that generated this result.
        /// </summary>
        public AiProviderType ProviderType { get; set; }

        /// <summary>
        /// Token usage extracted from the provider's response, when the provider's API
        /// surfaces it. Gemini: <c>usageMetadata</c>. OpenAI / Groq / Cloud / Azure: <c>usage</c>.
        /// Ollama: <c>prompt_eval_count</c> + <c>eval_count</c>. Null when the provider didn't
        /// return usage data (e.g. some streaming responses, DockerModel stdout pipe).
        /// </summary>
        public TokenUsage? Usage { get; set; }

        /// <summary>The exact model id the call actually used. Providers may pick a default
        /// when none is specified, fall back from a missing override, or route via a key-pool;
        /// the planner / cost calculator needs the real model used to pull the right pricing.</summary>
        public string? ModelUsed { get; set; }
    }

    /// <summary>Token counts returned by the LLM provider. All three values are the
    /// provider's own counts; we don't tokenise locally. <c>Total</c> is reported by the
    /// provider where available — when it's missing we compute <c>Prompt + Completion</c>.</summary>
    public sealed class TokenUsage
    {
        public int Prompt { get; set; }
        public int Completion { get; set; }
        public int Total { get; set; }
    }
}
