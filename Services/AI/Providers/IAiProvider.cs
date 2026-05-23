using AISupportAnalysisPlatform.Enums;
namespace AISupportAnalysisPlatform.Services.AI.Providers
{
    /// <summary>
    /// Core abstraction for all AI providers.
    /// Every provider (local, cloud, OpenAI) must implement this interface.
    /// </summary>
    public interface IAiProvider
    {
        /// <summary>
        /// Human-readable name of the currently active model (e.g. "llama3.2", "gpt-4o-mini").
        /// </summary>
        string ModelName { get; }
        
        /// <summary>
        /// The maximum number of context tokens supported by this model (e.g. 4096, 32768).
        /// </summary>
        int ContextCapacity { get; }

        /// <summary>
        /// The provider type this implementation represents.
        /// </summary>
        AiProviderType ProviderType { get; }

        /// <summary>
        /// Sends a prompt to the provider and returns the generated text.
        /// </summary>
        Task<AiProviderResult> GenerateAsync(string prompt);

        /// <summary>
        /// Converts text into a numerical vector (embedding) for semantic search.
        /// </summary>
        Task<float[]> GetEmbeddingAsync(string text);

        /// <summary>
        /// Validates this provider's configuration and connectivity.
        /// Returns (isValid, errorMessage).
        /// </summary>
        Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync();

        /// <summary>
        /// Retrieves a list of models currently installed/available on the local machine for this provider.
        /// Primarily used for Docker and Ollama.
        /// </summary>
        Task<List<AISupportAnalysisPlatform.Models.DTOs.AiModelDto>> GetInstalledModelsAsync();
    }
}

