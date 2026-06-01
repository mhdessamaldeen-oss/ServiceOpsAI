# ADR-002 — Result&lt;T, Fault&gt; instead of exceptions

**Status:** Accepted
**Date:** 2026-05-29

## Context

V2 catches and swallows exceptions in many phases (`try { ... } catch { /* swallow */ }`). The result: bugs hide as silent no-ops, the trace shows nothing, and intermittent failures take days to find.

## Decision

Domain code returns `Result<T, Fault>`. `Fault` is a discriminated union (`abstract record Fault`) with cases:

- `FaultKind.MissingInput`
- `FaultKind.RegexCompileFailed`
- `FaultKind.SchemaUnknown`
- `FaultKind.ValidationFailed`
- `FaultKind.SqlExecError`
- `FaultKind.LlmUnavailable`
- `FaultKind.OutOfScope`
- `FaultKind.CostBudgetExceeded`

Exceptions are reserved for **programmer errors** (null where the type system promised non-null, integer overflow). Domain failures are values, not control-flow exceptions.

## Consequences

- Every stage signature has explicit failure shape.
- Test assertions become `Assert.IsType<MissingInputFault>(result.Fault)` instead of "did this throw?"
- Trace events become typed (`FaultRaised { Fault: …}`), and the investigation page renders them by kind.
- Slightly more ceremony at call sites; we add a `.Bind()` extension for `Result` so chaining stays clean.

## Alternatives rejected

- **Bare `T?` returns.** Loses the *why* of the failure.
- **`(bool ok, T value, string? error)` tuples.** Ad-hoc; no compile-time exhaustiveness check on Fault kinds.
- **Always-throw.** Tried that in v2; it produces try/catch noise and swallowing.
