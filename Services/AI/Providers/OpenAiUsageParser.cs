using System.Text.Json;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// Shared parser for the OpenAI-style <c>usage</c> object that OpenAI, Groq, Azure /
    /// Cloud, LocalAI, and most OSS chat-completion endpoints emit. Centralised here so the
    /// six providers that speak this dialect don't each carry their own copy.
    /// </summary>
    internal static class OpenAiUsageParser
    {
        public static TokenUsage? ParseUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage)) return null;
            int prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
            int completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            int total = usage.TryGetProperty("total_tokens", out var t) && t.TryGetInt32(out var tv) ? tv : (prompt + completion);
            if (prompt == 0 && completion == 0 && total == 0) return null;
            return new TokenUsage { Prompt = prompt, Completion = completion, Total = total };
        }

        /// <summary>Some endpoints echo the resolved model id back in the response root —
        /// useful when the request used a routing alias and the operator wants the real
        /// model for cost lookup.</summary>
        public static string? ReadModel(JsonElement root) =>
            root.TryGetProperty("model", out var m) ? m.GetString() : null;
    }
}
