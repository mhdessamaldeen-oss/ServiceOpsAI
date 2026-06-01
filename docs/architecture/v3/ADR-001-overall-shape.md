# ADR-001 — Overall shape

**Status:** Accepted (this session)
**Date:** 2026-05-29

## Context

V2 has 50+ SpecRepair phases mutating a shared `QuerySpec` in DI registration order. Adding a phase is easy; reasoning about the whole pipeline is impossible. 21 hardcoded-vocab incidents leaked back into C# even with a JSON config in place.

## Decision

V3 organises the copilot as **7 typed pipeline stages over a pure Domain layer**:

```
Understand → Retrieve → Plan → Repair → Validate → Execute → Explain
```

- Each stage is `IPipelineStage<TIn, TOut>` returning `Result<TOut, Fault>`.
- Domain types are **immutable records** living in `V3/Domain/`.
- Application stages and the trace event bus live in `V3/Application/`.
- I/O adapters (LLM, DB, embedder, config) live in `V3/Infrastructure/`.
- No layer references something it shouldn't (enforced by architecture tests).

## Consequences

- Adding a feature = touch one stage + add a typed event. Not "another phase."
- Domain logic is 95%-testable without mocks.
- The cost is a one-time port; v2 keeps running until v3 passes the suite.

## Alternatives rejected

- **Keep patching v2.** Reviewer D listed 21 unresolved hardcoded-vocab leaks. Patching produces a 22nd.
- **Microservices rewrite.** Out of scope; the host monolith is fine.
- **Plug-in style with Roslyn analyzers.** Higher operational complexity than typed stages; nothing it solves that interfaces + arch-tests don't.
