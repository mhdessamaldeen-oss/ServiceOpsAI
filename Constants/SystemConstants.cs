namespace ServiceOpsAI.Constants
{
    public static class RoleNames
    {
        public const string Admin = "Admin";
        public const string SupportAgent = "SupportAgent";
        public const string EndUser = "EndUser";
        public const string Viewer = "Viewer";
        public const string Client = "Client";
    }

    public static class TicketStatusNames
    {
        public const string New = "New";
        public const string Open = "Open";
        public const string InProgress = "In Progress";
        public const string Pending = "Pending";
        public const string Resolved = "Resolved";
        public const string Closed = "Closed";
        public const string Rejected = "Rejected";
    }

    public static class TicketPriorityNames
    {
        public const string Low = "Low";
        public const string Medium = "Medium";
        public const string High = "High";
        public const string Critical = "Critical";
    }

    public static class TicketSourceNames
    {
        public const string WebPortal = "Web Portal";
        public const string Email = "Email";
        public const string Phone = "Phone";
        public const string InternalRequest = "Internal Request";
        public const string DefaultFallback = "Portals";
    }

    public static class ReferenceDataTypes
    {
        public const string Category = "category";
        public const string Priority = "priority";
        public const string Status = "status";
        public const string Source = "source";
        public const string ServiceType = "servicetype";
        public const string ComplaintType = "complainttype";
        public const string ResolutionType = "resolutiontype";
    }

    public static class SettingKeys
    {
        public const string AiActiveProvider = "AiActiveProvider";
        public const string AiAnalysisProvider = "AiAnalysisProvider";
        public const string AiRagProvider = "AiRagProvider";
        public const string AiCopilotProvider = "AiCopilotProvider";
        public const string AiClassifierProvider = "AiClassifierProvider";
        public const string DockerModel = "DockerModel";
        public const string LegacyDockerModelKey = "OllamaModel";
        public const string DockerBaseUrl = "AiDockerBaseUrl";
        public const string DockerTimeoutSeconds = "AiDockerTimeoutSeconds";
        public const string DockerMaxPromptChars = "AiDockerMaxPromptChars";
        public const string DockerMaxPromptTokens = "AiDockerMaxPromptTokens";
        public const string DockerTemperature = "AiDockerTemperature";
        public const string DockerNumCtx = "AiDockerNumCtx";
        public const string DockerNumPredict = "AiDockerNumPredict";
        public const string OllamaBaseUrl = "AiOllamaBaseUrl";
        public const string OllamaModelConfig = "AiOllamaModel";
        /// <summary>Dedicated embedding-model name (e.g. <c>bge-m3</c>). Separate from the
        /// chat/classifier model so the user can pick a 7B chat model for SQL planning AND a
        /// purpose-built embedding model for semantic search / RAG / entity matching — both
        /// served by the same local Ollama instance. Falls back to <see cref="OllamaModelConfig"/>
        /// when unset (single-model installs still work).</summary>
        public const string OllamaEmbeddingModel = "AiOllamaEmbeddingModel";

        // Per-workload model overrides — one slot per workload (Copilot / Analysis / Rag).
        // Each is OPTIONAL: when unset, the workload's provider uses its own default model
        // (Ollama ModelName for chat workloads, OllamaEmbeddingModel for Rag). When set, the
        // factory's WorkloadAwareProvider wrapper overrides ModelName for that specific call.
        // This is the "I want Copilot on qwen2.5:7b AND RAG on bge-m3, both via Ollama" knob —
        // the workload routing UI exposes them as a Model field next to each Provider dropdown.
        public const string CopilotWorkloadModel = "AiCopilotModel";
        public const string AnalysisWorkloadModel = "AiAnalysisModel";
        public const string RagWorkloadModel = "AiRagModel";
        /// <summary>Workload model for the intent router/classifier. Runs BEFORE the Copilot SQL
        /// generator. Keeping it on the same provider+model as Copilot is the safe default; can be
        /// downgraded to a smaller faster model (e.g. <c>qwen2.5:3b</c>) once accuracy is measured.</summary>
        public const string ClassifierWorkloadModel = "AiClassifierModel";
        public const string OllamaTimeoutSeconds = "AiOllamaTimeoutSeconds";
        public const string OllamaMaxPromptChars = "AiOllamaMaxPromptChars";
        public const string OllamaTemperature = "AiOllamaTemperature";
        // Tokens of input context the model is told to allocate (Ollama `num_ctx`). qwen3.5
        // supports up to 32768; llama3.2 up to 131072. Default 4096 is too small for the 21K-char
        // classifier prompt — anything below ~8192 will silently truncate input → empty output.
        public const string OllamaContextWindow = "AiOllamaContextWindow";
        public const string DefaultTheme = "DefaultTheme";
        public const string OpenAiApiKey = "AiOpenAiApiKey";
        public const string OpenAiModel = "AiOpenAiModel";
        public const string OpenAiBaseUrl = "AiOpenAiBaseUrl";
        public const string OpenAiTimeoutSeconds = "AiOpenAiTimeoutSeconds";
        public const string OpenAiMaxPromptChars = "AiOpenAiMaxPromptChars";
        public const string OpenAiTemperature = "AiOpenAiTemperature";
        public const string OpenAiMaxTokens = "AiOpenAiMaxTokens";
        public const string OpenAiOrganizationId = "AiOpenAiOrganizationId";
        public const string OpenAiProjectId = "AiOpenAiProjectId";
        public const string CloudEndpoint = "AiCloudEndpoint";
        public const string CloudApiKey = "AiCloudApiKey";
        public const string CloudModel = "AiCloudModel";
        public const string CloudDeploymentName = "AiCloudDeploymentName";
        public const string CloudTimeoutSeconds = "AiCloudTimeoutSeconds";
        public const string CloudMaxPromptChars = "AiCloudMaxPromptChars";
        public const string CloudTemperature = "AiCloudTemperature";
        public const string CloudMaxTokens = "AiCloudMaxTokens";
        public const string CloudApiVersion = "AiCloudApiVersion";
        public const string CloudAuthHeaderName = "AiCloudAuthHeaderName";
        public const string CloudUseBearerToken = "AiCloudUseBearerToken";
        public const string LocalAiBaseUrl = "AiLocalAiBaseUrl";
        public const string LocalAiModel = "AiLocalAiModel";
        public const string LocalAiApiKey = "AiLocalAiApiKey";
        public const string LocalAiTimeoutSeconds = "AiLocalAiTimeoutSeconds";
        public const string LocalAiMaxPromptChars = "AiLocalAiMaxPromptChars";
        public const string LocalAiTemperature = "AiLocalAiTemperature";
        public const string GeminiApiKey = "AiGeminiApiKey";
        public const string GeminiModel = "AiGeminiModel";
        public const string GeminiTemperature = "AiGeminiTemperature";
        public const string GeminiMaxTokens = "AiGeminiMaxTokens";
        // Rate-limit handling — added so users can configure retry behavior from the UI without code changes.
        // Default: retry once on HTTP 429 after 30s; assessment runner waits 7s between cases (free tier = 10 RPM).
        public const string GeminiRetryOn429 = "AiGeminiRetryOn429";
        public const string GeminiRetryDelayMs = "AiGeminiRetryDelayMs";
        public const string GeminiRetryMaxAttempts = "AiGeminiRetryMaxAttempts";
        public const string GeminiAssessmentDelayMs = "AiGeminiAssessmentDelayMs";

        /// <summary>Default <c>MaxLatencyMs</c> applied to assessment cases that don't set the field
        /// explicitly. Lets operators tune the budget per deployment (e.g. a fast cloud LLM can run
        /// at 15s; a local 7B model on modest hardware needs 90-120s). Default 30000 (30s) preserves
        /// historic behaviour; raise via SystemSettings UI when running against slow local models.</summary>
        public const string CopilotAssessmentDefaultLatencyMs = "CopilotAssessmentDefaultLatencyMs";

        // Groq settings — OpenAI-compatible API on Groq's LPU infrastructure (free tier ~30 RPM, daily TPD cap).
        // Default base url uses the OpenAI-compatible path so any OpenAI client reaches it unchanged.
        public const string GroqApiKey = "AiGroqApiKey";
        public const string GroqModel = "AiGroqModel";
        public const string GroqTemperature = "AiGroqTemperature";
        public const string GroqMaxTokens = "AiGroqMaxTokens";
        public const string GroqRetryOn429 = "AiGroqRetryOn429";
        public const string GroqRetryDelayMs = "AiGroqRetryDelayMs";
        public const string GroqRetryMaxAttempts = "AiGroqRetryMaxAttempts";

        // Per-provider curated "allowed models" lists. Each value is a comma-separated
        // list of model identifiers the user has explicitly added in the provider tab.
        // The workload-routing model dropdowns (Analysis / RAG / Copilot)
        // source their options from these lists — not from the live discovery endpoints —
        // so the user can keep the dropdown short and meaningful (1–3 models per provider).
        public const string OllamaAllowedModels = "AiOllamaAllowedModels";
        public const string DockerAllowedModels = "AiDockerAllowedModels";
        public const string OpenAiAllowedModels = "AiOpenAiAllowedModels";
        public const string GeminiAllowedModels = "AiGeminiAllowedModels";
        public const string GroqAllowedModels = "AiGroqAllowedModels";
        public const string CloudAllowedModels = "AiCloudAllowedModels";

        // SuperAdminCopilot runtime settings. These are intentionally separate from AI provider
        // routing: provider routing decides which model runs; these keys decide when Copilot may
        // use the model, what schema it may see, and how conservative it should be.
        public const string CopilotTableExposureMode = "CopilotTableExposureMode";
        public const string CopilotBlockedTables = "CopilotBlockedTables";
        public const string CopilotBlockedTablePatterns = "CopilotBlockedTablePatterns";
        public const string CopilotBlockedColumns = "CopilotBlockedColumns";
        public const string CopilotSensitiveColumns = "CopilotSensitiveColumns";
        public const string CopilotRetrieverTopK = "CopilotRetrieverTopK";
        public const string CopilotSchemaPromptStrategy = "CopilotSchemaPromptStrategy";
        public const string CopilotUseVectorRetriever = "CopilotUseVectorRetriever";
        public const string CopilotUsePastQuestionRag = "CopilotUsePastQuestionRag";
        public const string CopilotFewShotTopK = "CopilotFewShotTopK";
        public const string CopilotUseLlmExplainer = "CopilotUseLlmExplainer";
        public const string CopilotMaxLlmCallsPerQuestion = "CopilotMaxLlmCallsPerQuestion";
        public const string CopilotMaxSelfCorrectionRetries = "CopilotMaxSelfCorrectionRetries";
        public const string CopilotLlmCallTimeoutSeconds = "CopilotLlmCallTimeoutSeconds";
        public const string CopilotMaxQuestionWallClockSeconds = "CopilotMaxQuestionWallClockSeconds";
        public const string CopilotMaxRows = "CopilotMaxRows";
        public const string CopilotCommandTimeoutSeconds = "CopilotCommandTimeoutSeconds";
        public const string CopilotEnableSchemaIntrospection = "CopilotEnableSchemaIntrospection";
        public const string CopilotRestrictMetadataToConfiguredEntities = "CopilotRestrictMetadataToConfiguredEntities";
        public const string CopilotEnableResultCache = "CopilotEnableResultCache";
        public const string CopilotResultCacheTtlSeconds = "CopilotResultCacheTtlSeconds";
        public const string CopilotEnableCostGate = "CopilotEnableCostGate";
        public const string CopilotMaxEstimatedQueryCost = "CopilotMaxEstimatedQueryCost";
        public const string CopilotAmbiguityClarificationThreshold = "CopilotAmbiguityClarificationThreshold";
        public const string CopilotResolverMinConfidence = "CopilotResolverMinConfidence";
    }

    public static class AiProviderNames
    {
        public const string DockerLocal = "DockerLocal";
        public const string LegacyDockerLocalAlias = "LocalOllama";
        public const string OpenAI = "OpenAI";
        public const string Cloud = "Cloud";
        public const string LocalAI = "LocalAI";
        public const string Gemini = "Gemini";
        public const string Ollama = "Ollama";
        public const string Groq = "Groq";
    }

    public static class CopilotSurfaces
    {
        public const string Default = "default";
        public const string Assessment = "assessment";
        public const string Reports = "reports";
    }
}

