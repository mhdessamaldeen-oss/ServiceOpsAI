namespace ServiceOpsAI.Services.AI.Providers.Roles
{
    /// <summary>
    /// Per-role LLM binding collection. Bound to the <c>Ai:RoleBindings</c> section of
    /// appsettings.json so users can configure each stage independently without touching the
    /// existing <see cref="AiProviderSettings"/> workload slots.
    ///
    /// <para><b>Why a sibling section, not a modification of AiProviderSettings:</b> the existing
    /// settings class is on the live execution path. Adding a sibling section keeps the change
    /// strictly additive — old code keeps reading the old section and the new role-bound code
    /// reads the new section. Once activated, both can co-exist forever or one can be retired.</para>
    ///
    /// <para><b>Inheritance rule (implemented by <see cref="IRoleBoundLlmClientFactory"/>):</b>
    /// if a role's <see cref="AiRoleBinding.Provider"/> is empty, the factory falls back to the
    /// matching workload provider from <see cref="AiProviderSettings"/>. If the binding's
    /// <see cref="AiRoleBinding.Model"/> is empty, the factory falls back to the provider's
    /// default model. This lets a user override only what's needed — e.g. point QuerySpecComposer
    /// at a different model on the same Ollama instance, with one line of config.</para>
    /// </summary>
    public class AiRoleBindings
    {
        public const string SectionName = "Ai:RoleBindings";

        public AiRoleBinding Classifier { get; set; } = new();
        public AiRoleBinding QuerySpecComposer { get; set; } = new();
        public AiRoleBinding SchemaLinker { get; set; } = new();
        public AiRoleBinding StructuralCueParser { get; set; } = new();
        public AiRoleBinding SelfCorrector { get; set; } = new();
        public AiRoleBinding Paraphraser { get; set; } = new();
        public AiRoleBinding Frontier { get; set; } = new();
        public AiRoleBinding SyntheticGenerator { get; set; } = new();
        public AiRoleBinding Decomposer { get; set; } = new();
        public AiRoleBinding Explainer { get; set; } = new();

        /// <summary>Look up the binding for a given <see cref="AiRole"/>. Returns an empty
        /// binding (which the factory treats as "inherit everything from defaults") if the role
        /// isn't recognised.</summary>
        public AiRoleBinding Get(AiRole role) => role switch
        {
            AiRole.Classifier => Classifier,
            AiRole.QuerySpecComposer => QuerySpecComposer,
            AiRole.SchemaLinker => SchemaLinker,
            AiRole.StructuralCueParser => StructuralCueParser,
            AiRole.SelfCorrector => SelfCorrector,
            AiRole.Paraphraser => Paraphraser,
            AiRole.Frontier => Frontier,
            AiRole.SyntheticGenerator => SyntheticGenerator,
            AiRole.Decomposer => Decomposer,
            AiRole.Explainer => Explainer,
            _ => new AiRoleBinding(),
        };
    }

    /// <summary>
    /// One role-to-model binding. Every field is optional; empty values inherit from the
    /// existing workload-level <see cref="AiProviderSettings"/> defaults.
    /// </summary>
    public class AiRoleBinding
    {
        /// <summary>Provider name — must match a key the existing <see cref="AiProviderFactory"/>
        /// understands (e.g. <c>"Ollama"</c>, <c>"OpenAI"</c>, <c>"DockerLocal"</c>,
        /// <c>"Cloud"</c>, <c>"LocalAI"</c>). Empty string means "inherit from the matching
        /// workload provider".</summary>
        public string Provider { get; set; } = "";

        /// <summary>Model name (e.g. <c>"qwen2.5-coder:14b"</c>). Empty string means "use
        /// the provider's default Model".</summary>
        public string Model { get; set; } = "";

        /// <summary>Per-role temperature override. Null = inherit from provider default.
        /// Set this low (0.0-0.1) for deterministic tasks like classification, higher (0.3-0.7)
        /// when self-consistency benefits from sample variance.</summary>
        public double? Temperature { get; set; }

        /// <summary>Per-role response token cap. Null = inherit from provider default. Critical
        /// for offline batch roles (Paraphraser, SyntheticGenerator) which need much higher caps
        /// than the live request path.</summary>
        public int? MaxTokens { get; set; }
    }
}
