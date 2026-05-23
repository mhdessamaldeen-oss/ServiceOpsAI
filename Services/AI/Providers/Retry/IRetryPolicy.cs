namespace AISupportAnalysisPlatform.Services.AI.Providers.Retry
{
    /// <summary>
    /// Wraps a single LLM call with provider-specific retry behavior. The provider hands the policy
    /// a thunk that performs ONE attempt; the policy decides whether to invoke it again based on
    /// the failure shape (HTTP 429, provider-specific signals, etc.).
    /// </summary>
    public interface IRetryPolicy
    {
        Task<AiProviderResult> ExecuteAsync(
            Func<Task<AiProviderResult>> attempt,
            CancellationToken cancellationToken = default);
    }
}
