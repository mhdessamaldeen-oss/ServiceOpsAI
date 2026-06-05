using ServiceOpsAI.Enums;
using System.Text;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Linq;

namespace ServiceOpsAI.Services.AI.Providers
{
    public class OllamaAiProvider : IAiProvider
    {
        private readonly OllamaProviderOptions _configOptions;
        private readonly ILogger<OllamaAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        // Process-wide HttpClient. Re-creating `new HttpClient` per request (the prior pattern)
        // exhausts ephemeral TCP ports — 340 verified-query priming embeds × 2 min TIME_WAIT
        // decay = backpressure that stalls subsequent calls indefinitely. Sharing one instance
        // lets HttpClientHandler reuse the underlying socket pool to Ollama.
        // Timeout is left as InfiniteTimeSpan here and per-call enforcement happens via
        // CancellationTokenSource (HttpClient.Timeout cannot be changed after first request).
        private static readonly HttpClient _sharedHttp = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        public AiProviderType ProviderType => AiProviderType.Ollama;

        public OllamaAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<OllamaAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _configOptions = settings.Value.Ollama;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public string ModelName => GetDbSetting(SettingKeys.OllamaModelConfig) ?? _configOptions.Model;

        /// <summary>The Ollama model used for embeddings. Read from the dedicated
        /// <see cref="SettingKeys.OllamaEmbeddingModel"/> setting; falls back to the static
        /// config default (<c>bge-m3</c>); falls back to <see cref="ModelName"/> only if both
        /// the setting and the config default are empty (legacy single-model installs).
        ///
        /// <para>Why separate from <see cref="ModelName"/>: chat / classifier models (qwen2.5:7b
        /// etc.) produce poor embeddings — their internal vectors aren't trained as similarity
        /// representations. Sending the entity-matcher's signatures or the ticket-content
        /// embeddings to a chat model gives noisy cosines and false negatives in semantic
        /// search. Pre-fix audit (2026-05-07): all RAG-workload embedding calls were routed to
        /// the chat ModelName, masking the gap because Ollama's <c>/api/embeddings</c> endpoint
        /// silently accepts any model and returns SOMETHING.</para></summary>
        public string EmbeddingModelName
        {
            get
            {
                // From the AI settings: the dedicated embedding workload model (admin-editable),
                // then the appsettings default. NO hardcoded model — if neither is set, return empty so
                // the caller fails LOUD instead of silently embedding with the chat model (→ NaN).
                var fromDb = GetDbSetting(SettingKeys.OllamaEmbeddingModel);
                if (!string.IsNullOrWhiteSpace(fromDb)) return fromDb;
                if (!string.IsNullOrWhiteSpace(_configOptions.EmbeddingModel)) return _configOptions.EmbeddingModel;
                return string.Empty;
            }
        }

        /// <summary>Resolve the model name for a specific workload. The workload-routing UI exposes
        /// a Model field next to each Provider dropdown so the user can pick e.g.
        /// "Copilot → Ollama / qwen2.5:7b" AND "Rag → Ollama / bge-m3" simultaneously, all served
        /// by the same Ollama instance. When the workload-specific override is unset, falls back
        /// to <see cref="ModelName"/> for chat workloads and <see cref="EmbeddingModelName"/> for Rag.</summary>
        public string GetModelNameForWorkload(ServiceOpsAI.Enums.AiWorkloadType workload)
        {
            var key = workload switch
            {
                ServiceOpsAI.Enums.AiWorkloadType.Copilot    => SettingKeys.CopilotWorkloadModel,
                ServiceOpsAI.Enums.AiWorkloadType.Analysis   => SettingKeys.AnalysisWorkloadModel,
                ServiceOpsAI.Enums.AiWorkloadType.Rag        => SettingKeys.RagWorkloadModel,
                ServiceOpsAI.Enums.AiWorkloadType.Classifier => SettingKeys.ClassifierWorkloadModel,
                _ => null
            };
            if (key != null)
            {
                var fromDb = GetDbSetting(key);
                if (!string.IsNullOrWhiteSpace(fromDb)) return fromDb;
            }
            // Fallback cascade:
            //   Rag        → embedding-model default
            //   Classifier → Copilot's model (initial intent: one model in two roles)
            //   everything else → the chat model
            if (workload == ServiceOpsAI.Enums.AiWorkloadType.Rag)
                return EmbeddingModelName;
            if (workload == ServiceOpsAI.Enums.AiWorkloadType.Classifier)
            {
                var copilotModel = GetDbSetting(SettingKeys.CopilotWorkloadModel);
                if (!string.IsNullOrWhiteSpace(copilotModel)) return copilotModel;
            }
            return ModelName;
        }

