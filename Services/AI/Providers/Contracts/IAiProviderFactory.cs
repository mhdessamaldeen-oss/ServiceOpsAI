using AISupportAnalysisPlatform.Enums;

namespace AISupportAnalysisPlatform.Services.AI.Providers;

public interface IAiProviderFactory
{
    IAiProvider GetActiveProvider();
    IAiProvider GetProvider(AiProviderType providerType);
    IAiProvider GetProviderForWorkload(AiWorkloadType workload);
    AiProviderType ActiveProviderType { get; }
    Task<(bool IsValid, string? ErrorMessage)> ValidateActiveProviderAsync();
}
