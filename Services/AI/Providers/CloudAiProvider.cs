using ServiceOpsAI.Enums;
using System.Text;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.Extensions.Options;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// AI provider for generic cloud AI endpoints.
    /// Reads endpoint, API key, model, and deployment from DB SystemSettings (set via UI) first,
    /// with fallback to appsettings.json configuration.
    /// Supports Azure OpenAI, custom hosted models, and any OpenAI-compatible API.
    /// </summary>
    public class CloudAiProvider : IAiProvider
    {
        private readonly CloudProviderOptions _configOptions;
        private readonly ILogger<CloudAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.Cloud;

        public CloudAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<CloudAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _configOptions = settings.Value.Cloud;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public string ModelName => GetDbSetting(SettingKeys.CloudModel) ?? _configOptions.Model;
        public int ContextCapacity => 128000;
        private string GetEndpoint() => GetDbSetting(SettingKeys.CloudEndpoint) ?? _configOptions.Endpoint;
        private string GetApiKey() => GetDbSetting(SettingKeys.CloudApiKey) ?? _configOptions.ApiKey;
        private string GetDeploymentName() => GetDbSetting(SettingKeys.CloudDeploymentName) ?? _configOptions.DeploymentName ?? "";
        private int GetTimeoutSeconds() => GetIntSetting(SettingKeys.CloudTimeoutSeconds, _configOptions.TimeoutSeconds);
        private int GetMaxPromptChars() => GetIntSetting(SettingKeys.CloudMaxPromptChars, _configOptions.MaxPromptChars);
        private double GetTemperature() => GetDoubleSetting(SettingKeys.CloudTemperature, _configOptions.Temperature);
        private int GetMaxTokens() => GetIntSetting(SettingKeys.CloudMaxTokens, _configOptions.MaxTokens);
        private string GetApiVersion() => GetDbSetting(SettingKeys.CloudApiVersion) ?? _configOptions.ApiVersion ?? "2024-02-01";
        private string GetAuthHeaderName() => GetDbSetting(SettingKeys.CloudAuthHeaderName) ?? _configOptions.AuthHeaderName;
        private bool GetUseBearerToken() => bool.TryParse(GetDbSetting(SettingKeys.CloudUseBearerToken), out var value) ? value : _configOptions.UseBearerToken;

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return new AiProviderResult
                {
                    Success = false,
                    Error = "Cloud AI endpoint is not configured. Go to Settings → AI Provider Configuration → Cloud AI tab.",
                    ProviderType = AiProviderType.Cloud
                };
            }

            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AiProviderResult
                {
                    Success = false,
                    Error = "Cloud AI API key is not configured. Go to Settings → AI Provider Configuration → Cloud AI tab.",
                    ProviderType = AiProviderType.Cloud
                };
            }

            var model = ModelName;

            try
            {
                var safePrompt = TruncatePrompt(prompt, GetMaxPromptChars());

                _logger.LogInformation("Sending prompt to Cloud AI model '{Model}' at {Endpoint} ({Length} chars)",
                    model, endpoint, safePrompt.Length);

                using var httpClient = CreateHttpClient(endpoint, apiKey);
                var requestPath = BuildRequestPath(isEmbedding: false);

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

                var response = await httpClient.PostAsync(requestPath, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Cloud AI returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                    return new AiProviderResult
                    {
                        Success = false,
                        Error = $"Cloud AI returned HTTP {(int)response.StatusCode}: {errorBody}",
                        ProviderType = AiProviderType.Cloud
                    };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var responseText = ExtractResponseText(responseJson);

                _logger.LogInformation("Cloud AI response received ({Length} chars)", responseText.Length);

                TokenUsage? usage = null;
                string? cloudModel = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
                    usage = OpenAiUsageParser.ParseUsage(doc.RootElement);
                    cloudModel = OpenAiUsageParser.ReadModel(doc.RootElement);
                }
                catch { /* Best effort — Cloud's body shape varies; don't fail the call over missing usage. */ }

                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = responseText.Trim(),
                    ProviderType = AiProviderType.Cloud,
                    Usage = usage,
                    ModelUsed = cloudModel ?? model
                };
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("Cloud AI request timed out for model '{Model}'", model);
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Request to Cloud AI timed out after {GetTimeoutSeconds()}s.",
                    ProviderType = AiProviderType.Cloud
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Cannot connect to Cloud AI at {Endpoint}", endpoint);
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Cannot connect to Cloud AI at {endpoint}. Error: {ex.Message}",
                    ProviderType = AiProviderType.Cloud
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error calling Cloud AI");
                return new AiProviderResult
                {
                    Success = false,
                    Error = $"Unexpected error: {ex.Message}",
                    ProviderType = AiProviderType.Cloud
                };
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return Array.Empty<float>();

            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<float>();

            var model = ModelName;

            try
            {
                using var httpClient = CreateHttpClient(endpoint, apiKey);
                var requestPath = BuildRequestPath(isEmbedding: true);

                var requestBody = new { model = model, input = text };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(requestPath, content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cloud AI Embedding failed for model '{Model}' at path '{Path}': {Status}", 
                        model, requestPath, response.StatusCode);
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
                _logger.LogError(ex, "Cloud AI embedding call failed");
                return Array.Empty<float>();
            }
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
                return (false, "Cloud AI endpoint is not configured. Set it in Settings → AI Provider Configuration → Cloud AI tab.");

            if (string.IsNullOrWhiteSpace(GetApiKey()))
                return (false, "Cloud AI API key is not configured.");

            if (string.IsNullOrWhiteSpace(ModelName))
                return (false, "Cloud AI model name is not configured.");

            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var response = await httpClient.GetAsync(endpoint);
                return (true, null);
            }
            catch (HttpRequestException ex)
            {
                return (false, $"Cannot connect to Cloud AI at {endpoint}. Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Unexpected error validating Cloud AI configuration: {ex.Message}");
            }
        }

        private HttpClient CreateHttpClient(string endpoint, string apiKey)
        {
            // Ensure trailing slash for proper path concatenation
            var formattedEndpoint = endpoint.TrimEnd('/') + "/";
            
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(formattedEndpoint),
                Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds())
            };

            httpClient.DefaultRequestHeaders.Add("User-Agent", "SupportFlow-AI-Platform/2.0");

            if (GetUseBearerToken())
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            else
                httpClient.DefaultRequestHeaders.Add(GetAuthHeaderName(), apiKey);

            return httpClient;
        }

        private string BuildRequestPath(bool isEmbedding)
        {
            var deployment = GetDeploymentName();
            var action = isEmbedding ? "embeddings" : "chat/completions";

            if (!string.IsNullOrWhiteSpace(deployment))
            {
                var apiVersion = GetApiVersion();
                // Azure OpenAI pattern: openai/deployments/{deployment}/{action}?api-version={version}
                return $"openai/deployments/{deployment}/{action}?api-version={apiVersion}";
            }

            // Standard OpenAI-compatible pattern
            return action;
        }

        private string ExtractResponseText(string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                    return content.GetString() ?? "";

                if (firstChoice.TryGetProperty("text", out var text))
                    return text.GetString() ?? "";
            }

            if (root.TryGetProperty("response", out var resp))
                return resp.GetString() ?? "";

            if (root.TryGetProperty("output", out var output))
                return output.GetString() ?? "";

            _logger.LogWarning("Could not extract response text from Cloud AI response. Returning raw JSON.");
            return responseJson;
        }

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
        public async Task<List<ServiceOpsAI.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            var results = new List<ServiceOpsAI.Models.DTOs.AiModelDto>();

            var endpoint = GetEndpoint();
            var apiKey = GetApiKey();
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)) return results;

            // Azure OpenAI exposes user-deployed models under the "deployments" listing.
            // For other OpenAI-compatible endpoints, fall back to /models. We attempt both.
            try
            {
                using var http = CreateHttpClient(endpoint, apiKey);
                var apiVersion = GetApiVersion();
                var activeModel = ModelName;

                // Try Azure deployments first (only meaningful when api-version is set)
                bool gotAny = false;
                try
                {
                    var azureResp = await http.GetAsync($"openai/deployments?api-version={apiVersion}");
                    if (azureResp.IsSuccessStatusCode)
                    {
                        var azureJson = await azureResp.Content.ReadAsStringAsync();
                        using var azureDoc = JsonDocument.Parse(azureJson);
                        if (azureDoc.RootElement.TryGetProperty("data", out var azureArr))
                        {
                            foreach (var d in azureArr.EnumerateArray())
                            {
                                var depId = d.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                var modelId = d.TryGetProperty("model", out var modEl) ? modEl.GetString() : null;
                                var name = !string.IsNullOrWhiteSpace(depId) ? depId : modelId;
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                results.Add(new ServiceOpsAI.Models.DTOs.AiModelDto
                                {
                                    Name = name!,
                                    Family = modelId,
                                    IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                                });
                                gotAny = true;
                            }
                        }
                    }
                }
                catch { /* try the OpenAI-compatible path next */ }

                if (!gotAny)
                {
                    var modelsResp = await http.GetAsync("models");
                    if (modelsResp.IsSuccessStatusCode)
                    {
                        var modelsJson = await modelsResp.Content.ReadAsStringAsync();
                        using var modelsDoc = JsonDocument.Parse(modelsJson);
                        if (modelsDoc.RootElement.TryGetProperty("data", out var modelsArr))
                        {
                            foreach (var m in modelsArr.EnumerateArray())
                            {
                                var name = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                var owned = m.TryGetProperty("owned_by", out var o) ? o.GetString() : null;
                                results.Add(new ServiceOpsAI.Models.DTOs.AiModelDto
                                {
                                    Name = name!,
                                    Family = owned,
                                    IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list Cloud AI models");
            }

            return results.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

