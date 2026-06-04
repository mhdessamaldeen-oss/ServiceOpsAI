namespace ServiceOpsAI.Services.AI.Providers.Roles
{
    /// <summary>
    /// Per-stage LLM role identifier. Each new AnalystAgent pipeline stage that needs
    /// an LLM call declares its role here; the <see cref="IRoleBoundLlmClientFactory"/>
    /// resolves it to a concrete <see cref="AnalystAgent.Abstractions.ILlmClient"/>
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

        // 2026-06-03 — consolidated to the 4 roles the live pipeline actually calls. The dead roles
        // (SchemaLinker, StructuralCueParser, SelfCorrector, Paraphraser, Frontier, SyntheticGenerator)
        // were removed: schema-linking is done by the embedding retrievers (no LLM call), self-correct
        // is a retry on the SqlGenerator role, and the offline/escalation roles were never wired.

        /// <summary>Compound-question splitter (<c>LlmDecomposer</c>). Mid-size model is plenty —
        /// it's a sentence-segmentation task, not generation.</summary>
        Decomposer = 8,

        /// <summary>Natural-language explanation of result rows (<c>LlmExplainer</c>) and the
        /// coverage-check verifier (<c>CoverageChecker</c>). Mid-size chat-tuned model is fine.</summary>
        Explainer = 9,
    }
}
