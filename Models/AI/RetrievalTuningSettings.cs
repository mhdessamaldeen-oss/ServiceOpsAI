namespace AISupportAnalysisPlatform.Models.AI
{
    // Single tunable POCO for ALL retrieval knobs across the platform — ticket
    // similarity AND knowledge-base RAG AND the Tier-2 reranker / fusion / grounding
    // pipeline. Persisted as one JSON blob in SystemSettings (Key="RetrievalTuningSettings");
    // edited from Views/AiAnalysis/Benchmark.cshtml. Never read from appsettings.json.
    public class RetrievalTuningSettings
    {
        // ── Ticket-similarity hybrid weights (used by SemanticSearchService) ──
        public float VectorWeight { get; set; } = 0.82f;
        public float LexicalWeight { get; set; } = 0.18f;
        public float MixedVectorWeight { get; set; } = 0.88f;
        public float MixedLexicalWeight { get; set; } = 0.12f;

        // ── Ticket-similarity threshold + language boosts ──
        public float BaseThreshold { get; set; } = 0.18f;
        public float SameLanguageBoost { get; set; } = 0.03f;
        public float MixedScriptBoost { get; set; } = 0.015f;

        // ── Knowledge-base RAG hybrid weights (used by KnowledgeBaseRagService) ──
        // Previously hardcoded 0.78/0.22 in C#. Now lifted into the same UI flow as
        // ticket retrieval so operators can tune them per deployment.
        public float KbVectorWeight { get; set; } = 0.78f;
        public float KbLexicalWeight { get; set; } = 0.22f;
        public float KbBaseThreshold { get; set; } = 0.18f;

        // ── Field-inclusion toggles for ticket embedding ──
        public bool IncludeComments { get; set; } = true;
        public bool IncludeAttachments { get; set; } = true;
        public bool IncludeTechnicalAssessment { get; set; } = true;
        public bool IncludeResolutionSummary { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true;

        // ── Tier-2 RAG quality knobs (BM25 + RRF + reranker + grounding audit) ──
        // BM25 path: when off, scoring falls back to today's weighted-sum behavior.
        public bool EnableBm25 { get; set; } = true;
        // Reciprocal Rank Fusion k-constant: higher = flatter rank weighting.
        public int RrfK { get; set; } = 60;

        // Reranker: cross-encoder pass over top-N fused candidates.
        // EnableReranker = false until a reranker model is wired in the provider layer;
        // toggling it on without a registered reranker is a no-op (logged).
        public bool EnableReranker { get; set; } = false;
        public int RerankerTopN { get; set; } = 50;

        // Grounding audit: post-generation LLM call that verifies each recommendation
        // claim cites evidence appearing in the retrieved chunks. Downgrades
        // evidenceStrength when unsupported.
        public bool EnableGroundingAudit { get; set; } = true;
        // 0.0 = lenient ("close enough"), 1.0 = strict ("exact citation required").
        public float GroundingStrictness { get; set; } = 0.6f;
    }
}
