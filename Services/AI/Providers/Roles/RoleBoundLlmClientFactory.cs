namespace ServiceOpsAI.Services.AI.Providers.Roles
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using ServiceOpsAI.Constants;
    using ServiceOpsAI.Data;
    using ServiceOpsAI.Enums;
    using AnalystAgent.Abstractions;
    using AnalystAgent.Configuration;

    /// <summary>
    /// Real implementation of <see cref="IRoleBoundLlmClientFactory"/>. For each role, returns
    /// an <see cref="ILlmClient"/> that resolves its model at call time from the
    /// <c>SystemSettings</c> table — so admins can change a role's model from the UI without
    /// a restart. When no role-specific model is set, the call transparently delegates to the
    /// default <see cref="ILlmClient"/> (which uses the Copilot workload model).
    ///
    /// <para><b>Why dynamic lookup per call:</b> SystemSettings is the runtime source of truth
    /// (admin edits via the AI settings page write here). Caching the model at construction
    /// would mean settings changes need a process restart — exactly what we want to avoid.</para>
    ///
    /// <para><b>Provider scope:</b> currently only Ollama supports model overrides via
    /// <c>GenerateAsync(prompt, modelOverride)</c>. For non-Ollama providers the role-specific
    /// model is recorded in DB (UI works) but the actual call falls through to the default
    /// — same compromise the existing <see cref="WorkloadAwareProvider"/> makes for the
    /// workload-level overrides.</para>
    /// </summary>
    internal sealed class RoleBoundLlmClientFactory : IRoleBoundLlmClientFactory
    {
        // Map role → (provider key, model key) for that role's overrides. Both are optional:
        // when both unset the call falls through to the default ILlmClient. When the provider
        // key is set, the factory resolves that provider and (for Ollama) calls it with the
        // model override. Keys missing from the map (Paraphraser / Frontier / SyntheticGenerator)
        // currently fall through to the default; add their settings keys here when activated.
        private static readonly IReadOnlyDictionary<AiRole, (string Provider, string Model)> RoleSettingsKeys =
            new Dictionary<AiRole, (string, string)>
            {
                [AiRole.Classifier]          = (SettingKeys.AiClassifierProvider,               SettingKeys.ClassifierWorkloadModel),
                [AiRole.QuerySpecComposer]   = (SettingKeys.AiCopilotProvider,                  SettingKeys.CopilotWorkloadModel),
                [AiRole.Decomposer]          = (SettingKeys.DecomposerRoleProvider,             SettingKeys.DecomposerRoleModel),
                [AiRole.Explainer]           = (SettingKeys.ExplainerRoleProvider,              SettingKeys.ExplainerRoleModel),
            };

        private readonly ILlmClient _defaultClient;
        private readonly IAiProviderFactory _providerFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly AiRoleBindings _bindings;
        private readonly AiProviderSettings _providerSettings;
        private readonly IOptionsMonitor<AnalystOptions> _analystOptions;
        private readonly ILogger<RoleBoundLlmClientFactory> _logger;
        private readonly Dictionary<AiRole, ILlmClient> _perRoleClients = new();
        private readonly object _gate = new();

        public RoleBoundLlmClientFactory(
            ILlmClient defaultClient,
            IAiProviderFactory providerFactory,
            IServiceProvider serviceProvider,
            IOptions<AiRoleBindings> bindings,
            IOptions<AiProviderSettings> providerSettings,
            IOptionsMonitor<AnalystOptions> analystOptions,
            ILogger<RoleBoundLlmClientFactory> logger)
        {
            _defaultClient = defaultClient;
            _providerFactory = providerFactory;
            _serviceProvider = serviceProvider;
            _bindings = bindings.Value;
            _providerSettings = providerSettings.Value;
            _analystOptions = analystOptions;
            _logger = logger;
        }

        public ILlmClient For(AiRole role)
        {
            // Per-process cache — one ILlmClient instance per role. The provider+model lookup
            // is dynamic (re-reads SystemSettings per call), so caching the client is safe.
            lock (_gate)
            {
                if (_perRoleClients.TryGetValue(role, out var existing)) return existing;
                ILlmClient client = RoleSettingsKeys.TryGetValue(role, out var keys)
                    ? new RoleBoundLlmClient(role, keys.Provider, keys.Model, _defaultClient,
                        _providerFactory, _serviceProvider, _analystOptions, _logger)
                    : _defaultClient;
                _perRoleClients[role] = client;
                return client;
            }
        }

        public AiRoleBinding GetEffectiveBinding(AiRole role)
        {
            var raw = _bindings.Get(role);
            var inheritedProvider = role switch
            {
                AiRole.Classifier => _providerSettings.ClassifierProvider,
                _ => _providerSettings.CopilotProvider,
            };
            string roleProvider = "";
            string roleModel = "";
            if (RoleSettingsKeys.TryGetValue(role, out var keys))
            {
                roleProvider = ReadDbSetting(_serviceProvider, keys.Provider) ?? "";
                roleModel = ReadDbSetting(_serviceProvider, keys.Model) ?? "";
            }
            return new AiRoleBinding
            {
                Provider = !string.IsNullOrWhiteSpace(raw.Provider) ? raw.Provider
                          : !string.IsNullOrWhiteSpace(roleProvider) ? roleProvider
                          : inheritedProvider,
                Model = !string.IsNullOrWhiteSpace(raw.Model) ? raw.Model : roleModel,
                Temperature = raw.Temperature,
                MaxTokens = raw.MaxTokens,
            };
        }

        // Helper used by both the factory (for diagnostics) and the per-role client (for runtime
        // lookups). Single source of truth for "read this key from SystemSettings".
        internal static string? ReadDbSetting(IServiceProvider sp, string key)
        {
            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.SystemSettings.FirstOrDefault(s => s.Key == key)?.Value;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// One role's <see cref="ILlmClient"/>. On each call:
    /// <list type="number">
    ///   <item>Reads the role-specific provider AND model from SystemSettings (lazy, per-call).</item>
    ///   <item>If BOTH unset, delegates to the default <see cref="ILlmClient"/> (Copilot workload).</item>
    ///   <item>If a provider is set, resolves it via <see cref="IAiProviderFactory.GetProvider"/>.</item>
    ///   <item>For Ollama: calls with model override directly. For other providers: their own
    ///         configured model is used (per-call model override is Ollama-only today, same
    ///         compromise as <see cref="WorkloadAwareProvider"/>).</item>
    /// </list>
    /// </summary>
    internal sealed class RoleBoundLlmClient : ILlmClient
    {
        private readonly AiRole _role;
        private readonly string _providerKey;
        private readonly string _modelKey;
        private readonly ILlmClient _fallback;
        private readonly IAiProviderFactory _providerFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptionsMonitor<AnalystOptions> _analystOptions;
        private readonly ILogger _logger;
        private bool _loggedNonOllamaWarning;

        public RoleBoundLlmClient(
            AiRole role,
            string providerKey,
            string modelKey,
            ILlmClient fallback,
            IAiProviderFactory providerFactory,
            IServiceProvider serviceProvider,
            IOptionsMonitor<AnalystOptions> analystOptions,
            ILogger logger)
        {
            _role = role;
            _providerKey = providerKey;
            _modelKey = modelKey;
            _fallback = fallback;
            _providerFactory = providerFactory;
            _serviceProvider = serviceProvider;
            _analystOptions = analystOptions;
            _logger = logger;
        }

        public Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => InvokeAsync(systemPrompt, userPrompt, jsonMode: true, sampling: null, cancellationToken);

        public Task<string> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => InvokeAsync(systemPrompt, userPrompt, jsonMode: false, sampling: null, cancellationToken);

        /// <summary>Text generation with optional per-call sampling overrides (self-consistency draws).
        /// Threads <paramref name="sampling"/> through the SAME role-resolution path; applied only on the
        /// Ollama branch (per-call model+sampling override is Ollama-only), ignored otherwise — same
        /// compromise as the role model override. Null sampling is byte-identical to the legacy overload.</summary>
        public Task<string> GenerateTextAsync(string systemPrompt, string userPrompt,
            AnalystAgent.Abstractions.LlmSamplingOptions? sampling, CancellationToken cancellationToken = default)
            => InvokeAsync(systemPrompt, userPrompt, jsonMode: false, sampling, cancellationToken);

        private async Task<string> InvokeAsync(string systemPrompt, string userPrompt, bool jsonMode,
            AnalystAgent.Abstractions.LlmSamplingOptions? sampling, CancellationToken ct)
        {
            var roleProviderName = RoleBoundLlmClientFactory.ReadDbSetting(_serviceProvider, _providerKey);
            var roleModel = RoleBoundLlmClientFactory.ReadDbSetting(_serviceProvider, _modelKey);

            if (string.IsNullOrWhiteSpace(roleProviderName) && string.IsNullOrWhiteSpace(roleModel))
            {
                // No overrides → use the default client (Copilot workload provider+model).
                // This is the normal path until the admin configures a per-role binding. Sampling is
                // forwarded to the default client's sampling overload (text only); null → legacy call.
                return jsonMode
                    ? await _fallback.GenerateJsonAsync(systemPrompt, userPrompt, ct)
                    : await _fallback.GenerateTextAsync(systemPrompt, userPrompt, sampling, ct);
            }

            // Resolve the provider: role's provider if set, otherwise fall back to Copilot workload.
            IAiProvider provider;
            if (!string.IsNullOrWhiteSpace(roleProviderName)
                && Enum.TryParse<AiProviderType>(roleProviderName, ignoreCase: true, out var providerType))
            {
                provider = _providerFactory.GetProvider(providerType);
            }
            else
            {
                provider = _providerFactory.GetProviderForWorkload(AiWorkloadType.Copilot);
            }

            // Unwrap WorkloadAwareProvider so we can check the underlying provider type for the
            // Ollama model-override path. For non-Ollama providers, the inner provider's own
            // configured model is what gets used (per-call model override is Ollama-only).
            var innerProvider = provider is WorkloadAwareProvider wrapper
                ? UnwrapInner(wrapper)
                : provider;

            var combinedPrompt = string.IsNullOrEmpty(systemPrompt)
                ? userPrompt
                : systemPrompt + "\n\n" + userPrompt;

            // TRACE COVERAGE: the role-override branch calls the provider DIRECTLY (it does NOT go
            // through HostAiProviderLlmClient), so without this the emitter / decomposer / explainer
            // calls show as calls=0 in the trace — we'd be tuning a prompt we cannot read. Record the
            // call into the per-question scope via the shared helper. Best-effort in finally; a trace
            // failure must never break SQL generation. The _fallback branch above already records.
            var stage = LlmCallStageHint.Current ?? _role.ToString();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AiProviderResult? result = null;
            string? error = null;
            try
            {
                if (innerProvider is OllamaAiProvider ollama)
                {
                    var modelToUse = string.IsNullOrWhiteSpace(roleModel) ? null : roleModel;
                    // Translate the AnalystAgent sampling record into the provider-layer DTO (null → null,
                    // byte-identical to the legacy request). JSON mode never carries sampling.
                    ServiceOpsAI.Services.AI.Providers.LlmSamplingOptions? providerSampling = (jsonMode || sampling is null)
                        ? null
                        : new ServiceOpsAI.Services.AI.Providers.LlmSamplingOptions(sampling.Temperature, sampling.Seed);
                    result = await ollama.GenerateAsync(combinedPrompt, modelOverride: modelToUse,
                        expectJson: jsonMode, sampling: providerSampling);
                    if (!result.Success)
                        throw new InvalidOperationException(
                            $"LLM call failed for role {_role} on model '{modelToUse ?? "(provider default)"}': {result.Error}");
                    return result.ResponseText ?? string.Empty;
                }

                // Non-Ollama provider — uses its own configured model. If the admin set a role-
                // specific model, log once that it's not applied here (matches WorkloadAwareProvider's
                // compromise). The role-specific PROVIDER selection still applies.
                if (!string.IsNullOrWhiteSpace(roleModel) && !_loggedNonOllamaWarning)
                {
                    _logger.LogWarning(
                        "[RoleBoundLlm] role={Role} has model '{Model}' set but provider {Provider} does not support per-call model override; using the provider's own configured model.",
                        _role, roleModel, innerProvider?.GetType().Name ?? "null");
                    _loggedNonOllamaWarning = true;
                }
                result = jsonMode && provider is WorkloadAwareProvider w
                    ? await w.GenerateJsonAsync(combinedPrompt)
                    : await provider.GenerateAsync(combinedPrompt);
                if (!result.Success)
                    throw new InvalidOperationException(
                        $"LLM call failed for role {_role}: {result.Error}");
                return result.ResponseText ?? string.Empty;
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
                throw;
            }
            finally
            {
                sw.Stop();
                try
                {
                    var opts = _analystOptions.CurrentValue;
                    LlmCallScope.Current?.Record(LlmTraceCapture.BuildRecord(
                        stage: stage,
                        provider: result?.ProviderType.ToString() ?? "unknown",
                        model: result?.ModelUsed,
                        usage: result?.Usage,
                        elapsedMs: sw.ElapsedMilliseconds,
                        success: result?.Success ?? false,
                        error: error,
                        prompt: combinedPrompt,
                        response: result?.ResponseText,
                        previewCap: opts.LlmTracePreviewMaxChars,
                        fullCap: opts.LlmTraceFullMaxChars));
                }
                catch { /* tracing must never break generation */ }
            }
        }

        // WorkloadAwareProvider stores _inner privately. Reflection is the cleanest workaround
        // without modifying that class. Used only to detect "is this Ollama?" for the model
        // override path.
        private static IAiProvider? UnwrapInner(WorkloadAwareProvider wrapper)
        {
            var field = typeof(WorkloadAwareProvider).GetField("_inner",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(wrapper) as IAiProvider;
        }
    }
}
