using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Services.AI.Contracts
{
    public interface ICopilotToolIntentResolver
    {
        Task<CopilotToolResolution> ResolveAsync(string question, CancellationToken cancellationToken = default);
    }
}
