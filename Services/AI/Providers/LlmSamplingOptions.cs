namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// Provider-layer per-call sampling overrides (temperature / seed). Mirrors the AnalystAgent
    /// abstraction of the same name but lives in the host AI-provider namespace so a concrete
    /// provider (e.g. <see cref="OllamaAiProvider"/>) can accept it WITHOUT taking a dependency on
    /// the AnalystAgent module. The HostBridge translates the AnalystAgent record into this DTO.
    /// Both fields optional: a null field falls back to the provider's configured default, so a
    /// null instance is byte-identical to the legacy request.
    /// </summary>
    public sealed record LlmSamplingOptions(double? Temperature, int? Seed);
}
