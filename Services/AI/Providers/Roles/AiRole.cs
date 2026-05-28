namespace ServiceOpsAI.Services.AI.Providers.Roles
{
    /// <summary>
    /// Per-stage LLM role identifier. Each new SuperAdminCopilot pipeline stage that needs
    /// an LLM call declares its role here; the <see cref="IRoleBoundLlmClientFactory"/>
    /// resolves it to a concrete <see cref="SuperAdminCopilot.Abstractions.ILlmClient"/>
    /// using the per-role binding configured in <see cref="AiRoleBindings"/>.
    ///
    /// <para><b>Why an enum instead of strings:</b> compile-time safety. Renaming a role
    /// becomes a refactor, not a search-and-replace across config files and call sites.
    /// New roles must be added here AND in <see cref="AiRoleBindings"/> — keep them in
    /// sync.</para>
    /// </summary>
    public enum AiRole
    {
        /// <summary>Existing intent classifier — narrow JSON-label task. Small model (3-7B) is plenty.</summary>
        Classifier = 0,

        /// <summary>Existing generator that emits the full QuerySpec. The slot users typically
        /// point at their fine-tuned model. Hardest task — code-specialised model recommended.</summary>
        QuerySpecComposer = 1,

        /// <summary>Phase 2a — narrow "which tables/columns does this question reference?" stage.</summary>
        SchemaLinker = 2,

        /// <summary>Phase 2b — parses brackets / ordering / 'with their X' cues into a typed
        /// display-shape. Tiny task; a small fine-tuned model is ideal.</summary>
        StructuralCueParser = 3,

        /// <summary>Phase 3 — reads error + bad SQL + question, returns corrected SQL.
        /// Same skill as <see cref="QuerySpecComposer"/>; usually inherits its binding.</summary>
        SelfCorrector = 4,

        /// <summary>Phase 1 — offline batch paraphrase generation. Quality matters more than
        /// cost; recommended binding: frontier model (Claude Sonnet, GPT-4-class).</summary>
        Paraphraser = 5,

        /// <summary>Phase 7 — escalation for hard questions. Recommended binding: frontier model.</summary>
        Frontier = 6,

        /// <summary>Phase 0 — offline synthetic Q→SQL generation for growing the eval set.
        /// Same model as <see cref="Paraphraser"/> in practice.</summary>
        SyntheticGenerator = 7,

        /// <summary>Compound-question splitter (<c>LlmDecomposer</c>). Mid-size model is plenty —
        /// it's a sentence-segmentation task, not generation.</summary>
        Decomposer = 8,

        /// <summary>Natural-language explanation of result rows (<c>LlmExplainer</c>) and the
        /// coverage-check verifier (<c>CoverageChecker</c>). Mid-size chat-tuned model is fine.</summary>
        Explainer = 9,
    }
}
