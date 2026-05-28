namespace ServiceOpsAI.Services.AI.Providers.Roles
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SuperAdminCopilot.Abstractions;

    /// <summary>
    /// Resolves an <see cref="ILlmClient"/> for a given <see cref="AiRole"/>. The pipeline
    /// stages that need an LLM call ask this factory for the role-appropriate client rather
    /// than depending on the single global <see cref="ILlmClient"/> registration.
    ///
    /// <para><b>Behaviour after activation:</b> each call resolves the binding for the role,
    /// constructs an <see cref="ILlmClient"/> that targets that provider + model (with per-role
    /// temperature / token overrides), and returns it. Unbound roles fall back to the global
    /// default — so existing call sites that haven't migrated to <see cref="For"/> keep working.</para>
    /// </summary>
    public interface IRoleBoundLlmClientFactory
    {
        /// <summary>Resolve the LLM client for the given role.</summary>
        ILlmClient For(AiRole role);

        /// <summary>Diagnostic — returns the binding actually in use for the role (after defaults
        /// have been applied). Useful for the trace sink to record "which model handled this stage".</summary>
        AiRoleBinding GetEffectiveBinding(AiRole role);
    }

    /// <summary>
    /// Phase 0.5 stub. Returns the existing global <see cref="ILlmClient"/> regardless of role.
    /// The post-assessment activation step replaces this with a real implementation that consults
    /// <see cref="AiRoleBindings"/> and constructs per-role clients targeting the resolved
    /// provider + model. Until then the rest of the staged code can compile against
    /// <see cref="IRoleBoundLlmClientFactory"/> without changing live behaviour.
    /// </summary>
    internal sealed class StubRoleBoundLlmClientFactory : IRoleBoundLlmClientFactory
    {
        private readonly ILlmClient _defaultClient;
        private readonly AiRoleBindings _bindings;
        private readonly AiProviderSettings _providerSettings;
        private readonly ILogger<StubRoleBoundLlmClientFactory> _logger;

        public StubRoleBoundLlmClientFactory(
            ILlmClient defaultClient,
            IOptions<AiRoleBindings> bindings,
            IOptions<AiProviderSettings> providerSettings,
            ILogger<StubRoleBoundLlmClientFactory> logger)
        {
            _defaultClient = defaultClient;
            _bindings = bindings.Value;
            _providerSettings = providerSettings.Value;
            _logger = logger;
        }

        public ILlmClient For(AiRole role)
        {
            // Phase 0.5: always return the default ILlmClient. The role binding is recorded
            // (via the effective-binding diagnostic) but does not yet influence which client
            // is constructed. The real per-role provider construction happens in the
            // post-assessment activation step.
            _logger.LogTrace("[RoleFactory:stub] role={Role} -> default ILlmClient", role);
            return _defaultClient;
        }

        public AiRoleBinding GetEffectiveBinding(AiRole role)
        {
            var raw = _bindings.Get(role);

            // Apply inheritance: empty provider falls back to the workload-level setting
            // for whichever workload "looks like" this role. Today's mapping (subject to change
            // when the real factory ships):
            //   - Classifier              → AiProviderSettings.ClassifierProvider
            //   - QuerySpecComposer       → AiProviderSettings.CopilotProvider
            //   - SchemaLinker, StructuralCueParser, SelfCorrector → CopilotProvider (same family)
            //   - Paraphraser, SyntheticGenerator → CopilotProvider (offline override expected)
            //   - Frontier                → CopilotProvider (until a frontier provider slot exists)
            var inheritedProvider = role switch
            {
                AiRole.Classifier => _providerSettings.ClassifierProvider,
                _ => _providerSettings.CopilotProvider,
            };

            return new AiRoleBinding
            {
                Provider = string.IsNullOrWhiteSpace(raw.Provider) ? inheritedProvider : raw.Provider,
                Model = raw.Model, // inheritance of Model needs the actual provider-config lookup
                                   // — left to the real factory; the stub doesn't synthesise it
                Temperature = raw.Temperature,
                MaxTokens = raw.MaxTokens,
            };
        }
    }
}
