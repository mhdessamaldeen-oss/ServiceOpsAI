using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI.Contracts
{
    public interface ICopilotToolIntentResolver
    {
        Task<CopilotToolResolution> ResolveAsync(string question, CancellationToken cancellationToken = default);
    }
}
