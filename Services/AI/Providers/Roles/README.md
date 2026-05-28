# Per-Role LLM Bindings

Sibling layer to the existing per-workload provider bindings in `AiProviderSettings`. Lets
each new SuperAdminCopilot pipeline stage declare its own model independently — e.g.
schema-linking on a small 7B, QuerySpec composition on a fine-tuned 14B, paraphrase
generation on Claude Sonnet — all configurable via `appsettings.json`.

## Why a sibling layer instead of editing `AiProviderSettings`

`AiProviderSettings` is on the live execution path. The role-binding system is **strictly
additive**: it lives in its own config section (`Ai:RoleBindings`) and its own DI service
(`IRoleBoundLlmClientFactory`). Existing code keeps using the workload bindings exactly
as before. Migration to the role-bound factory is opt-in, per-call-site.

## How it will work post-activation

```jsonc
// appsettings.json
"Ai": {
    "ActiveProvider": "Ollama",
    "ClassifierProvider": "Ollama",
    "CopilotProvider":    "Ollama",

    // NEW sibling section — populated only for roles you want to override
    "RoleBindings": {
        "Classifier":          { "Provider": "Ollama", "Model": "qwen2.5:3b",        "Temperature": 0.0 },
        "QuerySpecComposer":   { "Provider": "Ollama", "Model": "qwen2.5-coder:14b", "Temperature": 0.1 },
        "SchemaLinker":        { "Provider": "Ollama", "Model": "qwen2.5-coder:7b",  "Temperature": 0.0 },
        "StructuralCueParser": { "Provider": "Ollama", "Model": "qwen2.5:3b",        "Temperature": 0.0 },
        "Paraphraser":         { "Provider": "Cloud",  "Model": "claude-sonnet-4-6", "Temperature": 0.7, "MaxTokens": 3000 },
        "Frontier":            { "Provider": "Cloud",  "Model": "claude-sonnet-4-6", "Temperature": 0.1 }
    }
}
```

Each pipeline stage requests its client via the factory:

```csharp
internal sealed class SchemaLinker
{
    private readonly ILlmClient _llm;
    public SchemaLinker(IRoleBoundLlmClientFactory factory)
    {
        _llm = factory.For(AiRole.SchemaLinker);
    }
    // ...
}
```

## Current status: stub

`StubRoleBoundLlmClientFactory` currently returns the existing global `ILlmClient` for every
role — so the role-bound code paths compile and integrate cleanly, but the live behaviour
is unchanged. The real implementation (which actually constructs per-role clients by
looking up the resolved provider + model) is part of the post-assessment activation step:

1. Replace `StubRoleBoundLlmClientFactory` with a real implementation that consults
   `AiProviderFactory` and constructs an `HostAiProviderLlmClient`-equivalent per binding.
2. Register `IRoleBoundLlmClientFactory` in DI.
3. Migrate each pipeline stage that wants role-specific behaviour to inject the factory
   instead of `ILlmClient`.

Stages that don't migrate keep working — they continue to use the global `ILlmClient`.

## Inheritance rules

A role binding with an empty `Provider` field inherits from the matching workload slot in
`AiProviderSettings`. A binding with an empty `Model` field inherits from the resolved
provider's default `Model`. So a user only has to specify what they want to override.

The full resolution chain for a role:

```
role binding (Provider/Model/Temperature/MaxTokens)
    ↳ falls back to workload binding (ClassifierProvider, CopilotProvider, …)
        ↳ falls back to global ActiveProvider
            ↳ falls back to DockerLocal
```

## Fine-tuning workflow (later)

When fine-tuning kicks in, swapping a custom model into a role is a **one-line config
change** — no code edits required:

```jsonc
"QuerySpecComposer": { "Provider": "Ollama", "Model": "myorg/qwen-coder-syria-finetune:v1" }
```

This is the single most important property of the role-binding system: **fine-tuned model
swap = config change**, never a code change.
