using ServiceOpsAI.Enums;
using System.Text;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.Extensions.Options;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// AI provider for local OpenAI-compatible engines like LocalAI.
    /// Does not require a valid API key and is optimized for local performance.
    /// </summary>
    public class LocalAiProvider : IAiProvider
    {
        private readonly LocalAiProviderOptions _configOptions;
        private readonly ILogger<LocalAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.LocalAI;

        public LocalAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<LocalAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _configOptions = settings.Value.LocalAI;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public string ModelName => GetDbSetting(SettingKeys.LocalAiModel) ?? _configOptions.Model;
        public int ContextCapacity => GetMaxPromptChars() / 4;
        private string GetBaseUrl() => GetDbSetting(SettingKeys.LocalAiBaseUrl) ?? _configOptions.BaseUrl;
        private string GetApiKey() => GetDbSetting(SettingKeys.LocalAiApiKey) ?? _configOptions.ApiKey;
        private int GetTimeoutSeconds() => int.TryParse(GetDbSetting(SettingKeys.LocalAiTimeoutSeconds), out var value) ? value : _configOptions.TimeoutSeconds;
        private int GetMaxPromptChars() => int.TryParse(GetDbSetting(SettingKeys.LocalAiMaxPromptChars), out var value) ? value : _configOptions.MaxPromptChars;
        private double GetTemperature() => double.TryParse(GetDbSetting(SettingKeys.LocalAiTemperature), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : _configOptions.Temperature;

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var baseUrl = GetBaseUrl();
            var model = ModelName;

            try
            {
                var safePrompt = TruncatePrompt(prompt, GetMaxPromptChars());
                _logger.LogInformation("Sending prompt to LocalAI model '{Model}' at {BaseUrl}", model, baseUrl);

                using var httpClient = new HttpClient { 
                    BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds())
                };
                httpClient.DefaultRequestHeaders.Add("User-Agent", "SupportFlow-AI-Platform/2.0");
                
                // LocalAI often requires an Authorization header even if it's junk
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetApiKey()}");

                var requestBody = new
                {
                    model = model,
                    messages = new[]
                    {
                        new { role = "system", content = "You are a helpful support analyst. Respond in valid JSON." },
                        new { role = "user", content = safePrompt }
                    },
                    temperature = GetTemperature()
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    return new AiProviderResult { Success = false, Error = $"LocalAI error {(int)response.StatusCode}: {errorBody}", ProviderType = AiProviderType.LocalAI };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var responseText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = responseText.Trim(),
                    ProviderType = AiProviderType.LocalAI,
                    Usage = OpenAiUsageParser.ParseUsage(doc.RootElement),
                    ModelUsed = OpenAiUsageParser.ReadModel(doc.RootElement)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalAI call failed");
                return new AiProviderResult { Success = false, Error = ex.Message, ProviderType = AiProviderType.LocalAI };
            }
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            try
            {
                var baseUrl = GetBaseUrl().TrimEnd('/') + "/";
                using var http = new HttpClient { 
                    BaseAddress = new Uri(baseUrl),
                    Timeout = TimeSpan.FromSeconds(5) 
                };
                var response = await http.GetAsync("models");
                if (response.IsSuccessStatusCode) return (true, null);
                return (false, $"LocalAI returned HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) { return (false, $"Cannot connect to LocalAI: {ex.Message}"); }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var baseUrl = GetBaseUrl();
            var model = ModelName;

            try
            {
                using var httpClient = new HttpClient { 
                    BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(60)
                };
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetApiKey()}");

                var requestBody = new { model = model, input = text };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Determine path: if baseUrl ends in /v1, use "embeddings", else use "v1/embeddings"
                var relativePath = baseUrl.TrimEnd('/').EndsWith("/v1") ? "embeddings" : "v1/embeddings";
                var response = await httpClient.PostAsync(relativePath, content);
                if (!response.IsSuccessStatusCode) 
                {
                    var debug = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("LocalAI Embedding failed: {Status} {Debug}", response.StatusCode, debug);
                    return Array.Empty<float>();
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                
                var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
                var embedding = new float[data.GetArrayLength()];
                for (int i = 0; i < embedding.Length; i++)
                {
                    embedding[i] = (float)data[i].GetDouble();
                }

                return embedding;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalAI embedding call failed");
                return Array.Empty<float>();
            }
        }

        private string? GetDbSetting(string key)
        {
            try {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.SystemSettings.FirstOrDefault(s => s.Key == key)?.Value;
            } catch { return null; }
        }

        private string TruncatePrompt(string prompt, int maxChars)
        {
            if (prompt.Length <= maxChars) return prompt;
            return prompt[..maxChars] + "... [TRUNCATED]";
        }
        public Task<List<ServiceOpsAI.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            return Task.FromResult(new List<ServiceOpsAI.Models.DTOs.AiModelDto>());
        }
    }
}

