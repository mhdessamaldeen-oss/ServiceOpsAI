namespace AISupportAnalysisPlatform.Services.AI.Providers.Retry
{
    /// <summary>
    /// Default policy for providers that don't need retries (DockerLocal, Ollama, OpenAI proxies, etc.).
    /// The first failure is returned as-is.
    /// </summary>
    public sealed class NoRetryPolicy : IRetryPolicy
    {
        public Task<AiProviderResult> ExecuteAsync(
            Func<Task<AiProviderResult>> attempt,
            CancellationToken cancellationToken = default) => attempt();
    }
}
