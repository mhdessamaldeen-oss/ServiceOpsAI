using ServiceOpsAI.Enums;
using ServiceOpsAI.Models.DTOs;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// Thin per-workload wrapper around any <see cref="IAiProvider"/>. The factory returns this
    /// instead of the raw provider so each workload (Copilot / Classifier / Analysis / Rag) gets
    /// its own model-name resolution AND its calls flow through with the correct model — even
    /// when several workloads route to the same underlying provider (e.g. all four pointed at
    /// Ollama, with three different model assignments).
    ///
    /// <para><b>Why a wrapper, not interface change:</b> The <see cref="IAiProvider"/> contract
    /// is shared by every backend (Ollama / OpenAI / Gemini / Groq / Cloud) and most of them only
    /// support ONE model per credential. Adding a workload parameter to every method on the
    /// interface would force every implementation to either implement it or no-op. The wrapper
    /// confines the workload-awareness to the single provider that benefits (Ollama today; can
    /// extend later if Docker etc. grow multi-model support).</para>
    ///
    /// <para>For non-Ollama providers, the wrapper just exposes the inner provider's defaults —
    /// the workload model dropdown is stored in DB (so the UI works) but does NOT yet override
    /// the actual API model for OpenAI/Gemini/Groq/Cloud. Reason: each cloud provider's HTTP
    /// request body has the model name baked into multiple places (auth scope, request body,
    /// error messages, retry logic), so the override needs a per-provider patch. This is on the
    /// roadmap as <em>Gap 8 — extended</em>; the right pattern is a sibling
    /// <c>IModelOverridable</c> interface that providers opt into. Until then, use the cloud
    /// provider's own tab (e.g. OpenAI tab → Model field) to set its single model. The Ollama
    /// path is the assessment-relevant one and is fully wired.</para>
    /// </summary>
    public sealed class WorkloadAwareProvider : IAiProvider
    {
        private readonly IAiProvider _inner;
        private readonly AiWorkloadType _workload;

        public WorkloadAwareProvider(IAiProvider inner, AiWorkloadType workload)
        {
            _inner = inner;
            _workload = workload;
        }

        /// <summary>The workload-resolved model name. For Ollama this consults the per-workload
        /// override settings (<c>AiCopilotModel</c>, <c>AiRagModel</c>, etc.) and falls back to
        /// the provider's default. For other providers it's just the provider's default.</summary>
        public string ModelName =>
            _inner is OllamaAiProvider ollama
                ? ollama.GetModelNameForWorkload(_workload)
                : _inner.ModelName;

        public int ContextCapacity => _inner.ContextCapacity;
        public AiProviderType ProviderType => _inner.ProviderType;

        public Task<AiProviderResult> GenerateAsync(string prompt) =>
            _inner is OllamaAiProvider ollama
                ? ollama.GenerateAsync(prompt, modelOverride: ollama.GetModelNameForWorkload(_workload))
                : _inner.GenerateAsync(prompt);

        /// <summary>Generate with optional per-call sampling overrides (temperature / seed) for the
        /// self-consistency path. Only Ollama applies them today (per-call model override is already
        /// Ollama-only); other providers fall through to plain <see cref="GenerateAsync(string)"/>,
        /// the same documented compromise as the model override. A null <paramref name="sampling"/>
        /// is byte-identical to <see cref="GenerateAsync(string)"/>.</summary>
        public Task<AiProviderResult> GenerateAsync(string prompt, LlmSamplingOptions? sampling) =>
            _inner is OllamaAiProvider ollama
                ? ollama.GenerateAsync(prompt, modelOverride: ollama.GetModelNameForWorkload(_workload),
                    expectJson: false, sampling: sampling)
                : _inner.GenerateAsync(prompt);

        /// <summary>Generate with constrained-JSON output. Used by the classifier / planner paths
        /// that expect a structured JSON plan back. For Ollama this sets <c>format:"json"</c> in
        /// the request body so the model is forced to emit a syntactically valid JSON object —
        /// drops most parse-recovery code in <c>JsonPlanParser</c>. For non-Ollama providers we
        /// fall through to plain <see cref="GenerateAsync(string)"/> (cloud providers each need
        /// their own JSON-mode flag wired separately when we get there).</summary>
        public Task<AiProviderResult> GenerateJsonAsync(string prompt) =>
            _inner is OllamaAiProvider ollama
                ? ollama.GenerateAsync(prompt, modelOverride: ollama.GetModelNameForWorkload(_workload), expectJson: true)
                : _inner.GenerateAsync(prompt);

        public Task<float[]> GetEmbeddingAsync(string text) =>
            _inner is OllamaAiProvider ollama
                ? ollama.GetEmbeddingAsync(text, modelOverride: ollama.GetModelNameForWorkload(_workload))
                : _inner.GetEmbeddingAsync(text);

        public Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync() =>
            _inner.ValidateConfigurationAsync();

        public Task<List<AiModelDto>> GetInstalledModelsAsync() =>
            _inner.GetInstalledModelsAsync();
    }
}
