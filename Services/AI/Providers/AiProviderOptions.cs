using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Constants;
namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// Root configuration for AI provider selection.
    /// Binds to the "Ai" section in appsettings.json.
    /// </summary>
    public class AiProviderSettings
    {
        public const string SectionName = "Ai";

        /// <summary>
        /// Default fallback provider if a workload-specific provider is not set.
        /// </summary>
        public string ActiveProvider { get; set; } = "DockerLocal";

        /// <summary>
        /// Provider specifically for Analysis workloads (Ticket Summarization, Insights).
        /// </summary>
        public string AnalysisProvider { get; set; } = "";

        /// <summary>
        /// Provider specifically for RAG workloads (Knowledge Base, Semantic Search).
        /// </summary>
        public string RagProvider { get; set; } = "";

        /// <summary>
        /// Provider specifically for Copilot workloads (Chat, Reasoning).
        /// </summary>
        public string CopilotProvider { get; set; } = "";

        /// <summary>
        /// Provider for the Intent Classifier workload (router that classifies inputs
        /// into SQL / Chat / Tool / OutOfScope / Refinement). Runs BEFORE the Copilot
        /// SQL generator so a narrow fine-tune doesn't see non-SQL inputs.
        /// Defaults to whatever Copilot uses if unset.
        /// </summary>
        public string ClassifierProvider { get; set; } = "";

        /// <summary>
        /// Configuration for the local Docker model provider.
        /// </summary>
        public DockerLocalProviderOptions DockerLocal { get; set; } = new();

        /// <summary>
        /// Configuration for the Ollama provider.
        /// </summary>
        public OllamaProviderOptions Ollama { get; set; } = new();

        /// <summary>
        /// Configuration for the OpenAI/GPT-style provider.
        /// </summary>
        public OpenAiProviderOptions OpenAI { get; set; } = new();

        /// <summary>
        /// Configuration for a generic cloud AI provider.
        /// </summary>
        public CloudProviderOptions Cloud { get; set; } = new();

        /// <summary>
        /// Configuration for local OpenAI-compatible providers like LocalAI.
        /// </summary>
        public LocalAiProviderOptions LocalAI { get; set; } = new();

        /// <summary>
        /// Parses the ActiveProvider string into the enum.
        /// Falls back to DockerLocal if unrecognized.
        /// </summary>
        public AiProviderType GetActiveProviderType()
        {
            return ActiveProvider?.Trim() switch
            {
                AiProviderNames.DockerLocal or AiProviderNames.LegacyDockerLocalAlias => AiProviderType.DockerLocal,
                "OpenAI" or "GPT" => AiProviderType.OpenAI,
                "Cloud" => AiProviderType.Cloud,
                "LocalAI" => AiProviderType.LocalAI,
                _ => AiProviderType.DockerLocal
            };
        }
    }

    /// <summary>
    /// Configuration for the local Docker model provider.
    /// </summary>
    public class DockerLocalProviderOptions
    {
        /// <summary>Base URL for the engine (if any). Default: DOCKER_NATIVE</summary>
        public string BaseUrl { get; set; } = "DOCKER_NATIVE";

        /// <summary>Default model name/tag. Can be overridden by SystemSettings DB row.</summary>
        public string Model { get; set; } = "llama3.2";

        /// <summary>Execution timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>Maximum prompt character size before truncation.</summary>
        public int MaxPromptChars { get; set; } = 6000;

        /// <summary>Maximum prompt token estimate.</summary>
        public int MaxPromptTokens { get; set; } = 4000;

        /// <summary>LLM temperature (lower = more deterministic).</summary>
        public double Temperature { get; set; } = 0.1;

        /// <summary>Context window size.</summary>
        public int NumCtx { get; set; } = 8192;

        /// <summary>Maximum tokens to predict.</summary>
        public int NumPredict { get; set; } = 800;
    }

    /// <summary>
    /// Configuration for OpenAI / GPT-style chat completion providers.
    /// </summary>
    public class OpenAiProviderOptions
    {
        /// <summary>API key for authentication. Store in user-secrets or environment variables.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Base URL for the API. Default: https://api.openai.com/v1</summary>
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>Model name (e.g. gpt-4o-mini, gpt-4o, gpt-3.5-turbo).</summary>
        public string Model { get; set; } = "gpt-4o-mini";

        /// <summary>HTTP request timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Maximum prompt character size before truncation.</summary>
        public int MaxPromptChars { get; set; } = 12000;

        /// <summary>LLM temperature.</summary>
        public double Temperature { get; set; } = 0.1;

        /// <summary>Maximum tokens in the response.</summary>
        public int MaxTokens { get; set; } = 1000;

        /// <summary>Optional organization ID.</summary>
        public string? OrganizationId { get; set; }

        /// <summary>Optional project ID.</summary>
        public string? ProjectId { get; set; }
    }

    /// <summary>
    /// Configuration for generic cloud AI provider endpoints.
    /// Supports Azure OpenAI Service, custom hosted endpoints, etc.
    /// </summary>
    public class CloudProviderOptions
    {
        /// <summary>Full endpoint URL for the cloud AI service.</summary>
        public string Endpoint { get; set; } = "";

        /// <summary>API key for authentication. Store in user-secrets or environment variables.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Model or deployment name.</summary>
        public string Model { get; set; } = "";

        /// <summary>HTTP request timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Maximum prompt character size before truncation.</summary>
        public int MaxPromptChars { get; set; } = 12000;

        /// <summary>LLM temperature.</summary>
        public double Temperature { get; set; } = 0.1;

        /// <summary>Maximum tokens in the response.</summary>
        public int MaxTokens { get; set; } = 1000;

        /// <summary>Optional deployment name (e.g. for Azure OpenAI).</summary>
        public string? DeploymentName { get; set; }

        /// <summary>Optional API version (e.g. for Azure OpenAI).</summary>
        public string? ApiVersion { get; set; }

        /// <summary>Auth header name. Default: "api-key" (Azure style). Use "Authorization" for Bearer tokens.</summary>
        public string AuthHeaderName { get; set; } = "api-key";

        /// <summary>If true, sends the API key as a Bearer token in the Authorization header.</summary>
        public bool UseBearerToken { get; set; } = false;
    }

    /// <summary>
    /// Configuration for local OpenAI-compatible engines (LocalAI, LM Studio).
    /// </summary>
    public class LocalAiProviderOptions
    {
        public string BaseUrl { get; set; } = "http://localhost:8080/v1";
        public string Model { get; set; } = "gpt-4";
        public string ApiKey { get; set; } = "ignored";
        public int TimeoutSeconds { get; set; } = 300;
        public int MaxPromptChars { get; set; } = 8000;
        public double Temperature { get; set; } = 0.1;
    }

    /// <summary>
    /// Configuration for the local Ollama provider.
    /// </summary>
    public class OllamaProviderOptions
    {
        public string BaseUrl { get; set; } = "http://localhost:11434/api";
        public string Model { get; set; } = "llama2";

        /// <summary>Dedicated embedding model. Separate from <see cref="Model"/> so the chat /
        /// classifier model (a chat-tuned 7B) can co-exist with a purpose-built embedding model
        /// (bge-m3, all-MiniLM, etc.) on the same Ollama instance. Empty string = fall back to
        /// <see cref="Model"/> (legacy single-model behavior).</summary>
        public string EmbeddingModel { get; set; } = "bge-m3";

        /// <summary>HTTP timeout for Ollama calls in seconds.
        /// <para>• Positive value = wait that many seconds, then cancel.</para>
        /// <para>• <c>0</c> or any non-positive value = NO timeout (wait indefinitely). Set
        /// this when running large local models on modest hardware where a full inference
        /// can legitimately take many minutes.</para>
        /// <para>Default 1800 (30 min) — generous enough for slow CPU inference of 7B–13B
        /// models without being literally infinite. Override via the admin Settings UI
        /// (<c>AiOllamaTimeoutSeconds</c>) for assessment runs that need longer.</para>
        /// </summary>
        public int TimeoutSeconds { get; set; } = 1800;
        public int MaxPromptChars { get; set; } = 8000;
        public double Temperature { get; set; } = 0.1;
    }
}