        // Reads from the centralized SettingKeys constant (admin-editable) so the value
        // shown in the Settings UI is the one the provider actually uses. The legacy
        // hardcoded "Ai:Ollama:ContextWindow" key is no longer consulted.
        // Default raised 4096 → 8192: at 4096 a rich schema prompt + question could overflow num_ctx
        // and Ollama SILENTLY TRUNCATES the tail, dropping the question/schema → wrong/empty answers.
        // qwen2.5-coder:7b supports far larger windows; 8192 fits the query-scoped schema with room for
        // the response. Admin can raise further via the OllamaContextWindow setting.
        public int ContextCapacity => int.TryParse(GetDbSetting(SettingKeys.OllamaContextWindow), out var val) ? val : 8192;
        
        private string GetBaseUrl() => GetDbSetting(SettingKeys.OllamaBaseUrl) ?? _configOptions.BaseUrl;
        private int GetTimeoutSeconds() => int.TryParse(GetDbSetting(SettingKeys.OllamaTimeoutSeconds), out var value) ? value : _configOptions.TimeoutSeconds;

        /// <summary>Translate the configured timeout into an HttpClient.Timeout value. A
        /// non-positive setting (0 or negative) maps to <see cref="Timeout.InfiniteTimeSpan"/>
        /// so slow local models can run as long as needed (assessment scenario, big models on
        /// modest hardware). Positive values pass through as <see cref="TimeSpan.FromSeconds"/>.</summary>
        private TimeSpan GetHttpClientTimeout()
        {
            var seconds = GetTimeoutSeconds();
            return seconds <= 0 ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        }
        private int GetMaxPromptChars() => int.TryParse(GetDbSetting(SettingKeys.OllamaMaxPromptChars), out var value) ? value : _configOptions.MaxPromptChars;
        private double GetTemperature() => double.TryParse(GetDbSetting(SettingKeys.OllamaTemperature), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : _configOptions.Temperature;

        public Task<AiProviderResult> GenerateAsync(string prompt) => GenerateAsync(prompt, modelOverride: null, expectJson: false);

        /// <summary>Generate with an explicit model override. The workload-aware factory wrapper
        /// passes the resolved per-workload model in here so each call uses the right model
        /// without polluting the IAiProvider interface contract.</summary>
        public Task<AiProviderResult> GenerateAsync(string prompt, string? modelOverride)
            => GenerateAsync(prompt, modelOverride, expectJson: false);

        /// <summary>Generate with constrained JSON output. When <paramref name="expectJson"/>
        /// is true, Ollama's <c>format: "json"</c> parameter is set in the request body — the
        /// model is forced to emit a syntactically-valid JSON object. This drops a large class
        /// of LLM output failures (truncated braces, trailing commas, prose-prefixed JSON) at
        /// the model layer and lets us delete most of <c>JsonPlanParser</c>'s recovery code.
        ///
        /// <para>Used by the classifier and any path that expects a structured plan back. Free
        /// chat (general-support, knowledge questions) stays at <c>expectJson=false</c>.</para></summary>
        public Task<AiProviderResult> GenerateAsync(string prompt, string? modelOverride, bool expectJson)
            => GenerateAsync(prompt, modelOverride, expectJson, sampling: null);

        /// <summary>Generate with optional per-call sampling overrides (temperature / seed) for the
        /// self-consistency path. When <paramref name="sampling"/> is non-null, its <c>Temperature</c>
        /// and <c>Seed</c> override the request's <c>options</c> block; a null field falls back to the
        /// configured default (so a null <paramref name="sampling"/> is byte-identical to the legacy
        /// request body). <c>keep_alive</c> is UNCHANGED — still the configured
        /// <see cref="OllamaProviderOptions.ChatKeepAliveSeconds"/>.</summary>
        public async Task<AiProviderResult> GenerateAsync(string prompt, string? modelOverride, bool expectJson,
            ServiceOpsAI.Services.AI.Providers.LlmSamplingOptions? sampling)
        {
            var baseUrl = GetBaseUrl();
            var model = string.IsNullOrWhiteSpace(modelOverride) ? ModelName : modelOverride!;

            try
            {
                var safePrompt = TruncatePrompt(prompt, GetMaxPromptChars());
                _logger.LogInformation("Sending prompt to Ollama model '{Model}' at {BaseUrl}{JsonHint}",
                    model, baseUrl, expectJson ? " (format=json)" : "");

                var httpClient = _sharedHttp;
                var fullUrl = baseUrl.TrimEnd('/') + "/api/chat";
                using var callCts = new System.Threading.CancellationTokenSource(GetHttpClientTimeout());

                // Request body shape changes with `format: "json"`. We build it as a Dictionary
                // so we only include the `format` key when needed — Ollama rejects unknown empty
                // values on some versions.
                var requestBody = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new[]
                    {
                        new { role = "system", content = "You are a helpful support analyst." },
                        new { role = "user", content = safePrompt }
                    },
                    ["stream"] = false,
                    // keep_alive is a TOP-LEVEL Ollama parameter, NOT an option (nested under `options`
                    // Ollama silently ignores it → 5-min default → reload churn between slow calls).
                    // Default 1800s (30 min) keeps the model warm across a session while staying
                    // EVICTABLE — a Forever pin (-1) would block the embedding model from ever loading
                    // and deadlock schema-embedding generation on a single GPU. Tune via
                    // OllamaProviderOptions.ChatKeepAliveSeconds.
                    ["keep_alive"] = _configOptions.ChatKeepAliveSeconds,
                    // Sampling: a non-null override wins per-field; a null field (or null sampling)
                    // falls back to the configured default — so a null sampling reproduces the legacy
                    // request body EXACTLY (temperature = GetTemperature(), seed = 42). This is what the
                    // self-consistency path uses to draw diverse candidates (higher temp + distinct seed).
                    ["options"] = new
                    {
                        temperature = sampling?.Temperature ?? GetTemperature(),
                        num_ctx = ContextCapacity,
                        // Cap response length explicitly. Ollama defaults to 128 which truncates JSON outputs mid-field; configurable via OllamaProviderOptions.MaxOutputTokens (default 2048).
                        num_predict = _configOptions.MaxOutputTokens,
                        seed = sampling?.Seed ?? 42,
                        top_p = 1.0
                    }
                };
                if (expectJson)
                {
                    // Forces the model to emit a single valid JSON object. Supported by Ollama
                    // for most modern models (qwen2.5, llama3.x, mistral). The model still has
                    // to be PROMPTED to produce JSON — `format` only constrains the output shape,
                    // it doesn't tell the model what schema. Our prompt already includes the
                    // expected JSON schema; this just guarantees the result is parseable.
                    requestBody["format"] = "json";
                }

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(fullUrl, content, callCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    return new AiProviderResult { Success = false, Error = $"Ollama error {(int)response.StatusCode}: {errorBody}", ProviderType = AiProviderType.Ollama };
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                var responseText = root.GetProperty("message").GetProperty("content").GetString() ?? "";

                // Ollama surfaces token counts at the response root, not nested under "usage".
                // prompt_eval_count = input tokens after Ollama tokenises the prompt.
                // eval_count = output tokens it generated. Both are int but may be absent on
                // some chat-streaming completions, hence the TryGet pattern.
                TokenUsage? usage = null;
                int promptToks = root.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt32(out var p) ? p : 0;
                int completionToks = root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var c) ? c : 0;
                if (promptToks > 0 || completionToks > 0)
                    usage = new TokenUsage { Prompt = promptToks, Completion = completionToks, Total = promptToks + completionToks };
                var modelUsed = root.TryGetProperty("model", out var m) ? m.GetString() : null;

                return new AiProviderResult { Success = true, ResponseText = responseText.Trim(), ProviderType = AiProviderType.Ollama, Usage = usage, ModelUsed = modelUsed };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ollama call failed");
                return new AiProviderResult { Success = false, Error = ex.Message, ProviderType = AiProviderType.Ollama };
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
                var response = await http.GetAsync("api/tags");
                if (response.IsSuccessStatusCode) return (true, null);
                return (false, $"Ollama returned HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) { return (false, $"Cannot connect to Ollama: {ex.Message}"); }
        }

        public Task<float[]> GetEmbeddingAsync(string text) => GetEmbeddingAsync(text, modelOverride: null);

        /// <summary>Generate an embedding with an explicit model override (for workload-bound calls).</summary>
        public async Task<float[]> GetEmbeddingAsync(string text, string? modelOverride)
        {
            var baseUrl = GetBaseUrl();
            // Use the dedicated embedding model — see EmbeddingModelName property doc-comment for
            // why this is separate from the chat ModelName. modelOverride wins when provided
            // (workload-bound wrapper passes the per-workload Rag model here).
            var model = string.IsNullOrWhiteSpace(modelOverride) ? EmbeddingModelName : modelOverride!;
            // GUARD: never embed with the chat/generator model — it returns NaN/garbage (its hidden states
            // aren't similarity vectors) and silently darkens the retriever. If the override/cascade resolves
            // to the chat model, use the CONFIGURED embedding setting; if none is configured, fail LOUD so
            // the operator sets the Embedding/Rag model in AI settings (no hardcoded substitution).
            if (string.IsNullOrWhiteSpace(model) || string.Equals(model, ModelName, StringComparison.OrdinalIgnoreCase))
            {
                var configured = EmbeddingModelName;
                if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, ModelName, StringComparison.OrdinalIgnoreCase))
                    model = configured;
                else
                {
                    _logger.LogWarning("[Ollama] No embedding model configured (resolved to the chat model '{Chat}'). Set the Embedding/Rag model in AI settings — skipping embed to avoid NaN.", ModelName);
                    return Array.Empty<float>();
                }
            }

            try
            {
                var httpClient = _sharedHttp;
                var fullUrl = baseUrl.TrimEnd('/') + "/api/embeddings";
                using var callCts = new System.Threading.CancellationTokenSource(GetHttpClientTimeout());

                // keep_alive: 60s (NOT -1). The chat model (qwen) is pinned (-1) because it runs on every
                // request; the embedder runs only at warmup + the rare no-anchor question (the SchemaLinker
                // does similarity lexically most of the time). Pinning BOTH oversubscribes VRAM — Ollama then
                // evicts qwen and reloads it cold every generation (the slowness). Let bge-m3 unload so qwen
                // keeps the GPU.
                var requestBody = new { model = model, prompt = text, keep_alive = 60 };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Ollama's embedding endpoint is /api/embeddings — without the api/ prefix it 404s.
                // (The chat call above uses "api/chat"; the bug here was the missing prefix.)
                var response = await httpClient.PostAsync(fullUrl, content, callCts.Token);
                if (!response.IsSuccessStatusCode) 
                {
                    var debug = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Ollama Embedding failed: {Status} {Debug}", response.StatusCode, debug);
                    return Array.Empty<float>();
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                
                var data = doc.RootElement.GetProperty("embedding");
                var embedding = new float[data.GetArrayLength()];
                for (int i = 0; i < embedding.Length; i++)
                {
                    embedding[i] = (float)data[i].GetDouble();
                    if (float.IsNaN(embedding[i]) || float.IsInfinity(embedding[i]))
                    {
                        _logger.LogWarning("[Ollama] embedding from '{Model}' contained NaN/Inf — discarding (use a real embedding model like bge-m3).", model);
                        return Array.Empty<float>();
                    }
                }

                return embedding;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ollama embedding call failed");
                return Array.Empty<float>();
            }
        }

        public async Task<List<ServiceOpsAI.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            var results = new List<ServiceOpsAI.Models.DTOs.AiModelDto>();
            try
            {
                var baseUrl = GetBaseUrl().TrimEnd('/') + "/";
                using var http = new HttpClient { 
                    BaseAddress = new Uri(baseUrl),
                    Timeout = TimeSpan.FromSeconds(5) 
                };
                var response = await http.GetAsync("api/tags");
                if (!response.IsSuccessStatusCode) return results;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    var activeModel = ModelName;
                    var tasks = modelsArray.EnumerateArray().Select(async m => {
                        var name = m.GetProperty("name").GetString() ?? "";
                        var sizeBytes = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        var sizeStr = sizeBytes > 0 ? (sizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB" : "N/A";
                        
                        var details = m.TryGetProperty("details", out var d) ? d : (JsonElement?)null;
                        var family = details?.TryGetProperty("family", out var f) == true ? f.GetString() : "";
                        var paramSize = details?.TryGetProperty("parameter_size", out var ps) == true ? ps.GetString() : "";
                        var quant = details?.TryGetProperty("quantization_level", out var q) == true ? q.GetString() : "";

                        string ctxWindow = "";
                        try {
                            var showResp = await http.PostAsJsonAsync("api/show", new { name });
                            if (showResp.IsSuccessStatusCode) {
                                var showJson = await showResp.Content.ReadAsStringAsync();
                                using var showDoc = JsonDocument.Parse(showJson);
                                if (showDoc.RootElement.TryGetProperty("parameters", out var p)) {
                                    var lines = p.GetString()?.Split('\n');
                                    var ctxLine = lines?.FirstOrDefault(l => l.Contains("num_ctx"));
                                    if (ctxLine != null) {
                                        ctxWindow = ctxLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                                        if (int.TryParse(ctxWindow, out int c)) {
                                            ctxWindow = c >= 1024 ? (c / 1024) + "k" : c.ToString();
                                        }
                                    }
                                }
                            }
                        } catch { /* ignore show errors */ }

                        return new ServiceOpsAI.Models.DTOs.AiModelDto
                        {
                            Name = name,
                            Size = sizeStr,
                            Family = family,
                            ParameterSize = paramSize,
                            Quantization = quant,
                            ContextWindow = ctxWindow,
                            IsActive = name == activeModel
                        };
                    });
                    
                    results.AddRange(await Task.WhenAll(tasks));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Ollama models");
            }
            return results;
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
    }
}
