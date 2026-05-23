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
    /// AI provider for Groq (LPU inference). OpenAI-compatible chat completions API. Captures Groq's
    /// `x-ratelimit-remaining-*` response headers on every successful call so the UI can show real
    /// remaining quota — no estimation required (unlike Gemini).
    /// </summary>
    public class GroqAiProvider : IAiProvider
    {
        private const string DefaultBaseUrl = "https://api.groq.com/openai/v1";
        private const string DefaultModel = "llama-3.3-70b-versatile";
        private const int DefaultContextCapacity = 128_000;

        private readonly ILogger<GroqAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRetryPolicy _retryPolicy;
        private readonly IGroqKeyPool _keyPool;

        public AiProviderType ProviderType => AiProviderType.Groq;

        public GroqAiProvider(
            ILogger<GroqAiProvider> logger,
            IServiceProvider serviceProvider,
            GroqRetryPolicy retryPolicy,
            IGroqKeyPool keyPool)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _retryPolicy = retryPolicy;
            _keyPool = keyPool;
        }

        public string ModelName => GetDbSetting(SettingKeys.GroqModel) ?? DefaultModel;
        public int ContextCapacity => DefaultContextCapacity;
        private string? GetLegacyApiKey() => GetDbSetting(SettingKeys.GroqApiKey);
        private double GetTemperature() =>
            double.TryParse(GetDbSetting(SettingKeys.GroqTemperature),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.2;
        private int GetMaxTokens() =>
            int.TryParse(GetDbSetting(SettingKeys.GroqMaxTokens), out var v) && v > 0 ? v : 2048;

        public Task<AiProviderResult> GenerateAsync(string prompt) =>
            _retryPolicy.ExecuteAsync(() => GenerateOnceAsync(prompt));

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
                    Error = "No Groq API key available — pool is exhausted/rate-limited and no legacy key is configured.",
                    ProviderType = ProviderType
                };
            }

            var model = ModelName;
            var url = $"{DefaultBaseUrl}/chat/completions";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                var requestBody = new
                {
                    model,
                    messages = new[] { new { role = "user", content = prompt } },
                    temperature = GetTemperature(),
                    max_tokens = GetMaxTokens()
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = $"Groq HTTP {(int)response.StatusCode}: {Truncate(responseBody, 800)}";
                    if (keyId.HasValue)
                    {
                        var statusCode = (int)response.StatusCode;
                        if (statusCode == 429)
                        {
                            // Use the retry-after hint if present in the body, else 60s default
                            var parkMs = ParseRetryAfterMs(responseBody) ?? 60_000;
                            await _keyPool.ReportRateLimitedAsync(keyId.Value, parkMs, error);
                        }
                        else if (statusCode >= 500)
                        {
                            // Server-side hiccup — short park, don't penalize
                            await _keyPool.ReportRateLimitedAsync(keyId.Value, 5_000, error);
                        }
                        else
                        {
                            await _keyPool.ReportFailureAsync(keyId.Value, error);
                        }
                    }
                    return new AiProviderResult { Success = false, Error = error, ProviderType = ProviderType };
                }

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // Capture quota snapshot from response headers — this is Groq's killer feature vs Gemini.
                var quota = ExtractQuotaSnapshot(response.Headers);
                if (keyId.HasValue) await _keyPool.ReportSuccessAsync(keyId.Value, quota);

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Groq returned no choices. Raw: {Truncate(responseBody, 600)}",
                        ProviderType = ProviderType
                    };
                }

                var first = choices[0];
                if (!first.TryGetProperty("message", out var msg) ||
                    !msg.TryGetProperty("content", out var contentEl))
                {
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Groq returned no message content. Raw: {Truncate(responseBody, 600)}",
                        ProviderType = ProviderType
                    };
                }

                var text = contentEl.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Groq returned empty text. Raw: {Truncate(responseBody, 600)}",
                        ProviderType = ProviderType
                    };
                }

                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = text,
                    ProviderType = ProviderType,
                    Usage = OpenAiUsageParser.ParseUsage(root),
                    ModelUsed = OpenAiUsageParser.ReadModel(root) ?? model
                };
            }
            catch (Exception ex)
            {
                var msg = $"Groq exception: {ex.GetType().Name}: {ex.Message}";
                if (keyId.HasValue) await _keyPool.ReportFailureAsync(keyId.Value, msg);
                return new AiProviderResult { Success = false, Error = msg, ProviderType = ProviderType };
            }
        }

        /// <summary>
        /// Pull the `x-ratelimit-*` headers Groq returns on every response. These are authoritative —
        /// no need to estimate like with Gemini. Returns null if no headers present (parse error / proxy).
        /// </summary>
        private static GroqQuotaSnapshot? ExtractQuotaSnapshot(System.Net.Http.Headers.HttpResponseHeaders headers)
        {
            int? remReq = ParseIntHeader(headers, "x-ratelimit-remaining-requests");
            long? remTok = ParseLongHeader(headers, "x-ratelimit-remaining-tokens");
            int? limReq = ParseIntHeader(headers, "x-ratelimit-limit-requests");
            long? limTok = ParseLongHeader(headers, "x-ratelimit-limit-tokens");

            if (remReq == null && remTok == null && limReq == null && limTok == null) return null;

            return new GroqQuotaSnapshot
            {
                RemainingRequests = remReq,
                RemainingTokens = remTok,
                LimitRequests = limReq,
                LimitTokens = limTok
            };
        }

        private static int? ParseIntHeader(System.Net.Http.Headers.HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) &&
            int.TryParse(values.FirstOrDefault(), out var v) ? v : (int?)null;

        private static long? ParseLongHeader(System.Net.Http.Headers.HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) &&
            long.TryParse(values.FirstOrDefault(), out var v) ? v : (long?)null;

        private static int? ParseRetryAfterMs(string? body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(body, @"try again in (\d+(?:\.\d+)?)s",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                return (int)Math.Ceiling(sec * 1000);
            }
            return null;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...");

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            // Groq does not currently host embedding models — return empty so callers fall back.
            await Task.CompletedTask;
            return Array.Empty<float>();
        }

        public Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            var apiKey = GetLegacyApiKey();
            // Pool may have keys even when legacy is null — but ValidateConfigurationAsync is sync-fast
            // and the pool lookup is async; treat the simpler legacy presence as the configured signal.
            // The ValidateProvider HTTP endpoint does a real-call check separately.
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Task.FromResult<(bool, string?)>((false, "Missing Groq API Key (no legacy key set; pool not consulted in this fast path)."));
            }
            return Task.FromResult<(bool, string?)>((true, null));
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

            // Find any usable key — pool first, legacy fallback
            var pooled = await _keyPool.AcquireAsync();
            var apiKey = pooled?.ApiKey ?? GetLegacyApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                var response = await http.GetAsync($"{DefaultBaseUrl}/models");
                if (!response.IsSuccessStatusCode) return results;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var arr)) return results;

                var activeModel = ModelName;
                foreach (var m in arr.EnumerateArray())
                {
                    if (!m.TryGetProperty("id", out var idEl)) continue;
                    var name = idEl.GetString() ?? "";
                    var ctx = m.TryGetProperty("context_window", out var c) ? c.GetInt32().ToString("N0") : null;
                    var owned = m.TryGetProperty("owned_by", out var o) ? o.GetString() : null;
                    var active = m.TryGetProperty("active", out var a) ? a.GetBoolean() : true;
                    if (!active) continue;

                    results.Add(new AISupportAnalysisPlatform.Models.DTOs.AiModelDto
                    {
                        Name = name,
                        Family = owned,
                        ContextWindow = ctx,
                        IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list Groq models");
            }
            return results;
        }
    }
}
