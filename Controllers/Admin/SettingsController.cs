using ServiceOpsAI.Data;
using ServiceOpsAI.Enums;
using ServiceOpsAI.Models;
using ServiceOpsAI.Services.AI.Providers;
using ServiceOpsAI.Services.AI.Providers.KeyPool;
using ServiceOpsAI.Services.Infrastructure;
using ServiceOpsAI.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Diagnostics;

using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI;
using ServiceOpsAI.Services.AI.Copilot.Tools;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;
using AutoMapper;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

namespace ServiceOpsAI.Controllers.Admin
{
    [Authorize(Roles = RoleNames.Admin)]
    public class SettingsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly IAiProviderFactory _providerFactory;
        private readonly AiProviderSettings _providerSettings;
        private readonly IDockerService _dockerService;
        private readonly IServiceProvider _serviceProvider;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IRuntimeDatabaseTargetService _runtimeDatabaseTargetService;
        private readonly IMapper _mapper;
        private const string SqlServerCandidatesTempDataKey = "SqlServerCandidates";
        private const string SqlServerProbeTempDataKey = "SqlServerProbe";
        private const string SqlServerLastServerNameTempDataKey = "SqlServerLastServerName";
        private const string SqlServerLastUseSqlAuthTempDataKey = "SqlServerLastUseSqlAuth";
        private const string SqlServerLastUserNameTempDataKey = "SqlServerLastUserName";

        private readonly CopilotToolRegistry _toolRegistry;
        private readonly IGeminiKeyPool _geminiKeyPool;
        private readonly IGroqKeyPool _groqKeyPool;
        private readonly CopilotOptions _copilotOptions;
        private readonly ILogger<SettingsController> _logger;

