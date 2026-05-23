using ServiceOpsAI.Enums;
using System.Text;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.Extensions.Options;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// AI provider for OpenAI / GPT-style chat completion endpoints.
    /// Reads connection settings from DB first, with fallback to appsettings.json.
    /// The model is no longer selected from the admin UI.
    /// </summary>
    public class OpenAiProvider : IAiProvider
    {
        private readonly OpenAiProviderOptions _configOptions;
        private readonly ILogger<OpenAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.OpenAI;

        public OpenAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<OpenAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _configOptions = settings.Value.OpenAI;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public string ModelName => GetDbSetting(SettingKeys.OpenAiModel) ?? _configOptions.Model;
        public int ContextCapacity => 128000;

        /// <summary>
        /// Reads API key from DB, falls back to config.
        /// </summary>
        private string GetApiKey() => GetDbSetting(SettingKeys.OpenAiApiKey) ?? _configOptions.ApiKey;

        /// <summary>
        /// Reads base URL from DB, falls back to config.
        /// </summary>
        private string GetBaseUrl() => GetDbSetting(SettingKeys.OpenAiBaseUrl) ?? _configOptions.BaseUrl;
        private int GetTimeoutSeconds() => GetIntSetting(SettingKeys.OpenAiTimeoutSeconds, _configOptions.TimeoutSeconds);
        private int GetMaxPromptChars() => GetIntSetting(SettingKeys.OpenAiMaxPromptChars, _configOptions.MaxPromptChars);
        private double GetTemperature() => GetDoubleSetting(SettingKeys.OpenAiTemperature, _configOptions.Temperature);
        private int GetMaxTokens() => GetIntSetting(SettingKeys.OpenAiMaxTokens, _configOptions.MaxTokens);
        private string? GetOrganizationId() => GetDbSetting(SettingKeys.OpenAiOrganizationId) ?? _configOptions.OrganizationId;
        private string? GetProjectId() => GetDbSetting(SettingKeys.OpenAiProjectId) ?? _configOptions.ProjectId;

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AiProviderResult
                {
                    Success = false,
                    Error = "OpenAI API key is not configured. Go to Settings → AI Provider Configuration → OpenAI tab to set your API key.",
                    ProviderType = AiProviderType.OpenAI
                };
            }

            var baseUrl = GetBaseUrl();
            var model = ModelName;

            try
            {
                var safePrompt = TruncatePrompt(prompt, GetMaxPromptChars());

                _logger.LogInformation("Sending prompt to OpenAI model '{Model}' ({Length} chars)", model, safePrompt.Length);

                using var httpClient = CreateHttpClient(baseUrl, apiKey);

                var requestBody = new
                {
                    model = model,
                    messages = new[]
                    {
                        new { role = "system", content = "You are a technical support analyst assistant. Respond only with valid JSON as instructed." },
                        new { role = "user", content = safePrompt }
                    },
                    temperature = GetTemperature(),
                    max_tokens = GetMaxTokens()
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Use relative path without leading slash to keep BaseAddress path (like /v1)
                var response = await httpClient.PostAsync("chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("OpenAI returned {StatusCode}: {Body}", response.StatusCode, errorBody);

                    var errorMessage = ParseOpenAiError(errorBody, (int)response.StatusCode);
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = errorMessage,
                        ProviderType = AiProviderType.OpenAI
                    };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);

                var responseText = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                _logger.LogInformation("OpenAI response received ({Length} chars)", responseText.Length);

                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = responseText.Trim(),
                    ProviderType = AiProviderType.OpenAI,
                    Usage = OpenAiUsageParser.ParseUsage(doc.RootElement),
                    ModelUsed = OpenAiUsageParser.ReadModel(doc.RootElement) ?? model
                };
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("OpenAI request timed out for model '{Model}'", model);
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Request to OpenAI timed out after {GetTimeoutSeconds()}s.",
                    ProviderType = AiProviderType.OpenAI
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Cannot connect to OpenAI at {BaseUrl}", baseUrl);
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Cannot connect to OpenAI at {baseUrl}. Error: {ex.Message}",
                    ProviderType = AiProviderType.OpenAI
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error calling OpenAI");
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Unexpected error: {ex.Message}",
                    ProviderType = AiProviderType.OpenAI
                };
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<float>();

            var baseUrl = GetBaseUrl();
            // Default embedding model for OpenAI if not explicitly specified for embeddings
            var model = "text-embedding-3-small"; 

            try
            {
                using var httpClient = CreateHttpClient(baseUrl, apiKey);
                var requestBody = new { model = model, input = text };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("embeddings", content);
                if (!response.IsSuccessStatusCode) return Array.Empty<float>();

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
                _logger.LogError(ex, "OpenAI embedding call failed");
                return Array.Empty<float>();
            }
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "OpenAI API key is not configured. Set it in Settings → AI Provider Configuration → OpenAI tab.");

            if (string.IsNullOrWhiteSpace(ModelName))
                return (false, "OpenAI model name is not configured.");

            try
            {
                using var httpClient = CreateHttpClient(GetBaseUrl(), apiKey);
                // Use relative path without leading slash
                var response = await httpClient.GetAsync("models");
                if (response.IsSuccessStatusCode)
                    return (true, null);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "OpenAI API key is invalid or expired.");

                return (false, $"OpenAI returned HTTP {(int)response.StatusCode} during validation.");
            }
            catch (HttpRequestException ex)
            {
                return (false, $"Cannot connect to OpenAI at {GetBaseUrl()}. Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Unexpected error validating OpenAI configuration: {ex.Message}");
            }
        }

        private HttpClient CreateHttpClient(string baseUrl, string apiKey)
        {
            // Ensure trailing slash for proper path concatenation in HttpClient
            var formattedBaseUrl = baseUrl.TrimEnd('/') + "/";
            
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(formattedBaseUrl),
                Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds())
            };

            var organizationId = GetOrganizationId();
            if (!string.IsNullOrWhiteSpace(organizationId))
                httpClient.DefaultRequestHeaders.Add("OpenAI-Organization", organizationId);

            var projectId = GetProjectId();
            if (!string.IsNullOrWhiteSpace(projectId))
                httpClient.DefaultRequestHeaders.Add("OpenAI-Project", projectId);

            httpClient.DefaultRequestHeaders.Add("User-Agent", "SupportFlow-AI-Platform/2.0");
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            if (!string.IsNullOrWhiteSpace(_configOptions.OrganizationId))
                httpClient.DefaultRequestHeaders.Add("OpenAI-Organization", _configOptions.OrganizationId);

            if (!string.IsNullOrWhiteSpace(_configOptions.ProjectId))
                httpClient.DefaultRequestHeaders.Add("OpenAI-Project", _configOptions.ProjectId);

            return httpClient;
        }

        /// <summary>
        /// Reads a setting from DB SystemSettings, returning null if not found.
        /// </summary>
        private string? GetDbSetting(string key)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var setting = db.SystemSettings.FirstOrDefault(s => s.Key == key);
                return !string.IsNullOrWhiteSpace(setting?.Value) ? setting.Value : null;
            }
            catch
            {
                return null;
            }
        }

        private int GetIntSetting(string key, int fallback)
            => int.TryParse(GetDbSetting(key), out var value) ? value : fallback;

        private double GetDoubleSetting(string key, double fallback)
            => double.TryParse(GetDbSetting(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private string TruncatePrompt(string prompt, int maxChars)
        {
            if (prompt.Length <= maxChars) return prompt;

            var instructionsIdx = prompt.IndexOf("=== INSTRUCTIONS ===", StringComparison.Ordinal);
            if (instructionsIdx < 0)
                return prompt[..maxChars] + "\n[PROMPT TRUNCATED]";

            var instructions = prompt[instructionsIdx..];
            var availableForContext = maxChars - instructions.Length - 100;
            if (availableForContext < 500) availableForContext = 500;

            var contextPart = prompt[..instructionsIdx];
            if (contextPart.Length > availableForContext)
                contextPart = contextPart[..availableForContext] + "\n\n[... CONTEXT TRUNCATED to fit model limits ...]\n\n";

            return contextPart + instructions;
        }

        private static string ParseOpenAiError(string errorBody, int statusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorObj))
                {
                    var message = errorObj.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                    var type = errorObj.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (!string.IsNullOrEmpty(message))
                        return $"OpenAI API error ({type ?? "unknown"}): {message}";
                }
            }
            catch { }

            return $"OpenAI returned HTTP {statusCode}: {errorBody}";
        }

        public async Task<List<ServiceOpsAI.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            var results = new List<ServiceOpsAI.Models.DTOs.AiModelDto>();
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            try
            {
                using var http = CreateHttpClient(GetBaseUrl(), apiKey);
                var response = await http.GetAsync("models");
                if (!response.IsSuccessStatusCode) return results;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var arr)) return results;

                var activeModel = ModelName;
                foreach (var m in arr.EnumerateArray())
                {
                    if (!m.TryGetProperty("id", out var idEl)) continue;
                    var name = idEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var owned = m.TryGetProperty("owned_by", out var o) ? o.GetString() : null;

                    results.Add(new ServiceOpsAI.Models.DTOs.AiModelDto
                    {
                        Name = name,
                        Family = owned,
                        IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list OpenAI models");
            }
            return results.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

