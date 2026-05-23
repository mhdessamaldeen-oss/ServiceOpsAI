namespace SuperAdminCopilot.Pipeline.Routing;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Tiny LLM-driven fallback classifier. Called by <see cref="HybridIntentRouter"/> only when
/// no deterministic probe claims the question — so on a typical run this fires on maybe
/// 10–20% of questions, not all of them.
///
/// <para><b>Prompt discipline</b>: the system prompt is intentionally under 600 characters
/// — local Ollama models perform far better on short focused prompts than on long ones.
/// The user prompt is just the question. The model returns a single JSON object with one
/// label field. JSON-mode is enforced through <see cref="ILlmClient.GenerateJsonAsync"/>.</para>
///
/// <para>On parse failure the classifier returns <see cref="IntentLabel.Clarify"/> with low
/// confidence so the orchestrator's hard-fail path kicks in — better to ask the user to
/// rephrase than to dispatch to the wrong branch.</para>
/// </summary>
internal sealed class LlmIntentClassifier : ILlmIntentClassifier
{
    private readonly ILlmClient _llm;
    private readonly ILogger<LlmIntentClassifier> _logger;

    public LlmIntentClassifier(ILlmClient llm, ILogger<LlmIntentClassifier> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    // Compact system prompt — every byte costs the local model latency.
    // The 8 labels match IntentLabel exactly; instructions are minimal because labels are
    // self-explanatory. Hand-tuned: do NOT add examples; they balloon the prompt and bias
    // the model toward the example shapes rather than the actual question.
    private const string SystemPrompt =
@"Classify the user question into ONE label and return JSON: {""intent"":""<label>"",""confidence"":<0-1>}.

Labels:
- greeting: small talk (""hi"", ""thanks"", ""what can you do"").
- knowledge: asks what a domain term means (""what is a ticket"").
- semantic_search: free-text similarity (""tickets about login errors"", ""similar to TCK-100"").
- tool: needs an external API (weather, currency, location, holidays).
- metadata: asks about table/column structure.
- data_query: a real question over the database.
- refuse: write request, secrets, opinion/prediction.
- clarify: too vague to act on.

Reply with JSON only.";

    public async Task<RouterDecision> ClassifyAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new RouterDecision(IntentLabel.Clarify, 0.0, "llm-classifier", "empty question");

        string raw;
        try
        {
            using var hint = SuperAdminCopilot.Abstractions.LlmCallStageHint.Use("IntentClassifier");
            raw = await _llm.GenerateJsonAsync(SystemPrompt, $"Question: {question}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LlmIntentClassifier] LLM call failed; defaulting to data_query.");
            // Conservative fallback — most ambiguous questions are still data queries. The
            // planner has its own refusal handling for write/secrets that slipped past preflight.
            return new RouterDecision(IntentLabel.DataQuery, 0.4, "llm-classifier", $"llm error: {ex.Message}");
        }

        return Parse(raw);
    }

    /// <summary>Parse the JSON envelope into a <see cref="RouterDecision"/>. Designed to be
    /// forgiving — a small local model may emit slight format drift; we try to recover before
    /// surrendering to clarify-mode.</summary>
    internal static RouterDecision Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new RouterDecision(IntentLabel.Clarify, 0.0, "llm-classifier", "empty response");

        // Strip code fences if the model added them.
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence > 0) s = s[..fence];
        }
        // Trim to outer braces in case the model added prose before/after.
        var open = s.IndexOf('{');
        var close = s.LastIndexOf('}');
        if (open >= 0 && close > open) s = s.Substring(open, close - open + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var intentStr = root.TryGetProperty("intent", out var i) ? i.GetString() : null;
            double confidence = 0.7;
            if (root.TryGetProperty("confidence", out var c))
            {
                if (c.ValueKind == JsonValueKind.Number) confidence = c.GetDouble();
                else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var parsed))
                    confidence = parsed;
            }
            var label = MapLabel(intentStr);
            return new RouterDecision(label, Math.Clamp(confidence, 0.0, 1.0), "llm-classifier",
                $"llm intent='{intentStr}'");
        }
        catch (JsonException)
        {
            // Try a bare-label fall-back — some models reply with just the label word.
            var label = MapLabel(s.Trim('"', ' ', '\n', '\r'));
            return label == IntentLabel.Clarify
                ? new RouterDecision(IntentLabel.Clarify, 0.0, "llm-classifier", "unparseable response")
                : new RouterDecision(label, 0.6, "llm-classifier", "bare label fallback");
        }
    }

    /// <summary>Map the model's text answer to <see cref="IntentLabel"/>. Tolerant — accepts
    /// underscores or hyphens, ignores case, falls back to <see cref="IntentLabel.Clarify"/>
    /// for unknown values so the orchestrator's hard-fail path engages.</summary>
    private static IntentLabel MapLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return IntentLabel.Clarify;
        var normalized = raw.Trim().ToLowerInvariant().Replace("-", "_");
        return normalized switch
        {
            "greeting" => IntentLabel.Greeting,
            "knowledge" or "concept" => IntentLabel.Knowledge,
            "semantic_search" or "semantic" or "similarity" or "search" => IntentLabel.SemanticSearch,
            "tool" or "external_tool" or "api" => IntentLabel.Tool,
            "metadata" or "schema" => IntentLabel.Metadata,
            "data_query" or "data" or "sql" or "query" => IntentLabel.DataQuery,
            "refuse" or "out_of_scope" or "write" or "secret" => IntentLabel.Refuse,
            "clarify" or "clarification" or "unclear" or "ambiguous" => IntentLabel.Clarify,
            _ => IntentLabel.Clarify,
        };
    }
}
