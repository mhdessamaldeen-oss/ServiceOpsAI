# ADR-009 — Plan stage + PlannerTier policy

**Status:** DRAFT (post-review skeleton — to be expanded next session)
**Date:** 2026-05-29

## Context

`IRepairRule.MaxTier` references `PlannerTier { Weak, Medium, Strong }` but no ADR defines
who sets the active tier, when it changes, or what tier any given deployment is on.

## Decision (provisional, to be ratified)

### Tier semantics

| Tier | Meaning | Example model |
|---|---|---|
| Weak | 7B local / quantized — needs every repair rule available | qwen2.5-coder:7b |
| Medium | 14-32B local or small cloud — most rules still help | qwen2.5-coder:32b, gpt-4o-mini |
| Strong | Frontier / fine-tuned — repair rules largely passthrough | gpt-4o, claude-opus-4.7, fine-tuned 7B |

### Who decides the active tier

`PlannerCapabilityProfile` (configured in `copilot-options.json`'s `Profile` field; today's
values: `LocalSmall`, `LocalMedium`, `LocalLarge`, `Cloud`) maps 1:1 to `PlannerTier`:

| Profile | Tier |
|---|---|
| LocalSmall | Weak |
| LocalMedium | Weak |
| LocalLarge | Medium |
| LocalMedium32B | Medium |
| Cloud | Strong |

The Plan stage reads `CopilotOptions.Profile` at request time, derives the tier, packs it
into `PipelineContext.ActiveTier`. `RepairBus` filters rules by `MaxTier >= ActiveTier`.

### Per-question tier override

A single `CopilotOptions.OverrideTierForQuestion` knob (default null) lets ops force a tier
without restarting. Used for A/B testing repair rules' value on stronger models.

### Plan stage architecture

`IPlanner` interface, multiple implementations under a Strategy + Router pattern:

| Planner | When it wins |
|---|---|
| `VerifiedQueryPlanner` | RetrievalSet has a `VerifiedQueryHit` above the trust threshold |
| `CachedRagPlanner` | RetrievalSet has a similar past trace and the question is structurally close |
| `LlmPlanner` | Default — issues the planning LLM call |
| `FallbackTemplatePlanner` | LLM unavailable; emits a best-effort spec for the simplest shapes |

`PlannerRouter` picks based on confidence + question shape. Single decision point; emits a
`PlannerChosen` trace event so the investigation page shows the route.

### Per-question tier downgrade

If `LlmPlanner` returns a malformed spec that even the Repair stage cannot fix, the Plan
stage retries once with a stronger tier if available. This replaces v2's
`MaxSelfCorrectionRetries` knob with a typed retry policy.

## Open questions

1. Does the LLM planner output flow directly into Repair, or through a Spec-Validation pre-stage?
   ADR-010 will answer this.
2. Fine-tune output: is the fine-tuned 7B a Medium-tier or Strong-tier model? Probably
   Medium until measured.
3. Cost-budget interaction: who short-circuits to a cheaper planner when the per-question
   USD budget is near the cap?
