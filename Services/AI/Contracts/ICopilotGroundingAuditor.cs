using ServiceOpsAI.Models.AI;
 
namespace ServiceOpsAI.Services.AI.Contracts
{
    public interface ICopilotGroundingAuditor
    {
        /// <summary>
        /// Verifies the generated answer against retrieved evidence to detect hallucinations.
        /// </summary>
        Task<CopilotGroundingResult> VerifyAsync(
            string question,
            string answer,
            CopilotExecutionResult execution,
            CancellationToken cancellationToken = default);
    }
}
