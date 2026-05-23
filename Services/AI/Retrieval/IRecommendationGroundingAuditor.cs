namespace ServiceOpsAI.Services.AI.Retrieval
{
    /// <summary>
    /// Post-generation hallucination check for the Copilot Recommendation flow.
    /// Verifies that each claim in the produced summary / recommended action is
    /// supported by the retrieved evidence (similar tickets + KB chunks). Used to
    /// downgrade <c>EvidenceStrength</c> when claims aren't actually grounded —
    /// the user-visible "Strong" badge should never appear over a hallucination.
    /// </summary>
    public interface IRecommendationGroundingAuditor
    {
        Task<RecommendationGroundingAudit> AuditAsync(
            string summary,
            string recommendedAction,
            IReadOnlyList<string> evidenceChunks,
            CancellationToken cancellationToken = default);
    }

    public sealed class RecommendationGroundingAudit
    {
        /// <summary>0.0 = no claims grounded; 1.0 = every claim cites retrieved evidence.</summary>
        public double Confidence { get; init; } = 1.0;
        public IReadOnlyList<string> UnsupportedClaims { get; init; } = Array.Empty<string>();
        /// <summary>True when the audit was actually performed (vs disabled / no evidence).</summary>
        public bool WasAudited { get; init; }
        public string Notes { get; init; } = "";
    }
}
