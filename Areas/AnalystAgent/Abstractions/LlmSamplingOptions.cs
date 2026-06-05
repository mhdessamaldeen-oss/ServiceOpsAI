namespace AnalystAgent.Abstractions;

/// <summary>
/// Per-call LLM sampling overrides for the self-consistency path. When present, these override
/// the provider's default decoding parameters for a SINGLE generation — so the orchestrator can
/// draw several DIVERSE candidates for one question (higher temperature) with a distinct, reproducible
/// seed per draw. Both fields are optional: a null leaves the provider's configured default in place
/// (the byte-identical no-op the flag-OFF path relies on).
/// </summary>
/// <param name="Temperature">Decoding temperature. Higher = more diverse samples. Null → provider default.</param>
/// <param name="Seed">Deterministic RNG seed for this single draw, so the same (prompt, seed) is reproducible
/// and distinct seeds yield distinct candidates. Null → provider default.</param>
public sealed record LlmSamplingOptions(double? Temperature, int? Seed);
