# ADR-010 — Validate stage contract

**Status:** DRAFT (post-review skeleton — to be expanded next session)
**Date:** 2026-05-29

## Context

ADR-005 promised "after every rule, run validator: spec still well-formed?" The Validate
stage and its contract were never specified.

## Decision (provisional, to be ratified)

### Two validation passes

1. **Inside Repair** — fast, cheap structural checks after each rule fires:
   - Root exists in catalog.
   - Every column reference resolvable.
   - Aggregation columns are numeric / countable.
   - GROUP BY contains every non-aggregated SELECT.

   Implemented as `ISpecInvariantCheck` instances; `RepairBus` runs them between rules. If
   a check fails, the bus **rolls back** the offending rule's `Apply` and emits a
   `RuleRolledBack` trace event. No exceptions.

2. **Validate stage proper** — runs AFTER all rules complete. Comprehensive checks:
   - Cost-gate (`MaxEstimatedQueryCost` from CopilotOptions).
   - Cardinality guard (estimated row count vs `MaxRows`).
   - Write-intent guard (no DML smuggled into a read-only path).
   - Cross-join detection.
   - Anti-join correctness (LEFT JOIN + WHERE PK IS NULL).

   Failures here are `ValidationFault` (raised; no rollback — repair is complete by this
   stage). The pipeline returns the fault to the user as a clarification request, not a
   server error.

### Fault model

- `ValidationFault(reason)` — raised when validation fails after repair.
- `PolicyRefusedFault(gate, reason)` — when a policy gate (write-intent / operational
  guard) blocks.
- `CostBudgetFault` — when the cost estimator predicts spend over the per-question cap.

### Rule rollback semantics

When a rule's `Apply` produces a spec that fails an invariant check:
- The bus discards the rule's output, keeps the previous spec.
- Emits `RuleRolledBack { Rule, Reason, BeforeHash }`.
- Continues to the next rule.

This is the right place to catch a bad rule before it cascades through other rules.

## Consequences

- The Validate stage becomes the "final gate" before SQL emission — clear single point of
  failure-detection rather than v2's scattered guards.
- Rolled-back rules show up in the investigation page; an operator can see "rule X tried to
  fix Y, but it broke Z, so we kept the old spec."
- Cost estimation moves out of the Plan stage (where v2 does it via
  `IQueryCostEstimator.EstimateAsync`) into Validate. Single responsibility.

## Open questions

1. Should invariants short-circuit (first failure stops) or accumulate (report all
   violations)? Probably accumulate for diagnostics, short-circuit for cost-gate.
2. Rollback on inter-rule invariant failure is new behavior; v2 has no concept. Worth
   measuring effect on the 305-suite.
3. The cardinality guard requires a count-rows pre-flight against the live DB; expensive
   on cold queries. Cache or skip for cold paths?
