using ServiceOpsAI.Enums;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// Resolves the correct AI provider based on DB settings (from UI) with fallback to appsettings.json.
    /// </summary>
    public class AiProviderFactory : IAiProviderFactory
    {
        private readonly AiProviderSettings _settings;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AiProviderFactory> _logger;

        private readonly Lazy<DockerModelAiProvider> _dockerProvider;
        private readonly Lazy<OpenAiProvider> _openAiProvider;
        private readonly Lazy<CloudAiProvider> _cloudProvider;
        private readonly Lazy<LocalAiProvider> _localAiProvider;
        private readonly Lazy<GeminiAiProvider> _geminiProvider;
        private readonly Lazy<OllamaAiProvider> _ollamaProvider;
        private readonly Lazy<GroqAiProvider> _groqProvider;

        public AiProviderFactory(
            IOptions<AiProviderSettings> settings,
            IServiceProvider serviceProvider,
            ILogger<AiProviderFactory> logger)
        {
            _settings = settings.Value;
            _serviceProvider = serviceProvider;
            _logger = logger;

            _dockerProvider = new Lazy<DockerModelAiProvider>(() =>
                ActivatorUtilities.CreateInstance<DockerModelAiProvider>(_serviceProvider));

            _openAiProvider = new Lazy<OpenAiProvider>(() =>
                ActivatorUtilities.CreateInstance<OpenAiProvider>(_serviceProvider));

            _cloudProvider = new Lazy<CloudAiProvider>(() =>
                ActivatorUtilities.CreateInstance<CloudAiProvider>(_serviceProvider));

            _localAiProvider = new Lazy<LocalAiProvider>(() =>
                ActivatorUtilities.CreateInstance<LocalAiProvider>(_serviceProvider));

            _geminiProvider = new Lazy<GeminiAiProvider>(() =>
                ActivatorUtilities.CreateInstance<GeminiAiProvider>(_serviceProvider));

            _ollamaProvider = new Lazy<OllamaAiProvider>(() =>
                ActivatorUtilities.CreateInstance<OllamaAiProvider>(_serviceProvider));

            _groqProvider = new Lazy<GroqAiProvider>(() =>
                ActivatorUtilities.CreateInstance<GroqAiProvider>(_serviceProvider));
        }

        public AiProviderType ActiveProviderType => ResolveActiveProviderType();

        public IAiProvider GetActiveProvider()
        {
            var providerType = ResolveActiveProviderType();
            return GetProvider(providerType);
        }

        public IAiProvider GetProvider(AiProviderType providerType)
        {
            return providerType switch
            {
                AiProviderType.DockerLocal => _dockerProvider.Value,
                AiProviderType.OpenAI => _openAiProvider.Value,
                AiProviderType.Cloud => _cloudProvider.Value,
                AiProviderType.LocalAI => _localAiProvider.Value,
                AiProviderType.Gemini => _geminiProvider.Value,
                AiProviderType.Ollama => _ollamaProvider.Value,
                AiProviderType.Groq => _groqProvider.Value,
                _ => throw new ArgumentException($"Unsupported AI provider type: {providerType}")
            };
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateActiveProviderAsync()
        {
            try
            {
                var provider = GetActiveProvider();
                return await provider.ValidateConfigurationAsync();
            }
            catch (Exception ex)
            {
                return (false, $"Failed to initialize {ActiveProviderType} provider: {ex.Message}");
            }
        }

        public IAiProvider GetProviderForWorkload(AiWorkloadType workload)
        {
            var providerType = ResolveProviderTypeForWorkload(workload);
            var provider = GetProvider(providerType);
            // Wrap in a workload-aware shim so per-workload model overrides apply transparently.
            // For non-Ollama providers the shim's ModelName falls through to the inner provider's
            // default, so behavior for OpenAI/Gemini/Groq/Cloud is unchanged.
            return new WorkloadAwareProvider(provider, workload);
        }

        private AiProviderType ResolveProviderTypeForWorkload(AiWorkloadType workload)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                
                string? settingKey = workload switch
                {
                    AiWorkloadType.Analysis => SettingKeys.AiAnalysisProvider,
                    AiWorkloadType.Rag => SettingKeys.AiRagProvider,
                    AiWorkloadType.Copilot => SettingKeys.AiCopilotProvider,
                    AiWorkloadType.Classifier => SettingKeys.AiClassifierProvider,
                    _ => null
                };

                if (settingKey != null)
                {
                    var workloadSetting = db.SystemSettings.FirstOrDefault(s => s.Key == settingKey);
                    if (workloadSetting != null && !string.IsNullOrWhiteSpace(workloadSetting.Value))
                    {
                        return ParseProviderType(workloadSetting.Value.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read {Workload} provider setting from DB.", workload);
            }

            // Fallback to appsettings.json if DB has no value
            string? fallbackConfig = workload switch
            {
                AiWorkloadType.Analysis => _settings.AnalysisProvider,
                AiWorkloadType.Rag => _settings.RagProvider,
                AiWorkloadType.Copilot => _settings.CopilotProvider,
                // Classifier defaults to Copilot's provider when unset — same model serves
                // both jobs initially; can be split via UI once a smaller router is preferred.
                AiWorkloadType.Classifier => string.IsNullOrWhiteSpace(_settings.ClassifierProvider)
                    ? _settings.CopilotProvider
                    : _settings.ClassifierProvider,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(fallbackConfig))
            {
                return ParseProviderType(fallbackConfig);
            }

            return ResolveActiveProviderType(); // Ultimate fallback
        }

        private AiProviderType ResolveActiveProviderType()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var dbSetting = db.SystemSettings.FirstOrDefault(s => s.Key == SettingKeys.AiActiveProvider);
                if (dbSetting != null && !string.IsNullOrWhiteSpace(dbSetting.Value))
                {
                    return ParseProviderType(dbSetting.Value.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read AiActiveProvider from DB.");
            }

            return _settings.GetActiveProviderType();
        }

        private AiProviderType ParseProviderType(string value)
        {
            return value switch
            {
                AiProviderNames.DockerLocal or AiProviderNames.LegacyDockerLocalAlias => AiProviderType.DockerLocal,
                "OpenAI" or "GPT" => AiProviderType.OpenAI,
                "Cloud" => AiProviderType.Cloud,
                "LocalAI" => AiProviderType.LocalAI,
                "Gemini" => AiProviderType.Gemini,
                AiProviderNames.Ollama => AiProviderType.Ollama,
                AiProviderNames.Groq => AiProviderType.Groq,
                _ => _settings.GetActiveProviderType()
            };
        }
    }
}


