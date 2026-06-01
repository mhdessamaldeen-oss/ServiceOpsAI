# ADR-007 — Cutover plan and rollback strategy

**Status:** Accepted
**Date:** 2026-05-29

## Context

V3 is a non-trivial refactor. We need a path to production that does NOT put v2 at risk.

## Decision

V3 is **additive**. Until cutover, v2 is the only pipeline that handles real questions. The transition follows **five gates plus Gate 0** (added post-review).

### Gate 0 — Database migration applied (ships separately, before any v3 wiring)

- `Add-Migration AddCopilotTraceEvents` creates the events table.
- Migration applied in CI's integration-test DB.
- Migration applied in every target environment BEFORE the v3-shadow flag is flipped.
- `TraceEventWriter` hosted service is registered behind `PipelineVersion != "v2"`; if the
  flag is `"v2"`, the writer is not registered, so a missing table cannot crash startup.

Rationale: ADR-006 makes `CopilotTraceEvents` the authoritative trace store. Shadow-mode
(Gate 4) attempts the first writes. If the table doesn't exist, the first event flush
crashes the worker.

### Gate 1 — Domain + Registry build clean

- `dotnet build` produces zero errors with v3 source present.
- v2 behavior is unchanged.

### Gate 2 — Parallel-run harness shows parity on hand-picked cases

- Pick 20 representative cases (1 per shape).
- Run them through v2 and through v3's Plan + Repair stages.
- Diff the emitted QuerySpecs.
- Spec divergence ≤ 5% per case.

### Gate 3 — Full suite parity

- Run the 305-case baseline through both pipelines.
- v3 score ≥ v2 score on every shape category.
- v3 has zero new failure modes v2 didn't have.

### Gate 4 — Feature flag, shadow-mode in production

- `CopilotOptions.PipelineVersion = "v3-shadow"` — v3 runs alongside v2 but v2's answer is returned to the user.
- v3 trace events captured but not surfaced.
- 7 days of production shadow.
- Compare trace events for divergence. Triage anything material.

### Gate 5 — Cutover

- `CopilotOptions.PipelineVersion = "v3"` — v3 is authoritative.
- v2 code path remains for 30 days as `_legacy`.
- Then deleted.

### Rollback

Flip `PipelineVersion` back to `"v2"`. Done. The v3 events stop being produced (legacy events table remains). The 50 v2 phases keep running.

## What MUST be true at every gate

- v2 unchanged in behavior. No v2 file touched as part of v3 work.
- v3 has its own folder, its own namespace, its own DI registrations under a flag.
- The `CopilotTraceEvents` table coexists with `CopilotTraceHistories`. The v2 row is still written via the existing async queue path.

## Acceptance criteria for "v3 is done"

| # | Criterion |
|---|---|
| 1 | All 12 repair rules implemented. |
| 2 | LinguisticRegistry covers every cue v2 uses. |
| 3 | Architecture tests in CI; failing PRs blocked. |
| 4 | Parallel-run harness reports `v3 score >= v2 score` on the 305-suite. |
| 5 | Investigation page shows typed events. |
| 6 | v2 code archived to `_legacy/`. |
| 7 | One page of release notes in `docs/architecture/v3/RELEASE-NOTES.md`. |
