# ADR-003 — Immutable QuerySpec via record types

**Status:** Accepted
**Date:** 2026-05-29

## Context

V2's `QuerySpec` is mutable: every phase calls `spec.Filters.Add(...)`, `spec.Aggregations.Clear()`, `spec.Joins.RemoveAt(...)`. Two consequences:

1. **NPE risk** when a phase appends `null` to a list (the September incident in `ApplyConceptPatternsPhase.CollectReferencedTables`).
2. **Untraceable mutations** — when a final SQL is wrong, you can't pinpoint *which phase* produced the offending Filter without rerunning with logging.

## Decision

In v3, `QuerySpec`, `FilterSpec`, `JoinSpec`, `OrderBySpec`, `AggregateSpec`, `ComputedSpec`, `HavingSpec`, `PeriodSpec` are **C# records with init-only properties** holding `ImmutableList<T>` collections.

Mutations happen via a `QuerySpecBuilder` (Builder pattern) or via `with`-expressions producing a new spec. Repair rules return a NEW `QuerySpec`; they do not mutate the input.

```csharp
public sealed record QuerySpec(
    string Root,
    ImmutableList<string> Select,
    ImmutableList<FilterSpec> Filters,
    ImmutableList<AggregateSpec> Aggregations,
    ImmutableList<string> GroupBy,
    ImmutableList<OrderBySpec> OrderBy,
    ImmutableList<JoinSpec> Joins,
    int? Limit,
    /* ... */);
```

## Consequences

- NPE risk on list elements **structurally eliminated** — `ImmutableList<T>` has no null elements.
- A repair rule's effect is a `QuerySpec → QuerySpec` function — trivially testable, trivially diff-able.
- The trace event `FaultRepaired` carries both the before-spec hash and the after-spec hash; you can replay.
- A small memory overhead per repair step. Worth it — repair stage runs once per question, not in a hot loop.

## Alternatives rejected

- **Keep mutable + sprinkle `.NotNull()` helper.** Patches the symptom; doesn't change the architecture.
- **Lenses / optics library.** Overkill for our use case; records + `with` is enough.