        public SettingsController(
            ApplicationDbContext context,
            IConfiguration config,
            IAiProviderFactory providerFactory,
            IOptions<AiProviderSettings> providerSettings,
            IDockerService dockerService,
            IServiceProvider serviceProvider,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IRuntimeDatabaseTargetService runtimeDatabaseTargetService,
            CopilotToolRegistry toolRegistry,
            IGeminiKeyPool geminiKeyPool,
            IGroqKeyPool groqKeyPool,
            IOptions<CopilotOptions> copilotOptions,
            ILogger<SettingsController> logger,
            IMapper mapper)
        {
            _context = context;
            _config = config;
            _providerFactory = providerFactory;
            _providerSettings = providerSettings.Value;
            _dockerService = dockerService;
            _serviceProvider = serviceProvider;
            _userManager = userManager;
            _roleManager = roleManager;
            _runtimeDatabaseTargetService = runtimeDatabaseTargetService;
            _toolRegistry = toolRegistry;
            _geminiKeyPool = geminiKeyPool;
            _groqKeyPool = groqKeyPool;
            _copilotOptions = copilotOptions.Value;
            _logger = logger;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index(string tab = "themes")
        {
            var themes = await _context.CustomThemes.OrderBy(t => t.IsSystemTheme).ThenBy(t => t.Id).ToListAsync();
            var themeSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DefaultTheme);
            
            ViewBag.CurrentThemeId = themeSetting?.Value ?? "1";
            ViewBag.DockerBaseUrl = _providerSettings.DockerLocal.BaseUrl;
            ViewBag.ActiveTab = tab;

            // Copilot Tools
            ViewBag.CopilotTools = await _toolRegistry.GetAllToolsAsync();

            // Provider configuration for the AI tab
            ViewBag.ActiveProvider = _providerSettings.ActiveProvider;

            // Provider configuration for the AI tab
            ViewBag.ActiveProvider = _providerSettings.ActiveProvider;
            ViewBag.ActiveProviderType = _providerSettings.GetActiveProviderType();
            ViewBag.DockerSettings = _providerSettings.DockerLocal;
            ViewBag.OpenAiSettings = _providerSettings.OpenAI;
            ViewBag.CloudSettings = _providerSettings.Cloud;
            ViewBag.LocalAiSettings = _providerSettings.LocalAI;

            // Read DB-stored overrides
            var dbProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiActiveProvider);
            var dbDockerBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerBaseUrl);
            var dbDockerModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerModel);
            var dbDockerTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerTimeoutSeconds);
            var dbDockerMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerMaxPromptChars);
            var dbDockerMaxPromptTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerMaxPromptTokens);
            var dbDockerTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerTemperature);
            var dbDockerNumCtx = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerNumCtx);
            var dbDockerNumPredict = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerNumPredict);

            var dbOpenAiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiApiKey);
            var dbOpenAiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiModel);
            var dbOpenAiBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiBaseUrl);
            var dbOpenAiTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiTimeoutSeconds);
            var dbOpenAiMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiMaxPromptChars);
            var dbOpenAiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiTemperature);
            var dbOpenAiMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiMaxTokens);
            var dbOpenAiOrganizationId = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiOrganizationId);
            var dbOpenAiProjectId = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiProjectId);
            var dbCloudEndpoint = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudEndpoint);
            var dbCloudApiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudApiKey);
            var dbCloudModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudModel);
            var dbCloudDeployment = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudDeploymentName);
            var dbCloudTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudTimeoutSeconds);
            var dbCloudMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudMaxPromptChars);
            var dbCloudTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudTemperature);
            var dbCloudMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudMaxTokens);
            var dbCloudApiVersion = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudApiVersion);
            var dbCloudAuthHeaderName = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudAuthHeaderName);
            var dbCloudUseBearerToken = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudUseBearerToken);
            var dbLocalAiBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiBaseUrl);
            var dbLocalAiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiModel);
            var dbLocalAiApiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiApiKey);
            var dbLocalAiTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiTimeoutSeconds);
            var dbLocalAiMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiMaxPromptChars);
            var dbLocalAiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiTemperature);
            var dbGeminiKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiApiKey);
            var dbGeminiModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiModel);
            var dbGeminiTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiTemperature);
            var dbGeminiMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiMaxTokens);
            var dbGeminiRetryOn429 = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiRetryOn429);
            var dbGeminiRetryDelayMs = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiRetryDelayMs);
            var dbGeminiRetryMaxAttempts = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiRetryMaxAttempts);
            var dbGeminiAssessmentDelayMs = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiAssessmentDelayMs);

            // Workload Providers + per-workload model overrides
            var dbRagProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiRagProvider);
            var dbAnalysisProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiAnalysisProvider);
            var dbCopilotProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiCopilotProvider);
            var dbClassifierProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiClassifierProvider);
            var dbAnalysisModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AnalysisWorkloadModel);
            var dbRagModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.RagWorkloadModel);
            var dbCopilotModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CopilotWorkloadModel);
            var dbClassifierModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.ClassifierWorkloadModel);
            // Per-role provider + model overrides (each stage of the SuperAdminCopilot pipeline
            // can pick its own provider + model — see RoleBoundLlmClientFactory). Empty = inherit
            // CopilotProvider / CopilotWorkloadModel pair.
            var dbDecomposerProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DecomposerRoleProvider);
            var dbDecomposerModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DecomposerRoleModel);
            var dbSchemaLinkerProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.SchemaLinkerRoleProvider);
            var dbSchemaLinkerModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.SchemaLinkerRoleModel);
            var dbStructuralCueParserProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.StructuralCueParserRoleProvider);
            var dbStructuralCueParserModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.StructuralCueParserRoleModel);
            var dbSelfCorrectorProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.SelfCorrectorRoleProvider);
            var dbSelfCorrectorModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.SelfCorrectorRoleModel);
            var dbExplainerProvider = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.ExplainerRoleProvider);
            var dbExplainerModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.ExplainerRoleModel);

            // Ollama Config
            var dbOllamaBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaBaseUrl);
            var dbOllamaModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaModelConfig);
            var dbOllamaEmbeddingModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaEmbeddingModel);
            var dbOllamaTimeoutSeconds = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaTimeoutSeconds);
            var dbOllamaMaxPromptChars = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaMaxPromptChars);
            var dbOllamaContextWindow = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaContextWindow);
            var dbOllamaTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OllamaTemperature);

            ViewBag.DbRagProvider = dbRagProvider?.Value ?? _providerSettings.RagProvider;
            ViewBag.DbAnalysisProvider = dbAnalysisProvider?.Value ?? _providerSettings.AnalysisProvider;
            ViewBag.DbCopilotProvider = dbCopilotProvider?.Value ?? _providerSettings.CopilotProvider;
            ViewBag.DbClassifierProvider = dbClassifierProvider?.Value ?? _providerSettings.ClassifierProvider;
            ViewBag.DbAnalysisModel = dbAnalysisModel?.Value ?? "";
            ViewBag.DbRagModel = dbRagModel?.Value ?? "";
            ViewBag.DbCopilotModel = dbCopilotModel?.Value ?? "";
            ViewBag.DbClassifierModel = dbClassifierModel?.Value ?? "";
            ViewBag.DbDecomposerProvider = dbDecomposerProvider?.Value ?? "";
            ViewBag.DbDecomposerModel = dbDecomposerModel?.Value ?? "";
            ViewBag.DbSchemaLinkerProvider = dbSchemaLinkerProvider?.Value ?? "";
            ViewBag.DbSchemaLinkerModel = dbSchemaLinkerModel?.Value ?? "";
            ViewBag.DbStructuralCueParserProvider = dbStructuralCueParserProvider?.Value ?? "";
            ViewBag.DbStructuralCueParserModel = dbStructuralCueParserModel?.Value ?? "";
            ViewBag.DbSelfCorrectorProvider = dbSelfCorrectorProvider?.Value ?? "";
            ViewBag.DbSelfCorrectorModel = dbSelfCorrectorModel?.Value ?? "";
            ViewBag.DbExplainerProvider = dbExplainerProvider?.Value ?? "";
            ViewBag.DbExplainerModel = dbExplainerModel?.Value ?? "";

            ViewBag.DbOllamaBaseUrl = dbOllamaBaseUrl?.Value ?? _providerSettings.Ollama.BaseUrl;
            ViewBag.DbOllamaModel = dbOllamaModel?.Value ?? _providerSettings.Ollama.Model;
            ViewBag.DbOllamaEmbeddingModel = dbOllamaEmbeddingModel?.Value ?? _providerSettings.Ollama.EmbeddingModel;
            ViewBag.DbOllamaTimeoutSeconds = dbOllamaTimeoutSeconds?.Value ?? _providerSettings.Ollama.TimeoutSeconds.ToString();
            ViewBag.DbOllamaMaxPromptChars = dbOllamaMaxPromptChars?.Value ?? _providerSettings.Ollama.MaxPromptChars.ToString();
            ViewBag.DbOllamaContextWindow = dbOllamaContextWindow?.Value ?? "4096";
            ViewBag.DbOllamaTemperature = dbOllamaTemperature?.Value ?? _providerSettings.Ollama.Temperature.ToString("0.##");

            ViewBag.CopilotTableExposureMode = await GetSystemSettingAsync(SettingKeys.CopilotTableExposureMode) ?? _copilotOptions.TableExposureMode.ToString();
            ViewBag.CopilotBlockedTables = await GetSystemSettingAsync(SettingKeys.CopilotBlockedTables) ?? string.Join(Environment.NewLine, _copilotOptions.BlockedTables);
            ViewBag.CopilotBlockedTablePatterns = await GetSystemSettingAsync(SettingKeys.CopilotBlockedTablePatterns) ?? string.Join(Environment.NewLine, _copilotOptions.BlockedTablePatterns);
            ViewBag.CopilotBlockedColumns = await GetSystemSettingAsync(SettingKeys.CopilotBlockedColumns) ?? string.Join(Environment.NewLine, _copilotOptions.BlockedColumns);
            ViewBag.CopilotSensitiveColumns = await GetSystemSettingAsync(SettingKeys.CopilotSensitiveColumns) ?? string.Join(Environment.NewLine, _copilotOptions.SensitiveColumns);
            ViewBag.CopilotRetrieverTopK = await GetSystemSettingAsync(SettingKeys.CopilotRetrieverTopK) ?? _copilotOptions.RetrieverTopK.ToString();
            ViewBag.CopilotSchemaPromptStrategy = await GetSystemSettingAsync(SettingKeys.CopilotSchemaPromptStrategy) ?? _copilotOptions.SchemaPromptStrategy.ToString();
            ViewBag.CopilotUseVectorRetriever = await GetSystemSettingAsync(SettingKeys.CopilotUseVectorRetriever) ?? _copilotOptions.UseVectorRetriever.ToString();
            ViewBag.CopilotUsePastQuestionRag = await GetSystemSettingAsync(SettingKeys.CopilotUsePastQuestionRag) ?? _copilotOptions.UsePastQuestionRag.ToString();
            ViewBag.CopilotFewShotTopK = await GetSystemSettingAsync(SettingKeys.CopilotFewShotTopK) ?? _copilotOptions.FewShotTopK.ToString();
            ViewBag.CopilotUseLlmExplainer = await GetSystemSettingAsync(SettingKeys.CopilotUseLlmExplainer) ?? _copilotOptions.UseLlmExplainer.ToString();
            ViewBag.CopilotMaxLlmCallsPerQuestion = await GetSystemSettingAsync(SettingKeys.CopilotMaxLlmCallsPerQuestion) ?? _copilotOptions.MaxLlmCallsPerQuestion.ToString();
            ViewBag.CopilotMaxSelfCorrectionRetries = await GetSystemSettingAsync(SettingKeys.CopilotMaxSelfCorrectionRetries) ?? _copilotOptions.MaxSelfCorrectionRetries.ToString();
            ViewBag.CopilotLlmCallTimeoutSeconds = await GetSystemSettingAsync(SettingKeys.CopilotLlmCallTimeoutSeconds) ?? _copilotOptions.LlmCallTimeoutSeconds.ToString();
            ViewBag.CopilotMaxQuestionWallClockSeconds = await GetSystemSettingAsync(SettingKeys.CopilotMaxQuestionWallClockSeconds) ?? _copilotOptions.MaxQuestionWallClockSeconds.ToString();
            ViewBag.CopilotMaxRows = await GetSystemSettingAsync(SettingKeys.CopilotMaxRows) ?? _copilotOptions.MaxRows.ToString();
            ViewBag.CopilotCommandTimeoutSeconds = await GetSystemSettingAsync(SettingKeys.CopilotCommandTimeoutSeconds) ?? _copilotOptions.CommandTimeoutSeconds.ToString();
            ViewBag.CopilotEnableSchemaIntrospection = await GetSystemSettingAsync(SettingKeys.CopilotEnableSchemaIntrospection) ?? _copilotOptions.EnableSchemaIntrospection.ToString();
            ViewBag.CopilotRestrictMetadataToConfiguredEntities = await GetSystemSettingAsync(SettingKeys.CopilotRestrictMetadataToConfiguredEntities) ?? _copilotOptions.RestrictMetadataToConfiguredEntities.ToString();
            ViewBag.CopilotEnableResultCache = await GetSystemSettingAsync(SettingKeys.CopilotEnableResultCache) ?? _copilotOptions.EnableResultCache.ToString();
            ViewBag.CopilotResultCacheTtlSeconds = await GetSystemSettingAsync(SettingKeys.CopilotResultCacheTtlSeconds) ?? _copilotOptions.ResultCacheTtlSeconds.ToString();
            ViewBag.CopilotEnableCostGate = await GetSystemSettingAsync(SettingKeys.CopilotEnableCostGate) ?? _copilotOptions.EnableCostGate.ToString();
            ViewBag.CopilotMaxEstimatedQueryCost = await GetSystemSettingAsync(SettingKeys.CopilotMaxEstimatedQueryCost) ?? _copilotOptions.MaxEstimatedQueryCost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ViewBag.CopilotAmbiguityClarificationThreshold = await GetSystemSettingAsync(SettingKeys.CopilotAmbiguityClarificationThreshold) ?? _copilotOptions.AmbiguityClarificationThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ViewBag.CopilotResolverMinConfidence = await GetSystemSettingAsync(SettingKeys.CopilotResolverMinConfidence) ?? _copilotOptions.ResolverMinConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture);

            ViewBag.DbActiveProvider = _providerFactory.ActiveProviderType;
            ViewBag.DbDockerBaseUrl = dbDockerBaseUrl?.Value ?? _providerSettings.DockerLocal.BaseUrl;
            ViewBag.DbDockerModel = dbDockerModel?.Value ?? _providerSettings.DockerLocal.Model;
            ViewBag.DbDockerTimeoutSeconds = dbDockerTimeoutSeconds?.Value ?? _providerSettings.DockerLocal.TimeoutSeconds.ToString();
            ViewBag.DbDockerMaxPromptChars = dbDockerMaxPromptChars?.Value ?? _providerSettings.DockerLocal.MaxPromptChars.ToString();
            ViewBag.DbDockerMaxPromptTokens = dbDockerMaxPromptTokens?.Value ?? _providerSettings.DockerLocal.MaxPromptTokens.ToString();
            ViewBag.DbDockerTemperature = dbDockerTemperature?.Value ?? _providerSettings.DockerLocal.Temperature.ToString("0.##");
            ViewBag.DbDockerNumCtx = dbDockerNumCtx?.Value ?? _providerSettings.DockerLocal.NumCtx.ToString();
            ViewBag.DbDockerNumPredict = dbDockerNumPredict?.Value ?? _providerSettings.DockerLocal.NumPredict.ToString();

            ViewBag.DbOpenAiKey = dbOpenAiKey?.Value ?? "";
            ViewBag.DbOpenAiModel = dbOpenAiModel?.Value ?? _providerSettings.OpenAI.Model;
            ViewBag.DbOpenAiBaseUrl = dbOpenAiBaseUrl?.Value ?? _providerSettings.OpenAI.BaseUrl;
            ViewBag.DbOpenAiTimeoutSeconds = dbOpenAiTimeoutSeconds?.Value ?? _providerSettings.OpenAI.TimeoutSeconds.ToString();
            ViewBag.DbOpenAiMaxPromptChars = dbOpenAiMaxPromptChars?.Value ?? _providerSettings.OpenAI.MaxPromptChars.ToString();
            ViewBag.DbOpenAiTemperature = dbOpenAiTemperature?.Value ?? _providerSettings.OpenAI.Temperature.ToString("0.##");
            ViewBag.DbOpenAiMaxTokens = dbOpenAiMaxTokens?.Value ?? _providerSettings.OpenAI.MaxTokens.ToString();
            ViewBag.DbOpenAiOrganizationId = dbOpenAiOrganizationId?.Value ?? _providerSettings.OpenAI.OrganizationId ?? "";
            ViewBag.DbOpenAiProjectId = dbOpenAiProjectId?.Value ?? _providerSettings.OpenAI.ProjectId ?? "";
            ViewBag.DbCloudEndpoint = dbCloudEndpoint?.Value ?? _providerSettings.Cloud.Endpoint;
            ViewBag.DbCloudApiKey = dbCloudApiKey?.Value ?? "";
            ViewBag.DbCloudModel = dbCloudModel?.Value ?? _providerSettings.Cloud.Model;
            ViewBag.DbCloudDeployment = dbCloudDeployment?.Value ?? _providerSettings.Cloud.DeploymentName ?? "";
            ViewBag.DbCloudTimeoutSeconds = dbCloudTimeoutSeconds?.Value ?? _providerSettings.Cloud.TimeoutSeconds.ToString();
            ViewBag.DbCloudMaxPromptChars = dbCloudMaxPromptChars?.Value ?? _providerSettings.Cloud.MaxPromptChars.ToString();
            ViewBag.DbCloudTemperature = dbCloudTemperature?.Value ?? _providerSettings.Cloud.Temperature.ToString("0.##");
            ViewBag.DbCloudMaxTokens = dbCloudMaxTokens?.Value ?? _providerSettings.Cloud.MaxTokens.ToString();
            ViewBag.DbCloudApiVersion = dbCloudApiVersion?.Value ?? _providerSettings.Cloud.ApiVersion ?? "";
            ViewBag.DbCloudAuthHeaderName = dbCloudAuthHeaderName?.Value ?? _providerSettings.Cloud.AuthHeaderName;
            ViewBag.DbCloudUseBearerToken = bool.TryParse(dbCloudUseBearerToken?.Value, out var dbUseBearer)
                ? dbUseBearer
                : _providerSettings.Cloud.UseBearerToken;
            ViewBag.DbLocalAiBaseUrl = dbLocalAiBaseUrl?.Value ?? _providerSettings.LocalAI.BaseUrl;
            ViewBag.DbLocalAiModel = dbLocalAiModel?.Value ?? _providerSettings.LocalAI.Model;
            ViewBag.DbLocalAiApiKey = dbLocalAiApiKey?.Value ?? _providerSettings.LocalAI.ApiKey;
            ViewBag.DbLocalAiTimeoutSeconds = dbLocalAiTimeoutSeconds?.Value ?? _providerSettings.LocalAI.TimeoutSeconds.ToString();
            ViewBag.DbLocalAiMaxPromptChars = dbLocalAiMaxPromptChars?.Value ?? _providerSettings.LocalAI.MaxPromptChars.ToString();
            ViewBag.DbLocalAiTemperature = dbLocalAiTemperature?.Value ?? _providerSettings.LocalAI.Temperature.ToString("0.##");
            ViewBag.DbGeminiKey = dbGeminiKey?.Value ?? "";
            ViewBag.DbGeminiModel = dbGeminiModel?.Value ?? "gemini-2.5-flash";
            ViewBag.DbGeminiTemperature = dbGeminiTemperature?.Value ?? _providerSettings.Cloud.Temperature.ToString("0.##");
            ViewBag.DbGeminiMaxTokens = dbGeminiMaxTokens?.Value ?? _providerSettings.Cloud.MaxTokens.ToString();

            // Groq config (legacy single-key fallback + retry knobs). Multi-key pool is loaded by JS.
            var dbGroqKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqApiKey);
            var dbGroqModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqModel);
            var dbGroqTemperature = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqTemperature);
            var dbGroqMaxTokens = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqMaxTokens);
            ViewBag.DbGroqKey = dbGroqKey?.Value ?? "";
            ViewBag.DbGroqModel = dbGroqModel?.Value ?? "llama-3.3-70b-versatile";
            ViewBag.DbGroqTemperature = dbGroqTemperature?.Value ?? "0.2";
            ViewBag.DbGroqMaxTokens = dbGroqMaxTokens?.Value ?? "2048";
            // Rate-limit & retry knobs (defaults match the values the provider falls back to in code).
            ViewBag.DbGeminiRetryOn429 = dbGeminiRetryOn429?.Value ?? "True";
            ViewBag.DbGeminiRetryDelayMs = dbGeminiRetryDelayMs?.Value ?? "30000";
            ViewBag.DbGeminiRetryMaxAttempts = dbGeminiRetryMaxAttempts?.Value ?? "2";
            ViewBag.DbGeminiAssessmentDelayMs = dbGeminiAssessmentDelayMs?.Value ?? "7000";
            ViewBag.TicketCount = await _context.Tickets.CountAsync();
            ViewBag.TicketCommentCount = await _context.TicketComments.CountAsync();
            ViewBag.TicketAttachmentCount = await _context.TicketAttachments.CountAsync();
            ViewBag.TicketHistoryCount = await _context.TicketHistories.CountAsync();
            ViewBag.NotificationCount = await _context.Notifications.CountAsync();
            ViewBag.AiAnalysisCount = await _context.TicketAiAnalyses.CountAsync();
            ViewBag.EmbeddingCount = await _context.TicketSemanticEmbeddings.CountAsync();
            var currentRuntimeTarget = _runtimeDatabaseTargetService.GetCurrent();
            ViewBag.CurrentConnectionString = currentRuntimeTarget.ConnectionString;
            ViewBag.CurrentRuntimeDatabaseProvider = currentRuntimeTarget.Provider.ToString();

            var currentConnectionBuilder = BuildConnectionStringBuilder(ViewBag.CurrentConnectionString);
            ViewBag.DbServerName = TempData.Peek(SqlServerLastServerNameTempDataKey) as string ?? currentConnectionBuilder.DataSource;
            ViewBag.DbDatabaseName = currentConnectionBuilder.InitialCatalog;
            ViewBag.DbUseSqlAuthentication = bool.TryParse(TempData.Peek(SqlServerLastUseSqlAuthTempDataKey) as string, out var useSqlAuth)
                ? useSqlAuth
                : !currentConnectionBuilder.IntegratedSecurity;
            ViewBag.DbUserName = TempData.Peek(SqlServerLastUserNameTempDataKey) as string ?? (!currentConnectionBuilder.IntegratedSecurity ? currentConnectionBuilder.UserID : "");
            ViewBag.DbTrustServerCertificate = currentConnectionBuilder.TrustServerCertificate;
            ViewBag.DbEncryptConnection = currentConnectionBuilder.Encrypt;
            ViewBag.SqlServerCandidates = DeserializeTempData<List<SqlServerCandidateInfo>>(SqlServerCandidatesTempDataKey) ?? new List<SqlServerCandidateInfo>();
            ViewBag.SqlServerProbe = DeserializeTempData<SqlServerProbeResult>(SqlServerProbeTempDataKey);

            var externalApis = await _context.ExternalApiSettings.OrderBy(a => a.Title).ToListAsync();
            ViewBag.ExternalApis = externalApis;

            var copilotTools = await _toolRegistry.GetAllToolsAsync();
            ViewBag.CopilotTools = copilotTools;

            return View(themes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedData()
        {
            try
            {
                await DbSeeder.SeedOperationalDataAsync(_serviceProvider, _userManager, _roleManager);
                TempData["Success"] = "Operational seed data loaded successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Data seed failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FlushData()
        {
            try
            {
                await DbSeeder.PurgeOperationalDataAsync(_serviceProvider);
                TempData["Success"] = "Operational data flushed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Data flush failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FlushAndSeedData()
        {
            try
            {
                await DbSeeder.PurgeOperationalDataAsync(_serviceProvider);
                await DbSeeder.SeedOperationalDataAsync(_serviceProvider, _userManager, _roleManager);
                TempData["Success"] = "Operational data flushed and reseeded successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Flush and seed failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateRuntimeDatabaseTarget(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                TempData["Error"] = "Database connection string is required.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            try
            {
                var target = BuildSqlServerTarget(connectionString);
                await ActivateRuntimeTargetAsync(target, runMigrations: false);
                TempData["Success"] = "Runtime database target switched successfully. It is not persisted and will reset after app restart.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Runtime target switch failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DiscoverSqlServers()
        {
            try
            {
                var candidates = await DiscoverSqlServerCandidatesAsync();
                StoreTempData(SqlServerCandidatesTempDataKey, candidates);
                TempData["Success"] = candidates.Count > 0
                    ? $"Detected {candidates.Count} reachable SQL Server instance(s)."
                    : "No reachable local SQL Server instances were detected. You can still type a server name manually.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"SQL Server discovery failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InspectSqlServer(string serverName, bool useSqlAuthentication = false, string userName = "", string password = "")
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                TempData["Error"] = "Server name is required.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            TempData[SqlServerLastServerNameTempDataKey] = serverName.Trim();
            TempData[SqlServerLastUseSqlAuthTempDataKey] = useSqlAuthentication.ToString();
            TempData[SqlServerLastUserNameTempDataKey] = userName?.Trim() ?? "";

            try
            {
                var probe = await ProbeSqlServerAsync(serverName.Trim(), useSqlAuthentication, userName ?? "", password ?? "");
                StoreTempData(SqlServerProbeTempDataKey, probe);
                TempData["Success"] = $"Connected to SQL Server '{probe.ServerName}'.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Server inspection failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MigrateDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                TempData["Error"] = "Database connection string is required for migration.";
                return RedirectToAction(nameof(Index), new { tab = "data" });
            }

            try
            {
                var target = BuildSqlServerTarget(connectionString);
                await ActivateRuntimeTargetAsync(target, runMigrations: true);
                TempData["Success"] = "Database migrated and activated as the current runtime target. Nothing was written to appsettings.json.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Database migration failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { tab = "data" });
        }

        // ─── AI Provider Management ───────────────────────────────

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SetProviderModel([FromBody] SetProviderModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return Json(ApiResponse.Fail("Model name is required."));

            try
            {
                if (request.ProviderType == AiProviderType.Ollama.ToString())
                {
                    await UpsertSystemSettingAsync(SettingKeys.OllamaModelConfig, request.ModelName);
                    return Json(ApiResponse.Ok($"Ollama default model switched to '{request.ModelName}'."));
                }
                else if (request.ProviderType == AiProviderType.DockerLocal.ToString())
                {
                    await UpsertSystemSettingAsync(SettingKeys.DockerModel, request.ModelName);
                    return Json(ApiResponse.Ok($"Docker default model switched to '{request.ModelName}'."));
                }
                
                return Json(ApiResponse.Fail($"Provider '{request.ProviderType}' does not support dynamic model switching."));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Error switching model: {ex.Message}"));
            }
        }

        public class SetProviderModelRequest
        {
            public string? ProviderType { get; set; }
            public string? ModelName { get; set; }
        }

        // ─── Allowed-models curation ───────────────────────────────
        // Each provider has a small curated list of models the user has explicitly
        // allowed (1-3 typical). The workload-routing dropdowns source from these
        // lists, not from live discovery, so the user can keep choices short and
        // meaningful. The list is stored as a comma-separated value in SystemSettings.

        private static string ResolveAllowedModelsKey(string providerType)
        {
            if (string.IsNullOrWhiteSpace(providerType))
                throw new ArgumentException("Provider type is required.", nameof(providerType));

            return providerType switch
            {
                nameof(AiProviderType.Ollama) => SettingKeys.OllamaAllowedModels,
                nameof(AiProviderType.DockerLocal) => SettingKeys.DockerAllowedModels,
                nameof(AiProviderType.OpenAI) => SettingKeys.OpenAiAllowedModels,
                nameof(AiProviderType.Gemini) => SettingKeys.GeminiAllowedModels,
                nameof(AiProviderType.Groq) => SettingKeys.GroqAllowedModels,
                nameof(AiProviderType.Cloud) => SettingKeys.CloudAllowedModels,
                _ => throw new ArgumentException($"Provider '{providerType}' does not support a curated allowed-models list.", nameof(providerType))
            };
        }

        private async Task<List<string>> GetAllowedModelsAsync(string providerType)
        {
            var key = ResolveAllowedModelsKey(providerType);
            var raw = (await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key))?.Value ?? "";
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> GetAllowedProviderModels(string providerType)
        {
            try
            {
                var models = await GetAllowedModelsAsync(providerType);
                return Json(ApiResponse<object>.Ok(new { providerType, models }));
            }
            catch (ArgumentException ex)
            {
                return Json(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read allowed models for {Provider}", providerType);
                return Json(ApiResponse.Fail($"Failed to read allowed models: {ex.Message}"));
            }
        }

        public class AllowedModelRequest
        {
            public string? ProviderType { get; set; }
            public string? ModelName { get; set; }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> AddAllowedProviderModel([FromBody] AllowedModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ProviderType))
                return Json(ApiResponse.Fail("Provider type is required."));
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return Json(ApiResponse.Fail("Model name is required."));

            try
            {
                var key = ResolveAllowedModelsKey(request.ProviderType);
                var current = await GetAllowedModelsAsync(request.ProviderType);
                var name = request.ModelName.Trim();
                if (!current.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase)))
                {
                    current.Add(name);
                    await UpsertSystemSettingAsync(key, string.Join(",", current));
                }
                return Json(ApiResponse<object>.Ok(new { providerType = request.ProviderType, models = current }, $"Added '{name}'."));
            }
            catch (ArgumentException ex)
            {
                return Json(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add allowed model");
                return Json(ApiResponse.Fail($"Failed to add allowed model: {ex.Message}"));
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> RemoveAllowedProviderModel([FromBody] AllowedModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ProviderType))
                return Json(ApiResponse.Fail("Provider type is required."));
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return Json(ApiResponse.Fail("Model name is required."));

            try
            {
                var key = ResolveAllowedModelsKey(request.ProviderType);
                var current = await GetAllowedModelsAsync(request.ProviderType);
                var name = request.ModelName.Trim();
                var updated = current
                    .Where(m => !string.Equals(m, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await UpsertSystemSettingAsync(key, string.Join(",", updated));
                return Json(ApiResponse<object>.Ok(new { providerType = request.ProviderType, models = updated }, $"Removed '{name}'."));
            }
            catch (ArgumentException ex)
            {
                return Json(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove allowed model");
                return Json(ApiResponse.Fail($"Failed to remove allowed model: {ex.Message}"));
            }
        }

        public class PullModelRequest
        {
            public string ModelName { get; set; } = "";
        }

        [HttpPost]
        public async Task PullOllamaModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
            {
                Response.StatusCode = 400;
                await Response.WriteAsync(JsonSerializer.Serialize(new { error = "Model name is required" }));
                return;
            }

            try
            {
                var baseUrl = await GetSystemSettingAsync(SettingKeys.OllamaBaseUrl) ?? _providerSettings.Ollama.BaseUrl;
                using var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
                
                var requestBody = new { name = request.ModelName.Trim(), stream = true };
                var content = JsonContent.Create(requestBody);

                Response.StatusCode = 200;
                Response.ContentType = "application/x-ndjson";

                var requestUrl = $"{baseUrl.TrimEnd('/')}/api/pull";
                using var requestMsg = new HttpRequestMessage(HttpMethod.Post, requestUrl) { Content = content };
                using var response = await client.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    await Response.WriteAsync(JsonSerializer.Serialize(new { isError = true, error = $"Ollama returned {(int)response.StatusCode}" }) + "\n");
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                // Stream lines until ReadLineAsync returns null (end of stream). Avoids the
                // CA2024 anti-pattern where reader.EndOfStream synchronously blocks an async method.
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        await Response.WriteAsync(line + "\n");
                        await Response.Body.FlushAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync(JsonSerializer.Serialize(new { isError = true, error = ex.Message }) + "\n");
            }
        }

        private async Task<string?> GetSystemSettingAsync(string key)
        {
            var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
            return setting?.Value;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCopilotSettings(IFormCollection form)
        {
            static string Text(IFormCollection f, string key, string fallback = "") =>
                f.TryGetValue(key, out var value) ? value.ToString().Trim() : fallback;

            static string Bool(IFormCollection f, string key) =>
                f.TryGetValue(key, out var value)
                    && value.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                    ? "True"
                    : "False";

            await UpsertSystemSettingAsync(SettingKeys.CopilotTableExposureMode, Text(form, "tableExposureMode", TableExposureMode.AllExceptBlocked.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotBlockedTables, Text(form, "blockedTables"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotBlockedTablePatterns, Text(form, "blockedTablePatterns"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotBlockedColumns, Text(form, "blockedColumns"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotSensitiveColumns, Text(form, "sensitiveColumns"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotRetrieverTopK, Text(form, "retrieverTopK", _copilotOptions.RetrieverTopK.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotSchemaPromptStrategy, Text(form, "schemaPromptStrategy", _copilotOptions.SchemaPromptStrategy.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotUseVectorRetriever, Bool(form, "useVectorRetriever"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotUsePastQuestionRag, Bool(form, "usePastQuestionRag"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotFewShotTopK, Text(form, "fewShotTopK", _copilotOptions.FewShotTopK.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotUseLlmExplainer, Bool(form, "useLlmExplainer"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotMaxLlmCallsPerQuestion, Text(form, "maxLlmCallsPerQuestion", _copilotOptions.MaxLlmCallsPerQuestion.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotMaxSelfCorrectionRetries, Text(form, "maxSelfCorrectionRetries", _copilotOptions.MaxSelfCorrectionRetries.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotLlmCallTimeoutSeconds, Text(form, "llmCallTimeoutSeconds", _copilotOptions.LlmCallTimeoutSeconds.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotMaxQuestionWallClockSeconds, Text(form, "maxQuestionWallClockSeconds", _copilotOptions.MaxQuestionWallClockSeconds.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotMaxRows, Text(form, "maxRows", _copilotOptions.MaxRows.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotCommandTimeoutSeconds, Text(form, "commandTimeoutSeconds", _copilotOptions.CommandTimeoutSeconds.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotEnableSchemaIntrospection, Bool(form, "enableSchemaIntrospection"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotRestrictMetadataToConfiguredEntities, Bool(form, "restrictMetadataToConfiguredEntities"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotEnableResultCache, Bool(form, "enableResultCache"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotResultCacheTtlSeconds, Text(form, "resultCacheTtlSeconds", _copilotOptions.ResultCacheTtlSeconds.ToString()));
            await UpsertSystemSettingAsync(SettingKeys.CopilotEnableCostGate, Bool(form, "enableCostGate"));
            await UpsertSystemSettingAsync(SettingKeys.CopilotMaxEstimatedQueryCost, Text(form, "maxEstimatedQueryCost", _copilotOptions.MaxEstimatedQueryCost.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await UpsertSystemSettingAsync(SettingKeys.CopilotAmbiguityClarificationThreshold, Text(form, "ambiguityClarificationThreshold", _copilotOptions.AmbiguityClarificationThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await UpsertSystemSettingAsync(SettingKeys.CopilotResolverMinConfidence, Text(form, "resolverMinConfidence", _copilotOptions.ResolverMinConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Bust the IOptionsMonitor cache so singletons (HostAiProviderLlmClient,
            // CopilotOrchestrator, etc.) see the new DB-stored values on their next
            // CurrentValue access. Without this clear, the values stay cached until restart;
            // with it, hot-path consumers that read IOptionsMonitor pick them up immediately.
            // Components that captured a struct copy of CopilotOptions at construction
            // (singletons using IOptions<T> rather than IOptionsMonitor<T>) STILL need a
            // restart — those are the cases the toast continues to warn about.
            try
            {
                var monitor = HttpContext.RequestServices.GetService(
                    typeof(Microsoft.Extensions.Options.IOptionsMonitorCache<SuperAdminCopilot.Configuration.CopilotOptions>))
                    as Microsoft.Extensions.Options.IOptionsMonitorCache<SuperAdminCopilot.Configuration.CopilotOptions>;
                monitor?.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clear CopilotOptions monitor cache; values will still reload on next IConfiguration change or restart.");
            }
            TempData["Success"] = "Copilot settings saved. Hot-path settings (timeouts, caps) take effect immediately; some singleton services (factories, retrievers) still need a restart to fully refresh.";
            return RedirectToAction(nameof(Index), new { tab = "copilot" });
        }

        // ── Model pricing CRUD ──────────────────────────────────────────────────────────
        // Read by ICostCalculator at trace-save time. Edits land immediately (60s cache TTL in
        // CostCalculator). One row per (provider, model) tuple; use Model="*" for a provider-
        // wide default that matches when no specific model row exists.

        [HttpGet]
        public async Task<IActionResult> Pricing()
        {
            var rows = await _context.ModelPricings
                .OrderBy(p => p.Provider).ThenBy(p => p.Model)
                .ToListAsync();
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePricing(ServiceOpsAI.Models.AI.ModelPricing input)
        {
            if (string.IsNullOrWhiteSpace(input.Provider) || string.IsNullOrWhiteSpace(input.Model))
            {
                TempData["Error"] = "Provider and Model are required (use Model = \"*\" for a provider-wide default).";
                return RedirectToAction(nameof(Pricing));
            }
            // Upsert on (Provider, Model). Existing row wins on edit; new row inserted on add.
            var existing = await _context.ModelPricings.FirstOrDefaultAsync(
                p => p.Provider == input.Provider && p.Model == input.Model);
            if (existing is null)
            {
                input.CreatedAt = DateTime.UtcNow;
                input.UpdatedAt = DateTime.UtcNow;
                _context.ModelPricings.Add(input);
            }
            else
            {
                existing.DisplayName = input.DisplayName;
                existing.InputPer1K = input.InputPer1K;
                existing.OutputPer1K = input.OutputPer1K;
                existing.Currency = string.IsNullOrWhiteSpace(input.Currency) ? "USD" : input.Currency.ToUpperInvariant();
                existing.ContextTokens = input.ContextTokens;
                existing.IsLocal = input.IsLocal;
                existing.IsActive = input.IsActive;
                existing.Notes = input.Notes;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await _context.SaveChangesAsync();
            // Bust the calculator's in-memory cache so the next question picks up the new rate
            // immediately instead of waiting out the 60-second TTL — admins editing a price
            // expect their change to take effect now, not a minute from now.
            (_serviceProvider.GetService(typeof(ServiceOpsAI.Services.AI.Cost.ICostCalculator))
                as ServiceOpsAI.Services.AI.Cost.ICostCalculator)?.InvalidateAll();
            TempData["Success"] = $"Saved pricing for {input.Provider}/{input.Model}.";
            return RedirectToAction(nameof(Pricing));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePricing(int id)
        {
            var row = await _context.ModelPricings.FindAsync(id);
            if (row is not null)
            {
                _context.ModelPricings.Remove(row);
                await _context.SaveChangesAsync();
                (_serviceProvider.GetService(typeof(ServiceOpsAI.Services.AI.Cost.ICostCalculator))
                    as ServiceOpsAI.Services.AI.Cost.ICostCalculator)?.InvalidateAll();
                TempData["Success"] = $"Deleted {row.Provider}/{row.Model}.";
            }
            return RedirectToAction(nameof(Pricing));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActiveProvider(string providerType)
        {
            if (string.IsNullOrWhiteSpace(providerType))
                return BadRequest("Provider type is required");

            // Validate provider type
            var validTypes = new[] { AiProviderNames.DockerLocal, AiProviderNames.LegacyDockerLocalAlias, AiProviderNames.OpenAI, AiProviderNames.Cloud, AiProviderNames.LocalAI, AiProviderNames.Gemini, AiProviderNames.Ollama, AiProviderNames.Groq };
            if (!validTypes.Contains(providerType))
                return BadRequest($"Invalid provider type: {providerType}");

            // Map old type to new if necessary
            if (providerType == AiProviderNames.LegacyDockerLocalAlias) providerType = AiProviderNames.DockerLocal;

            await UpsertSystemSettingAsync(SettingKeys.AiActiveProvider, providerType);

            TempData["Success"] = $"Default AI Provider switched to: {providerType}";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkloadRouting(
            string activeProvider,
            string analysisProvider, string? analysisModel,
            string ragProvider, string? ragModel,
            string copilotProvider, string? copilotModel,
            string? classifierProvider, string? classifierModel,
            // Per-role provider + model overrides — empty = inherit from
            // CopilotProvider / CopilotWorkloadModel. Same cascading shape as the workload slots.
            string? decomposerProvider = null, string? decomposerModel = null,
            string? schemaLinkerProvider = null, string? schemaLinkerModel = null,
            string? structuralCueParserProvider = null, string? structuralCueParserModel = null,
            string? selfCorrectorProvider = null, string? selfCorrectorModel = null,
            string? explainerProvider = null, string? explainerModel = null)
        {
            await UpsertSystemSettingAsync(SettingKeys.AiActiveProvider, activeProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.AiAnalysisProvider, analysisProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.AiRagProvider, ragProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.AiCopilotProvider, copilotProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.AiClassifierProvider, classifierProvider ?? "");
            // Per-workload model overrides (empty value = use provider default).
            await UpsertSystemSettingAsync(SettingKeys.AnalysisWorkloadModel, analysisModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.RagWorkloadModel, ragModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.CopilotWorkloadModel, copilotModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.ClassifierWorkloadModel, classifierModel ?? "");
            // Per-ROLE provider + model overrides — finer granularity than the 4 workloads above.
            // Each is OPTIONAL; the RoleBoundLlmClientFactory falls back to the
            // CopilotProvider/CopilotWorkloadModel pair when a slot is empty.
            await UpsertSystemSettingAsync(SettingKeys.DecomposerRoleProvider, decomposerProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.DecomposerRoleModel, decomposerModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.SchemaLinkerRoleProvider, schemaLinkerProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.SchemaLinkerRoleModel, schemaLinkerModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.StructuralCueParserRoleProvider, structuralCueParserProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.StructuralCueParserRoleModel, structuralCueParserModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.SelfCorrectorRoleProvider, selfCorrectorProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.SelfCorrectorRoleModel, selfCorrectorModel ?? "");
            await UpsertSystemSettingAsync(SettingKeys.ExplainerRoleProvider, explainerProvider ?? "");
            await UpsertSystemSettingAsync(SettingKeys.ExplainerRoleModel, explainerModel ?? "");

            TempData["Success"] = "AI Workload routing saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveOllamaConfig(string baseUrl, string model, string? embeddingModel, int? timeoutSeconds, int? maxPromptChars, int? contextWindow, double? temperature)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.OllamaBaseUrl, baseUrl);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.OllamaModelConfig, model);
            if (!string.IsNullOrWhiteSpace(embeddingModel))
                await UpsertSystemSettingAsync(SettingKeys.OllamaEmbeddingModel, embeddingModel);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OllamaTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OllamaMaxPromptChars, maxPromptChars.Value.ToString());
            if (contextWindow.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OllamaContextWindow, contextWindow.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OllamaTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TempData["Success"] = "Ollama settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveOpenAiConfig(string apiKey, string model, string baseUrl, int? timeoutSeconds, int? maxPromptChars, double? temperature, int? maxTokens, string organizationId, string projectId)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiModel, model);
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.OpenAiBaseUrl, baseUrl);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.OpenAiMaxTokens, maxTokens.Value.ToString());
            await UpsertSystemSettingAsync(SettingKeys.OpenAiOrganizationId, organizationId ?? "");
            await UpsertSystemSettingAsync(SettingKeys.OpenAiProjectId, projectId ?? "");

            TempData["Success"] = "OpenAI / ChatGPT settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCloudConfig(string endpoint, string apiKey, string model, string deploymentName, int? timeoutSeconds, int? maxPromptChars, double? temperature, int? maxTokens, string apiVersion, string authHeaderName, bool useBearerToken)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                await UpsertSystemSettingAsync(SettingKeys.CloudEndpoint, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.CloudApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.CloudModel, model);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.CloudMaxTokens, maxTokens.Value.ToString());

            await UpsertSystemSettingAsync(SettingKeys.CloudDeploymentName, deploymentName ?? "");
            await UpsertSystemSettingAsync(SettingKeys.CloudApiVersion, apiVersion ?? "");
            await UpsertSystemSettingAsync(SettingKeys.CloudAuthHeaderName, authHeaderName ?? "api-key");
            await UpsertSystemSettingAsync(SettingKeys.CloudUseBearerToken, useBearerToken.ToString());

            TempData["Success"] = "Cloud AI settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLocalAiConfig(string baseUrl, string model, string apiKey, int? timeoutSeconds, int? maxPromptChars, double? temperature)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.LocalAiBaseUrl, baseUrl);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.LocalAiModel, model);
            await UpsertSystemSettingAsync(SettingKeys.LocalAiApiKey, apiKey ?? "");
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiMaxPromptChars, maxPromptChars.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.LocalAiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TempData["Success"] = "LocalAI settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveGeminiConfig(
            string apiKey, string model, double? temperature, int? maxTokens,
            // Rate-limit / retry knobs (all optional — provider has sensible defaults if a row is missing).
            int? assessmentDelayMs, bool? retryOn429, int? retryDelayMs, int? retryMaxAttempts)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.GeminiApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.GeminiModel, model);
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GeminiTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GeminiMaxTokens, maxTokens.Value.ToString());

            // Always persist the rate-limit fields (use 0 / false / 1 to explicitly disable).
            if (assessmentDelayMs.HasValue && assessmentDelayMs.Value >= 0)
                await UpsertSystemSettingAsync(SettingKeys.GeminiAssessmentDelayMs, assessmentDelayMs.Value.ToString());
            if (retryOn429.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GeminiRetryOn429, retryOn429.Value.ToString());
            if (retryDelayMs.HasValue && retryDelayMs.Value > 0)
                await UpsertSystemSettingAsync(SettingKeys.GeminiRetryDelayMs, retryDelayMs.Value.ToString());
            if (retryMaxAttempts.HasValue && retryMaxAttempts.Value > 0)
                await UpsertSystemSettingAsync(SettingKeys.GeminiRetryMaxAttempts, retryMaxAttempts.Value.ToString());

            TempData["Success"] = "Gemini settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDockerLocalConfig(string baseUrl, int? timeoutSeconds, int? maxPromptChars, int? maxPromptTokens, double? temperature, int? numCtx, int? numPredict)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                await UpsertSystemSettingAsync(SettingKeys.DockerBaseUrl, baseUrl);
            if (timeoutSeconds.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerTimeoutSeconds, timeoutSeconds.Value.ToString());
            if (maxPromptChars.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerMaxPromptChars, maxPromptChars.Value.ToString());
            if (maxPromptTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerMaxPromptTokens, maxPromptTokens.Value.ToString());
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (numCtx.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerNumCtx, numCtx.Value.ToString());
            if (numPredict.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.DockerNumPredict, numPredict.Value.ToString());

            TempData["Success"] = "Docker engine settings saved. Default model is managed from the installed model list.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        // ─── Gemini Multi-Key Pool ─────────────────────────────────────────────
        // The pool rotates across saved API keys so one key's daily 250 RPD cap doesn't bottleneck
        // the copilot. Each call below is a thin wrapper around IGeminiKeyPool.

        /// <summary>List every key with its computed status (today's usage + estimated remaining + last error).</summary>
        [HttpGet]
        public async Task<IActionResult> ListGeminiKeys()
        {
            var statuses = await _geminiKeyPool.ListStatusesAsync();
            return Json(ApiResponse<object>.Ok(new { Keys = statuses }));
        }

        /// <summary>
        /// Add a new Gemini API key to the pool. Optional <paramref name="ownerEmail"/> records which
        /// Google account it belongs to (operator-typed; we can't derive it from the key — Google
        /// API keys are user-anonymous). Duplicate keys (by SHA-256 fingerprint) are rejected
        /// with a structured response so the UI can show "already in pool as X" instead of a generic error.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddGeminiKey(string label, string apiKey, string? ownerEmail = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(ApiResponse.Fail("API key cannot be empty."));
            try
            {
                var result = await _geminiKeyPool.AddAsync(label ?? string.Empty, apiKey, ownerEmail);
                // Emit a custom shape so the JS can handle "duplicate" specially (highlight the
                // existing row instead of just showing an error toast).
                return Json(new
                {
                    success = result.Added,
                    message = result.Message,
                    data = new
                    {
                        id = result.KeyId,
                        duplicateOfId = result.DuplicateOfId,
                        duplicateLabel = result.DuplicateLabel
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add Gemini key.");
                return Json(ApiResponse.Fail($"Could not add key: {ex.Message}"));
            }
        }

        /// <summary>Remove a key permanently.</summary>
        [HttpPost]
        public async Task<IActionResult> RemoveGeminiKey(int id)
        {
            await _geminiKeyPool.RemoveAsync(id);
            return Json(ApiResponse.Ok("Key removed."));
        }

        /// <summary>Toggle IsActive without deleting — use to "park" a key.</summary>
        [HttpPost]
        public async Task<IActionResult> SetGeminiKeyActive(int id, bool active)
        {
            await _geminiKeyPool.SetActiveAsync(id, active);
            return Json(ApiResponse.Ok(active ? "Key enabled." : "Key disabled."));
        }

        /// <summary>Manually reset today's request counter for a key (and clear any rate-limit timestamp).</summary>
        [HttpPost]
        public async Task<IActionResult> ResetGeminiKeyCount(int id)
        {
            await _geminiKeyPool.ResetCountAsync(id);
            return Json(ApiResponse.Ok("Counter reset."));
        }

        /// <summary>Update the friendly label of a key.</summary>
        [HttpPost]
        public async Task<IActionResult> RenameGeminiKey(int id, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return Json(ApiResponse.Fail("Label cannot be empty."));
            await _geminiKeyPool.RenameAsync(id, label);
            return Json(ApiResponse.Ok("Label updated."));
        }

        /// <summary>
        /// Probe a specific pool key by firing a minimal "ok" request directly at Gemini. Bypasses
        /// the pool's rotation/parking logic so the operator can verify "is THIS key actually
        /// working RIGHT NOW?" — useful after adding a new key or to verify a parked key recovered.
        /// Reports latency + parsed status (200=valid, 403=invalid/expired, 429=quota-cooling).
        /// Does NOT mutate the pool's counters (probe shouldn't burn the user's RPD).
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> TestGeminiKey(int id)
        {
            var key = await _context.GeminiApiKeys.FirstOrDefaultAsync(k => k.Id == id);
            if (key is null) return Json(ApiResponse.Fail($"Key id={id} not found."));

            var model = (await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiModel))?.Value
                        ?? "gemini-2.5-flash";
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key.ApiKey}";
            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = "Respond with the single word ok and nothing else." } } } },
                generationConfig = new { temperature = 0.0, maxOutputTokens = 8 }
            };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resp = await client.PostAsync(url, new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json"));
                sw.Stop();
                var body = await resp.Content.ReadAsStringAsync();
                var status = (int)resp.StatusCode;
                var verdict = status switch
                {
                    200 => "ok",
                    400 => "bad-request",
                    401 or 403 => "invalid-or-expired",
                    429 => "rate-limited",
                    >= 500 => "google-error",
                    _ => "unexpected"
                };
                return Json(ApiResponse<object>.Ok(new
                {
                    label = key.Label,
                    verdict,
                    httpStatus = status,
                    elapsedMs = sw.ElapsedMilliseconds,
                    bodyPreview = body.Length > 200 ? body.Substring(0, 200) + "…" : body
                }, $"{verdict} (HTTP {status}) in {sw.ElapsedMilliseconds}ms"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Json(ApiResponse<object>.Ok(new
                {
                    label = key.Label,
                    verdict = "network-error",
                    httpStatus = 0,
                    elapsedMs = sw.ElapsedMilliseconds,
                    bodyPreview = ex.Message
                }, $"network-error in {sw.ElapsedMilliseconds}ms: {ex.Message}"));
            }
        }

        // ─── Groq Configuration & Multi-Key Pool ───────────────────────────────
        // Mirrors the Gemini endpoints. Groq publishes real `x-ratelimit-*` headers per call so
        // ListGroqKeys returns authoritative remaining-quota in addition to our own counters.

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveGroqConfig(
            string apiKey, string model, double? temperature, int? maxTokens,
            bool? retryOn429, int? retryDelayMs, int? retryMaxAttempts)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                await UpsertSystemSettingAsync(SettingKeys.GroqApiKey, apiKey);
            if (!string.IsNullOrWhiteSpace(model))
                await UpsertSystemSettingAsync(SettingKeys.GroqModel, model);
            if (temperature.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GroqTemperature, temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (maxTokens.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GroqMaxTokens, maxTokens.Value.ToString());
            if (retryOn429.HasValue)
                await UpsertSystemSettingAsync(SettingKeys.GroqRetryOn429, retryOn429.Value.ToString());
            if (retryDelayMs.HasValue && retryDelayMs.Value > 0)
                await UpsertSystemSettingAsync(SettingKeys.GroqRetryDelayMs, retryDelayMs.Value.ToString());
            if (retryMaxAttempts.HasValue && retryMaxAttempts.Value > 0)
                await UpsertSystemSettingAsync(SettingKeys.GroqRetryMaxAttempts, retryMaxAttempts.Value.ToString());

            TempData["Success"] = "Groq settings saved to the database.";
            return RedirectToAction(nameof(Index), new { tab = "ai" });
        }

        [HttpGet]
        public async Task<IActionResult> ListGroqKeys()
        {
            var statuses = await _groqKeyPool.ListStatusesAsync();
            return Json(ApiResponse<object>.Ok(new { Keys = statuses }));
        }

        [HttpPost]
        public async Task<IActionResult> AddGroqKey(string label, string apiKey, string? ownerEmail = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(ApiResponse.Fail("API key cannot be empty."));
            try
            {
                var result = await _groqKeyPool.AddAsync(label ?? string.Empty, apiKey, ownerEmail);
                return Json(new
                {
                    success = result.Added,
                    message = result.Message,
                    data = new
                    {
                        id = result.KeyId,
                        duplicateOfId = result.DuplicateOfId,
                        duplicateLabel = result.DuplicateLabel
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add Groq key.");
                return Json(ApiResponse.Fail($"Could not add key: {ex.Message}"));
            }
        }

        [HttpPost]
        public async Task<IActionResult> RemoveGroqKey(int id)
        {
            await _groqKeyPool.RemoveAsync(id);
            return Json(ApiResponse.Ok("Key removed."));
        }

        [HttpPost]
        public async Task<IActionResult> SetGroqKeyActive(int id, bool active)
        {
            await _groqKeyPool.SetActiveAsync(id, active);
            return Json(ApiResponse.Ok(active ? "Key enabled." : "Key disabled."));
        }

        [HttpPost]
        public async Task<IActionResult> ResetGroqKeyCount(int id)
        {
            await _groqKeyPool.ResetCountAsync(id);
            return Json(ApiResponse.Ok("Counter reset."));
        }

        [HttpPost]
        public async Task<IActionResult> RenameGroqKey(int id, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return Json(ApiResponse.Fail("Label cannot be empty."));
            await _groqKeyPool.RenameAsync(id, label);
            return Json(ApiResponse.Ok("Label updated."));
        }

        /// <summary>
        /// Send a one-off prompt to the chosen provider and return the raw response. Lets admins
        /// sanity-check whether a model is alive (e.g. is Gemini 503 right now?) without going
        /// through the full copilot pipeline. Returns the response text, latency, and any error
        /// verbatim — no synthesis or fallback.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AskProvider(string providerType, string question, string? model = null)
        {
            if (string.IsNullOrWhiteSpace(question))
                return Json(ApiResponse.Fail("Question is empty."));
            if (!Enum.TryParse<AiProviderType>(providerType, out var type))
                return Json(ApiResponse.Fail("Invalid provider type."));

            // The Quick Test UI requires the user to pick a model. Upsert it as the
            // provider's default chat model so providers (which all read ModelName
            // from a SystemSettings key) use the selected model for this call and
            // any subsequent calls until the user picks differently.
            if (!string.IsNullOrWhiteSpace(model))
            {
                var modelKey = TryResolveProviderModelSettingKey(type);
                if (modelKey is not null)
                {
                    await UpsertSystemSettingAsync(modelKey, model.Trim());
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var provider = _providerFactory.GetProvider(type);
                var result = await provider.GenerateAsync(question);
                sw.Stop();

                return Json(new
                {
                    success = result.Success,
                    response = result.ResponseText ?? string.Empty,
                    error = result.Error ?? string.Empty,
                    modelName = provider.ModelName,
                    latencyMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "AskProvider failed for {Provider}", providerType);
                return Json(new
                {
                    success = false,
                    response = string.Empty,
                    error = $"{ex.GetType().Name}: {ex.Message}",
                    modelName = providerType,
                    latencyMs = sw.ElapsedMilliseconds
                });
            }
        }

        // Maps a provider to the SystemSettings key that stores its current chat model.
        // Returns null for providers that don't have a single-model setting.
        private static string? TryResolveProviderModelSettingKey(AiProviderType type) => type switch
        {
            AiProviderType.Ollama       => SettingKeys.OllamaModelConfig,
            AiProviderType.DockerLocal  => SettingKeys.DockerModel,
            AiProviderType.OpenAI       => SettingKeys.OpenAiModel,
            AiProviderType.Gemini       => SettingKeys.GeminiModel,
            AiProviderType.Groq         => SettingKeys.GroqModel,
            AiProviderType.Cloud        => SettingKeys.CloudModel,
            _ => null
        };

        [HttpGet]
        public async Task<IActionResult> ValidateProvider(string providerType)
        {
            try
            {
                if (providerType == AiProviderNames.DockerLocal || providerType == AiProviderNames.LegacyDockerLocalAlias)
                {
                    var result = await _dockerService.IsDockerRunningAsync();
                    if (result)
                        return Json(ApiResponse.Ok("Docker Engine is active and responding."));
                    return Json(ApiResponse.Fail("Docker Engine is not running."));
                }
                else if (providerType == AiProviderNames.OpenAI)
                {
                    var dbKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiApiKey);
                    var apiKey = dbKey?.Value ?? _providerSettings.OpenAI.ApiKey;
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return Json(ApiResponse.Fail("API Key is not configured"));

                    var dbBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.OpenAiBaseUrl);
                    var baseUrl = dbBaseUrl?.Value ?? _providerSettings.OpenAI.BaseUrl;

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models");
                    if (response.IsSuccessStatusCode)
                        return Json(ApiResponse.Ok("OpenAI API connection verified"));
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        return Json(ApiResponse.Fail("API Key is invalid or expired"));
                    return Json(ApiResponse.Fail($"OpenAI returned HTTP {(int)response.StatusCode}"));
                }
                else if (providerType == AiProviderNames.Cloud)
                {
                    var dbEndpoint = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.CloudEndpoint);
                    var endpoint = dbEndpoint?.Value ?? _providerSettings.Cloud.Endpoint;
                    if (string.IsNullOrWhiteSpace(endpoint))
                        return Json(ApiResponse.Fail("Cloud endpoint is not configured"));

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var response = await http.GetAsync(endpoint);
                    return Json(ApiResponse.Ok($"Cloud endpoint reachable at {endpoint}"));
                }
                else if (providerType == AiProviderNames.LocalAI)
                {
                    var dbBaseUrl = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LocalAiBaseUrl);
                    var baseUrl = dbBaseUrl?.Value ?? _providerSettings.LocalAI.BaseUrl;

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models");
                    if (response.IsSuccessStatusCode)
                        return Json(ApiResponse.Ok($"Connected to LocalAI at {baseUrl}"));
                    return Json(ApiResponse.Fail($"LocalAI returned HTTP {(int)response.StatusCode}"));
                }
                else if (providerType == AiProviderNames.Gemini)
                {
                    var dbKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiApiKey);
                    var apiKey = dbKey?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return Json(ApiResponse.Fail("Gemini API Key is not configured"));

                    var dbModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GeminiModel);
                    var model = dbModel?.Value ?? "gemini-2.5-flash";

                    // Do a REAL generateContent call (not just GET model info) so this test catches
                    // the same failure modes the live classifier hits: MAX_TOKENS, SAFETY blocks,
                    // wrong key, quota exceeded, model unavailable. The previous GET only checked
                    // that the model existed, so a key with no generation quota would silently pass.
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                    var requestBody = new
                    {
                        contents = new[] { new { parts = new[] { new { text = "Reply with the single word: ok" } } } },
                        generationConfig = new { temperature = 0.0, maxOutputTokens = 64 }
                    };
                    var content = new System.Net.Http.StringContent(
                        System.Text.Json.JsonSerializer.Serialize(requestBody),
                        System.Text.Encoding.UTF8,
                        "application/json");
                    var response = await http.PostAsync(url, content);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var snippet = body.Length > 400 ? body[..400] + "..." : body;
                        return Json(ApiResponse.Fail($"Gemini HTTP {(int)response.StatusCode}: {snippet}"));
                    }

                    // Verify the response shape — same defensive walk the live provider uses now.
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("promptFeedback", out var pf) &&
                            pf.TryGetProperty("blockReason", out var br))
                        {
                            return Json(ApiResponse.Fail($"Gemini blocked the test prompt: {br.GetString()}"));
                        }
                        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                        {
                            return Json(ApiResponse.Fail("Gemini returned no candidates"));
                        }
                        var first = candidates[0];
                        var finishReason = first.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "unknown";
                        if (!first.TryGetProperty("content", out var cnt) ||
                            !cnt.TryGetProperty("parts", out var parts) ||
                            parts.GetArrayLength() == 0)
                        {
                            return Json(ApiResponse.Fail($"Gemini test got no content parts (finishReason={finishReason}). Try increasing Max Tokens."));
                        }
                        var text = parts[0].TryGetProperty("text", out var t) ? t.GetString() : null;
                        return Json(ApiResponse.Ok($"Gemini '{model}' OK (finishReason={finishReason}, replied: {text?.Trim()})"));
                    }
                    catch (Exception parseEx)
                    {
                        return Json(ApiResponse.Fail($"Gemini response parse failed: {parseEx.Message}"));
                    }
                }
                else if (providerType == AiProviderNames.Groq)
                {
                    // Resolve key: pool first, then legacy SystemSettings — same lookup the runtime uses.
                    var pooled = await _groqKeyPool.AcquireAsync();
                    string? apiKey = pooled?.ApiKey;
                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        var dbKey = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqApiKey);
                        apiKey = dbKey?.Value;
                    }
                    if (string.IsNullOrWhiteSpace(apiKey))
                        return Json(ApiResponse.Fail("Groq API key is not configured (no pool keys, no legacy key)."));

                    var dbModel = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.GroqModel);
                    var model = dbModel?.Value ?? "llama-3.3-70b-versatile";

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                    var url = "https://api.groq.com/openai/v1/chat/completions";
                    var requestBody = new
                    {
                        model,
                        messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
                        temperature = 0.0,
                        max_tokens = 16
                    };
                    var content = new System.Net.Http.StringContent(
                        System.Text.Json.JsonSerializer.Serialize(requestBody),
                        System.Text.Encoding.UTF8,
                        "application/json");
                    var response = await http.PostAsync(url, content);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var snippet = body.Length > 400 ? body[..400] + "..." : body;
                        return Json(ApiResponse.Fail($"Groq HTTP {(int)response.StatusCode}: {snippet}"));
                    }

                    // Pull rate-limit headers — gives the operator a real "credits remaining" readout
                    // straight from the Test button click, even before the pool is populated.
                    var remReq = response.Headers.TryGetValues("x-ratelimit-remaining-requests", out var r) ? r.FirstOrDefault() : null;
                    var remTok = response.Headers.TryGetValues("x-ratelimit-remaining-tokens", out var t) ? t.FirstOrDefault() : null;
                    var quotaSuffix = (remReq != null || remTok != null)
                        ? $" — Remaining: {remReq ?? "?"} req · {remTok ?? "?"} tokens this window"
                        : "";

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        var first = doc.RootElement.GetProperty("choices")[0];
                        var reply = first.GetProperty("message").GetProperty("content").GetString();
                        return Json(ApiResponse.Ok($"Groq '{model}' OK (replied: {reply?.Trim()}){quotaSuffix}"));
                    }
                    catch (Exception parseEx)
                    {
                        return Json(ApiResponse.Fail($"Groq response parse failed: {parseEx.Message}"));
                    }
                }

                return Json(ApiResponse.Fail("Unknown provider type"));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Connection failed: {ex.Message}"));
            }
        }

        // ─── AI Model Management (Docker) ──────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetProviderModels(string providerType)
        {
            try
            {
                if (!Enum.TryParse<AiProviderType>(providerType, out var type))
                    return Json(ApiResponse.Fail("Invalid provider type."));

                var provider = _providerFactory.GetProvider(type);
                var models = await provider.GetInstalledModelsAsync();

                return Json(ApiResponse<DockerModelsResponseDto>.Ok(new DockerModelsResponseDto
                {
                    Models = models,
                    ActiveModel = provider.ModelName
                }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get models for provider {Provider}", providerType);
                return Json(ApiResponse.Fail($"Discovery failed: {ex.Message}"));
            }
        }

        [HttpPost]
        public async Task PullDockerModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
            {
                Response.StatusCode = 400;
                await Response.WriteAsync(JsonSerializer.Serialize(new { error = "Model name is required" }));
                return;
            }

            try
            {
                Response.StatusCode = 200;
                Response.ContentType = "application/x-ndjson";
                var requestedName = request.ModelName.Trim();

                // Model tags (e.g. llama3.2, phi3:mini) use 'docker model pull'
                // Container images (e.g. vllm/vllm-openai:latest) use 'docker pull'
                bool isModelTag = !requestedName.Contains('/');
                string dockerArgs = isModelTag
                    ? $"model pull {requestedName}"
                    : $"pull {requestedName}";

                var statusMsg = JsonSerializer.Serialize(new { status = $"[CMD] docker {dockerArgs}" });
                await Response.WriteAsync(statusMsg + "\n");
                await Response.Body.FlushAsync();

                var processInfo = new ProcessStartInfo("docker", dockerArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    var errorLines = new List<string>();
                    var outTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var line = await process.StandardOutput.ReadLineAsync();
                            if (line == null) break;
                            
                            var json = JsonSerializer.Serialize(new { status = line });
                            await Response.WriteAsync(json + "\n");
                            await Response.Body.FlushAsync();
                        }
                    });

                    var errTask = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var line = await process.StandardError.ReadLineAsync();
                            if (line == null) break;

                            errorLines.Add(line);
                            var json = JsonSerializer.Serialize(new { status = line });
                            await Response.WriteAsync(json + "\n");
                            await Response.Body.FlushAsync();
                        }
                    });

                    await Task.WhenAll(outTask, errTask);
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode != 0)
                    {
                        var json = JsonSerializer.Serialize(new { isError = true, status = $"Process exited with code {process.ExitCode}." });
                        await Response.WriteAsync(json + "\n");
                        await Response.Body.FlushAsync();

                        if (isModelTag && errorLines.Any(line =>
                                line.Contains("pull access denied", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("repository does not exist", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("authorization failed", StringComparison.OrdinalIgnoreCase)))
                        {
                            var hintJson = JsonSerializer.Serialize(new
                            {
                                status = $"[HINT] '{requestedName}' is not a valid published Docker model name. Search first with 'docker model search {requestedName.Split(':')[0]}' and use the full model name returned by Docker, for example 'hf.co/microsoft/Phi-3-mini-4k-instruct' or 'ai/smollm2:360M-Q4_K_M'."
                            });
                            await Response.WriteAsync(hintJson + "\n");
                            await Response.Body.FlushAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync(JsonSerializer.Serialize(new { isError = true, error = $"Error: {ex.Message}" }));
            }
        }
        [HttpPost]
        public async Task<IActionResult> SetDockerDefaultModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return BadRequest(ApiResponse.Fail("Model name is required"));

            try
            {
                var requestedName = request.ModelName.Trim();
                var installedModelNames = await GetInstalledDockerModelNamesAsync();

                if (!installedModelNames.Contains(requestedName))
                {
                    return Json(ApiResponse.Fail($"'{requestedName}' is not installed as a Docker model on this host."));
                }

                await UpsertSystemSettingAsync(SettingKeys.DockerModel, requestedName);

                return Json(ApiResponse.Ok($"Runtime default switched to '{requestedName}'."));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Error: {ex.Message}"));
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDockerModel([FromBody] PullModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ModelName))
                return BadRequest(ApiResponse.Fail("Model name is required"));

            try
            {
                var requestedName = request.ModelName.Trim();
                var installedModelNames = await GetInstalledDockerModelNamesAsync();
                var isDockerModel = installedModelNames.Contains(requestedName);
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (isDockerModel)
                {
                    processInfo.ArgumentList.Add("model");
                    processInfo.ArgumentList.Add("rm");
                    processInfo.ArgumentList.Add(requestedName);
                }
                else
                {
                    processInfo.ArgumentList.Add("rmi");
                    processInfo.ArgumentList.Add(requestedName);
                }

                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        var defaultSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DockerModel);
                        var clearedDefault = false;
                        if (defaultSetting != null &&
                            string.Equals(defaultSetting.Value, requestedName, StringComparison.OrdinalIgnoreCase))
                        {
                            _context.SystemSettings.Remove(defaultSetting);
                            await _context.SaveChangesAsync();
                            clearedDefault = true;
                        }

                        var targetLabel = isDockerModel ? "Docker model" : "Docker image";
                        var suffix = clearedDefault ? " The saved runtime default was cleared." : "";
                        return Json(ApiResponse.Ok($"{targetLabel} '{requestedName}' was removed from Docker.{suffix}"));
                    }
                    else
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        return Json(ApiResponse.Fail($"Delete failed: {error}"));
                    }
                }
                return Json(ApiResponse.Fail("Failed to launch docker process."));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail($"Error: {ex.Message}"));
            }
        }

        [HttpGet]
        public async Task<IActionResult> CheckDockerStatus()
        {
            var isInstalled = await _dockerService.IsDockerInstalledAsync();
            if (!isInstalled) return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "missing", Message = "Docker CLI not found on host system." }));

            var isRunning = await _dockerService.IsDockerRunningAsync();
            if (!isRunning) return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "stopped", Message = "Docker is installed but currently not running." }));

            return Json(ApiResponse<DockerStatusDto>.Ok(new DockerStatusDto { Status = "running", Message = "Docker is active and healthy." }));
        }

        [HttpPost]
        public async Task<IActionResult> ProvisionEngine(string engineType)
        {
            if (string.IsNullOrWhiteSpace(engineType)) return BadRequest(ApiResponse.Fail("Engine type is required"));

            var result = await _dockerService.LaunchEngineAsync(engineType);
            if (result.Success)
            {
                return Json(ApiResponse.Ok(result.Message));
            }
            return Json(ApiResponse.Fail(result.Message));
        }

        // ─── Theme Management ──────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTheme(int themeId)
        {
            var theme = await _context.CustomThemes.FindAsync(themeId);
            if (theme == null) return NotFound();

            await UpsertSystemSettingAsync("DefaultTheme", themeId.ToString());

            TempData["Success"] = $"Platform Architecture Synchronized to: {theme.Name.ToUpper()}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTheme(CustomTheme model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    if (model.Id > 0)
                    {
                        var existing = await _context.CustomThemes.FirstOrDefaultAsync(t => t.Id == model.Id);
                        if (existing == null) return NotFound();

                        existing.Name = model.Name;
                        existing.PrimaryColor = model.PrimaryColor;
                        existing.BgMain = model.BgMain;
                        existing.BgCard = model.BgCard;
                        existing.BgSidebar = model.BgSidebar;
                        existing.BgHeader = model.BgHeader;
                        existing.TextMain = model.TextMain;
                        existing.TextMuted = model.TextMuted;
                        existing.BorderColor = model.BorderColor;
                        existing.PrimaryContrastText = model.PrimaryContrastText;
                        existing.BgSurfaceAlt = model.BgSurfaceAlt;
                        existing.BgElevated = model.BgElevated;
                        existing.SuccessColor = model.SuccessColor;
                        existing.WarningColor = model.WarningColor;
                        existing.DangerColor = model.DangerColor;
                        existing.InfoColor = model.InfoColor;
                        existing.ShadowColor = model.ShadowColor;
                        existing.IsSystemTheme = model.IsSystemTheme;
                        existing.SystemIdentifier = model.SystemIdentifier;
                        
                        _context.CustomThemes.Update(existing);
                        TempData["Success"] = $"Architecture Token '{model.Name}' refined successfully.";
                    }
                    else
                    {
                        _context.CustomThemes.Add(model);
                        TempData["Success"] = $"New Architecture Token '{model.Name}' established.";
                    }
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Architectural Persistence Error: {ex.Message}";
                }
            }
            else
            {
                var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                TempData["Error"] = $"Validation Collision: {errors}";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetTheme(int id)
        {
            var theme = await _context.CustomThemes.FindAsync(id);
            if (theme == null || !theme.IsSystemTheme) return NotFound();

            ResetSystemThemeToFactory(theme);

            _context.CustomThemes.Update(theme);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Architecture Token '{theme.Name}' reset to factory directive.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteTheme(int id)
        {
            var theme = await _context.CustomThemes.FindAsync(id);
            if (theme == null || theme.IsSystemTheme) return BadRequest();

            var currentThemeSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "DefaultTheme");
            if (currentThemeSetting != null && currentThemeSetting.Value == id.ToString())
            {
                TempData["Error"] = $"Conflict Resolution Failure: '{theme.Name}' is the currently active architecture. Transition before purging.";
                return RedirectToAction(nameof(Index));
            }

            _context.CustomThemes.Remove(theme);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Custom Architecture '{theme.Name}' successfully purged.";
            return RedirectToAction(nameof(Index));
        }

        // ─── Helper ───────────────────────────────────────────────

        private static void ResetSystemThemeToFactory(CustomTheme theme)
        {
            switch (theme.SystemIdentifier)
            {
                case "emerald-obsidian":
                    theme.PrimaryColor="#10b981"; theme.PrimaryContrastText="#000000";
                    theme.BgMain="#0a0a0a"; theme.BgCard="#141416"; theme.BgSurfaceAlt="#1a1a1d"; theme.BgElevated="#1f1f23";
                    theme.BgSidebar="#000000"; theme.BgHeader="#000000";
                    theme.TextMain="#ffffff"; theme.TextMuted="#a1a1aa"; theme.BorderColor="#27272a";
                    theme.SuccessColor="#34d399"; theme.WarningColor="#fbbf24"; theme.DangerColor="#f87171"; theme.InfoColor="#60a5fa";
                    theme.ShadowColor="rgba(0, 0, 0, 0.45)"; break;
                case "solar-ember":
                    theme.PrimaryColor="#f59e0b"; theme.PrimaryContrastText="#0b0b0b";
                    theme.BgMain="#111827"; theme.BgCard="#1f2937"; theme.BgSurfaceAlt="#273345"; theme.BgElevated="#2c3a4f";
                    theme.BgSidebar="#0d1421"; theme.BgHeader="#0d1421";
                    theme.TextMain="#f9fafb"; theme.TextMuted="#9ca3af"; theme.BorderColor="#374151";
                    theme.SuccessColor="#34d399"; theme.WarningColor="#fbbf24"; theme.DangerColor="#f87171"; theme.InfoColor="#60a5fa";
                    theme.ShadowColor="rgba(0, 0, 0, 0.4)"; break;
                case "midnight-azure":
                    theme.PrimaryColor="#3b82f6"; theme.PrimaryContrastText="#ffffff";
                    theme.BgMain="#020617"; theme.BgCard="#0f172a"; theme.BgSurfaceAlt="#172033"; theme.BgElevated="#1e293b";
                    theme.BgSidebar="#000814"; theme.BgHeader="#000814";
                    theme.TextMain="#f8fafc"; theme.TextMuted="#94a3b8"; theme.BorderColor="#1e293b";
                    theme.SuccessColor="#34d399"; theme.WarningColor="#fbbf24"; theme.DangerColor="#f87171"; theme.InfoColor="#60a5fa";
                    theme.ShadowColor="rgba(0, 0, 0, 0.5)"; break;
                case "cyber-neon":
                    theme.PrimaryColor="#f97316"; theme.PrimaryContrastText="#0b0b0b";
                    theme.BgMain="#0f0f0f"; theme.BgCard="#1a1a1a"; theme.BgSurfaceAlt="#212124"; theme.BgElevated="#26262b";
                    theme.BgSidebar="#000000"; theme.BgHeader="#000000";
                    theme.TextMain="#ffffff"; theme.TextMuted="#a1a1aa"; theme.BorderColor="#27272a";
                    theme.SuccessColor="#34d399"; theme.WarningColor="#fbbf24"; theme.DangerColor="#f87171"; theme.InfoColor="#60a5fa";
                    theme.ShadowColor="rgba(0, 0, 0, 0.5)"; break;
                case "deep-forest":
                    theme.PrimaryColor="#10b981"; theme.PrimaryContrastText="#022c22";
                    theme.BgMain="#022c22"; theme.BgCard="#064e3b"; theme.BgSurfaceAlt="#0a5e48"; theme.BgElevated="#0f6e54";
                    theme.BgSidebar="#011d17"; theme.BgHeader="#011d17";
                    theme.TextMain="#ecfdf5"; theme.TextMuted="#a7d4c0"; theme.BorderColor="#065f46";
                    theme.SuccessColor="#34d399"; theme.WarningColor="#fbbf24"; theme.DangerColor="#fca5a5"; theme.InfoColor="#7dd3fc";
                    theme.ShadowColor="rgba(0, 0, 0, 0.45)"; break;
                case "indigo-horizon":
                    theme.PrimaryColor="#6366f1"; theme.PrimaryContrastText="#ffffff";
                    theme.BgMain="#f1f5f9"; theme.BgCard="#ffffff"; theme.BgSurfaceAlt="#f8fafc"; theme.BgElevated="#ffffff";
                    theme.BgSidebar="#ffffff"; theme.BgHeader="#fafbfc";
                    theme.TextMain="#1e293b"; theme.TextMuted="#64748b"; theme.BorderColor="#e2e8f0";
                    theme.SuccessColor="#16a34a"; theme.WarningColor="#d97706"; theme.DangerColor="#dc2626"; theme.InfoColor="#2563eb";
                    theme.ShadowColor="rgba(15, 23, 42, 0.08)"; break;
                case "slate-alpine":
                    theme.PrimaryColor="#0ea5e9"; theme.PrimaryContrastText="#ffffff";
                    theme.BgMain="#f1f5f9"; theme.BgCard="#ffffff"; theme.BgSurfaceAlt="#f8fafc"; theme.BgElevated="#ffffff";
                    theme.BgSidebar="#ffffff"; theme.BgHeader="#f8fafc";
                    theme.TextMain="#0f172a"; theme.TextMuted="#475569"; theme.BorderColor="#cbd5e1";
                    theme.SuccessColor="#16a34a"; theme.WarningColor="#d97706"; theme.DangerColor="#dc2626"; theme.InfoColor="#2563eb";
                    theme.ShadowColor="rgba(15, 23, 42, 0.08)"; break;
                case "rose-quartz":
                    theme.PrimaryColor="#ec4899"; theme.PrimaryContrastText="#ffffff";
                    theme.BgMain="#fdf2f4"; theme.BgCard="#ffffff"; theme.BgSurfaceAlt="#fef7f9"; theme.BgElevated="#ffffff";
                    theme.BgSidebar="#ffffff"; theme.BgHeader="#fef7f9";
                    theme.TextMain="#1f2937"; theme.TextMuted="#64748b"; theme.BorderColor="#fbcfe8";
                    theme.SuccessColor="#16a34a"; theme.WarningColor="#d97706"; theme.DangerColor="#dc2626"; theme.InfoColor="#2563eb";
                    theme.ShadowColor="rgba(131, 24, 67, 0.08)"; break;
                case "emerald-light":
                    theme.PrimaryColor="#10b981"; theme.PrimaryContrastText="#ffffff";
                    theme.BgMain="#f1f5f9"; theme.BgCard="#ffffff"; theme.BgSurfaceAlt="#f8fafc"; theme.BgElevated="#ffffff";
                    theme.BgSidebar="#ffffff"; theme.BgHeader="#f8fafc";
                    theme.TextMain="#0f172a"; theme.TextMuted="#4b5563"; theme.BorderColor="#d1d5db";
                    theme.SuccessColor="#16a34a"; theme.WarningColor="#d97706"; theme.DangerColor="#dc2626"; theme.InfoColor="#2563eb";
                    theme.ShadowColor="rgba(15, 23, 42, 0.08)"; break;
            }
        }

        private async Task UpsertSystemSettingAsync(string key, string value)
        {
            var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting == null)
            {
                setting = new SystemSetting { Key = key, Value = value };
                _context.SystemSettings.Add(setting);
            }
            else
            {
                setting.Value = value;
                _context.SystemSettings.Update(setting);
            }
            await _context.SaveChangesAsync();
        }

        private async Task<HashSet<string>> GetInstalledDockerModelNamesAsync()
        {
            var processInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processInfo.ArgumentList.Add("model");
            processInfo.ArgumentList.Add("ls");

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }

        private static SqlConnectionStringBuilder BuildConnectionStringBuilder(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString);
            }
            catch
            {
                return new SqlConnectionStringBuilder
                {
                    DataSource = ".",
                    InitialCatalog = "ServiceOpsAI",
                    IntegratedSecurity = true,
                    TrustServerCertificate = true,
                    Encrypt = false
                };
            }
        }

        private static RuntimeDatabaseTarget BuildSqlServerTarget(string connectionString) => new()
        {
            Provider = RuntimeDatabaseProvider.SqlServer,
            ConnectionString = connectionString.Trim()
        };

        private async Task<List<SqlServerCandidateInfo>> DiscoverSqlServerCandidatesAsync()
        {
            var machineName = Environment.MachineName;
            var candidateNames = new[]
            {
                ".",
                "localhost",
                machineName,
                @".\\SQLEXPRESS",
                @"localhost\\SQLEXPRESS",
                $@"{machineName}\\SQLEXPRESS",
                @"(localdb)\\MSSQLLocalDB"
            }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            var candidates = new List<SqlServerCandidateInfo>();

            foreach (var candidateName in candidateNames)
            {
                try
                {
                    var probe = await ProbeSqlServerAsync(candidateName, useSqlAuthentication: false, userName: "", password: "");
                    candidates.Add(new SqlServerCandidateInfo
                    {
                        ServerName = candidateName,
                        Edition = probe.Edition,
                        ProductVersion = probe.ProductVersion,
                        EngineEdition = probe.EngineEdition
                    });
                }
                catch
                {
                    // Ignore unreachable candidates and only surface working SQL Server instances.
                }
            }

            return candidates;
        }

        private static string BuildSqlConnectionString(
            string serverName,
            string databaseName,
            bool useSqlAuthentication,
            string userName,
            string password,
            bool trustServerCertificate = true,
            bool encrypt = false,
            int timeoutSeconds = 5)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = serverName.Trim(),
                InitialCatalog = string.IsNullOrWhiteSpace(databaseName) ? "master" : databaseName.Trim(),
                IntegratedSecurity = !useSqlAuthentication,
                TrustServerCertificate = trustServerCertificate,
                Encrypt = encrypt,
                ConnectTimeout = timeoutSeconds,
                MultipleActiveResultSets = false
            };

            if (useSqlAuthentication)
            {
                builder.UserID = userName?.Trim() ?? "";
                builder.Password = password ?? "";
            }

            return builder.ConnectionString;
        }

        private async Task<SqlServerProbeResult> ProbeSqlServerAsync(string serverName, bool useSqlAuthentication, string userName, string password)
        {
            var connectionString = BuildSqlConnectionString(serverName, "master", useSqlAuthentication, userName, password, timeoutSeconds: 3);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var probe = new SqlServerProbeResult
            {
                ServerName = serverName
            };

            const string sql = @"
SELECT
    CAST(SERVERPROPERTY('Edition') AS nvarchar(256)) AS Edition,
    CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS ProductVersion,
    CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)) AS ProductLevel,
    CAST(SERVERPROPERTY('EngineEdition') AS int) AS EngineEdition;

SELECT name
FROM sys.databases
WHERE state_desc = 'ONLINE'
ORDER BY name;";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                probe.Edition = reader["Edition"]?.ToString() ?? "Unknown";
                probe.ProductVersion = reader["ProductVersion"]?.ToString() ?? "Unknown";
                probe.ProductLevel = reader["ProductLevel"]?.ToString() ?? "";
                probe.EngineEdition = MapEngineEdition(reader["EngineEdition"] as int? ?? 0);
            }

            if (await reader.NextResultAsync())
            {
                while (await reader.ReadAsync())
                {
                    var databaseName = reader["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(databaseName))
                    {
                        probe.Databases.Add(databaseName);
                    }
                }
            }

            return probe;
        }

        private static string MapEngineEdition(int engineEdition) => engineEdition switch
        {
            1 => "Personal or Desktop",
            2 => "Standard",
            3 => "Enterprise",
            4 => "Express",
            5 => "Azure SQL Database",
            6 => "Azure Synapse",
            8 => "Azure SQL Managed Instance",
            9 => "Azure SQL Edge",
            11 => "Serverless SQL Pool",
            12 => "Microsoft Fabric SQL Database",
            _ => $"Unknown ({engineEdition})"
        };

        private void StoreTempData<T>(string key, T value)
        {
            TempData[key] = JsonSerializer.Serialize(value);
        }

        private T? DeserializeTempData<T>(string key)
        {
            var json = TempData.Peek(key) as string;
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json);
        }

        private async Task ActivateRuntimeTargetAsync(RuntimeDatabaseTarget target, bool runMigrations)
        {
            var previousTarget = _runtimeDatabaseTargetService.GetCurrent();

            try
            {
                _runtimeDatabaseTargetService.SetCurrent(target);

                using var scope = _serviceProvider.CreateScope();
                var scopedServices = scope.ServiceProvider;
                var dbContext = scopedServices.GetRequiredService<ApplicationDbContext>();

                if (runMigrations)
                {
                    await dbContext.Database.MigrateAsync();
                    var scopedUserManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var scopedRoleManager = scopedServices.GetRequiredService<RoleManager<IdentityRole>>();
                    await DbSeeder.InitializeCoreAsync(scopedServices, scopedUserManager, scopedRoleManager);
                    return;
                }

                if (!await dbContext.Database.CanConnectAsync())
                {
                    throw new InvalidOperationException("The selected database target is not reachable.");
                }

                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                if (pendingMigrations.Any())
                {
                    throw new InvalidOperationException("The selected database target is not initialized yet. Run migration first, then activate it.");
                }
            }
            catch
            {
                _runtimeDatabaseTargetService.SetCurrent(previousTarget);
                throw;
            }
        }

        public sealed class SqlServerCandidateInfo
        {
            public string ServerName { get; set; } = "";
            public string Edition { get; set; } = "";
            public string ProductVersion { get; set; } = "";
            public string EngineEdition { get; set; } = "";
        }

        public sealed class SqlServerProbeResult
        {
            public string ServerName { get; set; } = "";
            public string Edition { get; set; } = "";
            public string ProductVersion { get; set; } = "";
            public string ProductLevel { get; set; } = "";
            public string EngineEdition { get; set; } = "";
            public List<string> Databases { get; set; } = new();
        }
        // ─── External API Management ───────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveExternalApi(ExternalApiSetting model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    if (model.Id > 0)
                    {
                        var existing = await _context.ExternalApiSettings.FindAsync(model.Id);
                        if (existing == null) return NotFound();

                        existing.Title = model.Title;
                        existing.Endpoint = model.Endpoint;
                        existing.Description = model.Description;
                        existing.IsActive = model.IsActive;
                        _context.ExternalApiSettings.Update(existing);
                        TempData["Success"] = $"API Hub '{model.Title}' updated successfully.";
                    }
                    else
                    {
                        _context.ExternalApiSettings.Add(model);
                        TempData["Success"] = $"New API Hub '{model.Title}' integrated into the chatbot fabric.";
                    }
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"API Integration Error: {ex.Message}";
                }
            }
            else
            {
                var errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                TempData["Error"] = $"Validation Collision: {errors}";
            }

            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteExternalApi(int id)
        {
            var api = await _context.ExternalApiSettings.FindAsync(id);
            if (api == null) return NotFound();

            _context.ExternalApiSettings.Remove(api);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"API Hub '{api.Title}' has been disconnected.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleExternalApi(int id)
        {
            var api = await _context.ExternalApiSettings.FindAsync(id);
            if (api == null) return NotFound();

            api.IsActive = !api.IsActive;
            await _context.SaveChangesAsync();

            var status = api.IsActive ? "Activated" : "Deactivated";
            TempData["Success"] = $"API Hub '{api.Title}' {status}.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        // ─── Copilot Tool Management ─────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCopilotTool(CopilotToolDefinition tool)
        {
            // Basic validation
            if (string.IsNullOrWhiteSpace(tool.ToolKey))
            {
                ModelState.AddModelError(nameof(tool.ToolKey), "Routing Key is required.");
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(tool.ToolKey, @"^[a-zA-Z0-9_\-]+$"))
            {
                ModelState.AddModelError(nameof(tool.ToolKey), "Routing Key must be alphanumeric (underscores/dashes allowed).");
            }

            if (tool.ToolType == "External")
            {
                if (string.IsNullOrWhiteSpace(tool.EndpointUrl))
                {
                    ModelState.AddModelError(nameof(tool.EndpointUrl), "Endpoint URL is required for external tools.");
                }
                else if (!Uri.TryCreate(tool.EndpointUrl, UriKind.Absolute, out _))
                {
                    ModelState.AddModelError(nameof(tool.EndpointUrl), "Endpoint URL must be a valid absolute URI.");
                }
            }

            // Auto-generate Test Prompt if missing
            if (string.IsNullOrWhiteSpace(tool.TestPrompt))
            {
                tool.TestPrompt = BuildGeneratedToolPrompt(tool);
            }

            if (ModelState.IsValid)
            {
                try
                {
                    await _toolRegistry.SaveAsync(tool);
                    TempData["Success"] = $"Tool '{tool.Title}' saved successfully.";
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Failed to save tool: {ex.Message}";
                }
            }
            else
            {
                var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage;
                TempData["Error"] = $"Validation failed: {firstError ?? "Invalid configuration."}";
            }
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleCopilotTool(int id, bool isEnabled)
        {
            await _toolRegistry.ToggleAsync(id, isEnabled);
            TempData["Success"] = "Tool visibility updated.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCopilotTool(int id)
        {
            await _toolRegistry.DeleteAsync(id);
            TempData["Success"] = "Tool removed from registry.";
            return RedirectToAction(nameof(Index), new { tab = "chatbot" });
        }

        private static string BuildGeneratedToolPrompt(CopilotToolDefinition tool)
        {
            if (!string.IsNullOrWhiteSpace(tool.QueryExtractionHint))
            {
                return $"{tool.Title}: {tool.QueryExtractionHint.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                return $"{tool.Title}: {tool.Description.Trim()}";
            }

            return tool.Title;
        }
    }
}
