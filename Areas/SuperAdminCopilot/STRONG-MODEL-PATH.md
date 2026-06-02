# SuperAdminCopilot — Local → Strong-Model Path

How to move the copilot from the local model (`qwen2.5-coder:7b`, **Weak** tier) to a strong cloud
model (Claude / GPT, **Strong** tier), and what each knob changes. The abstraction is already clean:
**no copilot code changes are required** — this is host-provider + config only.

---

## What "strong model" buys you

The copilot is engineered so the *planner* (NL → `QuerySpec`) is the only place model strength
matters. Everything downstream (compile → validate → execute → explain) is deterministic. A stronger
planner needs **fewer crutches**: the 23 `IRepairRule` patches that fix weak-model spec mistakes are
tier-gated and auto-disable as the model gets stronger, so the same pipeline gets *simpler*, not more
complex, on a strong model.

---

## The 5 steps

### 1. Point the Copilot workload at the strong provider
The only host-coupled file is [HostBridge/HostAiProviderLlmClient.cs](HostBridge/HostAiProviderLlmClient.cs)
— it resolves `AiWorkloadType.Copilot` through the host `IAiProviderFactory`. Swap that workload's
backing provider to the cloud model. The copilot's [`ILlmClient`](Abstractions/ILlmClient.cs)
contract is unchanged, so nothing else in the pipeline moves.

The simplest switch is config: flip the active **Profile** in
[Configuration/copilot-options.json](Configuration/copilot-options.json) to the `Cloud` preset
(frontier model, Strong tier) instead of `LocalMedium`.

### 2. Let the tier auto-derive (do NOT hand-set it)
`PlannerTierDeriver.FromModel()` reads the model name and derives `Weak | Medium | Strong`. At
**Strong**, every repair rule whose `MaxTier` is below Strong is **skipped** — the weak-model
language/vocab crutches (e.g. negation rewriting, natural-key tokenisation, lifecycle-verb date
mapping) turn themselves off because a strong planner emits those correctly. Verify in the trace:
the run should report `tier=Strong` and the skipped rules.

### 3. Enable cost gates (off by default for local)
Local models are free, so the gates ship disabled. For a paid cloud model, in `copilot-options.json`:
- `MaxCostPerQuestionUsd` → e.g. `0.05`–`0.10` (the `CostGateExecutor` refuses runaway questions).
- `MaxTokensPerQuestion` → a sane ceiling; the `HostAiProviderLlmClient` pre-call gate stops before exhaustion.
- `LlmCallTimeoutSeconds` → drop from the local default (~900s) to ~30s; cloud answers in seconds.

### 4. Widen the schema window
Strong models have far larger context windows. Raise neighbour-table expansion
(`ExpandWithNeighbors`, default `6`) toward `15`–`20` so the planner sees more of the schema slice
per question. Config only — no recompile.

### 5. (Optional) Light up per-shape example routing
The 8-shape classifier (`DeterministicPromptShapeClassifier`) already tags every question; the
per-shape example-bank routing (Phase 7c) is wired but observational. On a strong model it's worth
finishing so each shape gets a sharper, smaller example set.

---

## What stays the same (the point of the architecture)

- **No code change** — the swap is `IAiProviderFactory` (host) + `copilot-options.json` (config).
- **Security is identical** — `ISensitiveColumnPolicy`, `QuerySpecAccessPolicyValidator`,
  `SqlAstValidator`, `ReadOnlyExecutor`, `PiiRedactingExecutor` are model-agnostic.
- **Portability is identical** — `ISqlDialect` still selects the SQL engine via `CopilotOptions.Database`.

---

## Support / Repair / Enhance — quick map

| You want to… | Do this | Where |
|---|---|---|
| **Support** a new question phrasing | Nothing model-specific — a strong planner handles it; for the local model add JSON cues, never C# | `Configuration/*.json` |
| **Repair** a recurring weak-model mistake | Add/adjust an `IRepairRule` (tier-gated so it auto-disables on strong models) | `Application/Repair/Rules/` |
| **Enhance** to a strong model | The 5 steps above (host provider + config) | `HostBridge/`, `copilot-options.json` |
| **Add a new DB/columns later** | Re-run schema introspection; the semantic layer + dialect carry it — no compiler change | runtime snapshot |
| **Retarget the SQL engine** | Set `CopilotOptions.Database`; the dialect binding flips | `copilot-options.json` |
