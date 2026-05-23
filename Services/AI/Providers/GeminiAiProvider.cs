using AISupportAnalysisPlatform.Enums;
using System.Text;
using System.Text.Json;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Services.AI.Providers.KeyPool;
using AISupportAnalysisPlatform.Services.AI.Providers.Retry;
using Microsoft.Extensions.Options;

namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// AI provider for Google Gemini (AI Studio / Vertex AI). Handles Gemini's native JSON shape for
    /// generation and embeddings; rate-limit retry behavior lives in <see cref="GeminiRateLimitPolicy"/>.
    /// </summary>
    public class GeminiAiProvider : IAiProvider
    {
        private readonly CloudProviderOptions _configOptions;
        private readonly ILogger<GeminiAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRetryPolicy _retryPolicy;
        private readonly IGeminiKeyPool _keyPool;

        public AiProviderType ProviderType => AiProviderType.Gemini;

        public GeminiAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<GeminiAiProvider> logger,
            IServiceProvider serviceProvider,
            GeminiRateLimitPolicy retryPolicy,
            IGeminiKeyPool keyPool)
        {
            _configOptions = settings.Value.Cloud;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _retryPolicy = retryPolicy;
            _keyPool = keyPool;
        }

        public string ModelName => GetDbSetting(SettingKeys.GeminiModel) ?? _configOptions.Model ?? "gemini-2.5-flash";
        public int ContextCapacity => 1_048_576;
        private string? GetLegacyApiKey() => GetDbSetting(SettingKeys.GeminiApiKey) ?? _configOptions.ApiKey;
        private double GetTemperature() => double.TryParse(GetDbSetting(SettingKeys.GeminiTemperature), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : _configOptions.Temperature;
        private int GetMaxTokens() => int.TryParse(GetDbSetting(SettingKeys.GeminiMaxTokens), out var value) ? value : _configOptions.MaxTokens;

        public Task<AiProviderResult> GenerateAsync(string prompt) =>
            _retryPolicy.ExecuteAsync(() => GenerateOnceAsync(prompt));

        /// <summary>
        /// Pull the next available key from the pool; if the pool is empty fall back to the legacy
        /// single key in SystemSettings. Returns (keyId=null, key=null) when nothing is available
        /// at all — caller surfaces a "no keys" error.
        /// </summary>
        private async Task<(int? KeyId, string? ApiKey, string Source)> AcquireKeyAsync()
        {
            var pooled = await _keyPool.AcquireAsync();
            if (pooled != null) return (pooled.Id, pooled.ApiKey, "pool");

            var legacy = GetLegacyApiKey();
            if (!string.IsNullOrWhiteSpace(legacy)) return (null, legacy, "legacy");

            return (null, null, "none");
        }

        private async Task<AiProviderResult> GenerateOnceAsync(string prompt)
        {
            var (keyId, apiKey, source) = await AcquireKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AiProviderResult
                {
                    Success = false,
                    Error = "No Gemini API key available — pool is exhausted/rate-limited and no legacy key is configured.",
                    ProviderType = ProviderType
                };
            }

            var model = ModelName;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_configOptions.TimeoutSeconds > 0 ? _configOptions.TimeoutSeconds : 120) };

                // Cap MaxTokens at a sane floor — old config default of 1000 truncated gemini-1.5-flash
                // mid-output for our larger structured-plan prompts (6 worked examples + schema), causing
                // empty `parts` arrays and "[no response from LLM]" in traces.
                var maxOut = Math.Max(GetMaxTokens(), 4096);

                var requestBody = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new
                    {
                        temperature = GetTemperature(),
                        maxOutputTokens = maxOut,
                        // Force structured JSON mode when the prompt smells like JSON — gemini honors this and
                        // strips Markdown fences itself, eliminating the "```json ... ```" parse failures.
                        responseMimeType = LooksLikeJsonPrompt(prompt) ? "application/json" : "text/plain"
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = $"Gemini HTTP {(int)response.StatusCode}: {Truncate(responseBody, 800)}";
                    if (keyId.HasValue)
                    {
                        var statusCode = (int)response.StatusCode;
                        if (statusCode == 429)
                        {
                            // The retry policy reads Google's retryDelay from the body — for the pool we
                            // just need to mark the key parked. Use 60s as a conservative default; if the
                            // policy retries with the same key it'll re-trigger here and we'll re-park it.
                            await _keyPool.ReportRateLimitedAsync(keyId.Value, 60_000, error);
                        }
                        else if (statusCode == 503)
                        {
                            // 503 = "model is overloaded" on Google's side — the key is fine. Logging the
                            // error to surface in the UI but NOT incrementing ConsecutiveFailures, otherwise
                            // a Google outage would cascade into auto-disabling every key in the pool.
                            await _keyPool.ReportRateLimitedAsync(keyId.Value, 5_000, error);
                        }
                        else
                        {
                            await _keyPool.ReportFailureAsync(keyId.Value, error);
                        }
                    }
                    return new AiProviderResult { Success = false, Error = error, ProviderType = ProviderType };
                }

                // Gemini can return success with NO parts (MAX_TOKENS, SAFETY filter, RECITATION block).
                // Walk the shape defensively and return the actual finishReason / blockReason as the error.
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // Anything past this point is a successful HTTP call (so the key worked) — increment
                // its usage counter even if the response shape is unusable. Quota was consumed.
                if (keyId.HasValue) await _keyPool.ReportSuccessAsync(keyId.Value);

                if (root.TryGetProperty("promptFeedback", out var pf) &&
                    pf.TryGetProperty("blockReason", out var br))
                {
                    return new AiProviderResult { Success = false, Error = $"Gemini blocked prompt: {br.GetString()}", ProviderType = ProviderType };
                }

                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    return new AiProviderResult { Success = false, Error = $"Gemini returned no candidates. Raw: {Truncate(responseBody, 600)}", ProviderType = ProviderType };
                }

                var first = candidates[0];
                var finishReason = first.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;

                if (!first.TryGetProperty("content", out var cnt) ||
                    !cnt.TryGetProperty("parts", out var parts) ||
                    parts.GetArrayLength() == 0)
                {
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Gemini returned no content parts (finishReason={finishReason ?? "unknown"}, MaxTokens={maxOut}). " +
                                $"Raw: {Truncate(responseBody, 600)}",
                        ProviderType = ProviderType
                    };
                }

                var text = parts[0].TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Gemini returned empty text (finishReason={finishReason ?? "unknown"}). Raw: {Truncate(responseBody, 600)}",
                        ProviderType = ProviderType
                    };
                }

                // Gemini surfaces token counts under usageMetadata at the response root.
                // Three fields, all optional in older API versions; safe property access only.
                TokenUsage? usage = null;
                if (root.TryGetProperty("usageMetadata", out var um))
                {
                    int promptToks = um.TryGetProperty("promptTokenCount", out var pt) && pt.TryGetInt32(out var p) ? p : 0;
                    int completionToks = um.TryGetProperty("candidatesTokenCount", out var ct) && ct.TryGetInt32(out var c) ? c : 0;
                    int totalToks = um.TryGetProperty("totalTokenCount", out var tt) && tt.TryGetInt32(out var tot) ? tot : (promptToks + completionToks);
                    if (promptToks > 0 || completionToks > 0 || totalToks > 0)
                        usage = new TokenUsage { Prompt = promptToks, Completion = completionToks, Total = totalToks };
                }

                // (Pool counter already incremented above when the HTTP call succeeded.)
                return new AiProviderResult { Success = true, ResponseText = text, ProviderType = ProviderType, Usage = usage, ModelUsed = model };
            }
            catch (Exception ex)
            {
                var msg = $"Gemini exception: {ex.GetType().Name}: {ex.Message}";
                if (keyId.HasValue) await _keyPool.ReportFailureAsync(keyId.Value, msg);
                return new AiProviderResult { Success = false, Error = msg, ProviderType = ProviderType };
            }
        }

        private static bool LooksLikeJsonPrompt(string prompt) =>
            !string.IsNullOrEmpty(prompt) &&
            (prompt.Contains("Output ONLY valid JSON", StringComparison.OrdinalIgnoreCase)
             || prompt.Contains("Output JSON ONLY", StringComparison.OrdinalIgnoreCase)
             || prompt.Contains("\"Steps\"", StringComparison.Ordinal));

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...");

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var apiKey = GetLegacyApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<float>();

            // Gemini embedding models are different from chat models
            var model = ModelName.Contains("embedding") ? ModelName : "text-embedding-004";
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent?key={apiKey}";

            try
            {
                using var client = new HttpClient();
                var requestBody = new { content = new { parts = new[] { new { text = text } } } };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);

                if (!response.IsSuccessStatusCode) return Array.Empty<float>();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

                var result = new float[values.GetArrayLength()];
                for (int i = 0; i < result.Length; i++) result[i] = (float)values[i].GetDouble();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini embedding failed");
                return Array.Empty<float>();
            }
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            if (string.IsNullOrWhiteSpace(GetLegacyApiKey())) return (false, "Missing Gemini API Key.");
            return (true, null);
        }

        private string? GetDbSetting(string key)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return db.SystemSettings.FirstOrDefault(s => s.Key == key)?.Value;
        }

        public async Task<List<AISupportAnalysisPlatform.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            var results = new List<AISupportAnalysisPlatform.Models.DTOs.AiModelDto>();
            var apiKey = GetLegacyApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var response = await http.GetAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
                if (!response.IsSuccessStatusCode) return results;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("models", out var modelsArray)) return results;

                var activeModel = ModelName;
                foreach (var m in modelsArray.EnumerateArray())
                {
                    if (!m.TryGetProperty("name", out var nameEl)) continue;
                    var fullName = nameEl.GetString() ?? "";
                    var name = fullName.StartsWith("models/") ? fullName.Substring(7) : fullName;

                    if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        var supports = methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                        if (!supports) continue;
                    }

                    var family = m.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                    var ctx = m.TryGetProperty("inputTokenLimit", out var tl) ? tl.GetInt32().ToString("N0") : null;

                    results.Add(new AISupportAnalysisPlatform.Models.DTOs.AiModelDto
                    {
                        Name = name,
                        Family = family,
                        ContextWindow = ctx,
                        IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list Gemini models");
            }
            return results;
        }
    }
}
