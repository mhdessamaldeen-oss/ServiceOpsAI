# ADR-005 — 50 SpecRepair phases → 12 typed RepairRules

**Status:** Accepted
**Date:** 2026-05-29

## Context

V2's 50+ SpecRepair phases overlap, fight, and silently mutate each other's outputs. Their ordering is "whatever DI registered first." Adding a phase has no friction; understanding the pipeline does.

## Decision

V3 has **exactly one** Repair stage. It runs a set of `IRepairRule` implementations.

```csharp
public interface IRepairRule
{
    FaultKind FaultClass { get; }
    PlannerTier MaxTier { get; }
    IReadOnlyList<FaultKind> Requires { get; }    // dependency on other rules

    Result<Diagnosis, NoFault> Detect(QuerySpec spec, RepairContext ctx);
    QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis);
}
```

A rule:
1. **Detects** a fault — returns `Diagnosis` or `NoFault.Instance`.
2. **Applies** the fix — returns a new spec.
3. Declares its dependencies — `RepairBus` runs `MissingRoot` before `DanglingColumnReference`.

The 12 rules (each ≤80 LOC):

| # | Rule | Replaces (v2 phases) |
|---|---|---|
| 1 | `MissingRoot` | InferRootFromQuestion + InferRootFromColumnRefs + ArabicQuestionDispatch's root setting |
| 2 | `DanglingColumnReference` | AutoQualifyColumns + DropAggregatedColumnsFromSelect |
| 3 | `WrongAggregationShape` | ForceNonCount + ReplaceWrong + ForceCountDistinct + ForceAggOnCount + NormalizeAggregationFunction |
| 4 | `MissingTemporalScope` | InjectTemporalFilter + SpecificYearMonthFilter + SwapDateColumnByVerb |
| 5 | `MissingLookupFilter` | InjectLookupValue + ConvertFkEqualsName + ConvertNameToLike + MapValueSynonyms |
| 6 | `MissingTextSearch` | InjectTextSearchFilter + ApplyConceptPatterns text branch |
| 7 | `MissingAntiJoin` | InjectAntiJoin + UpgradeInnerJoinToAntiJoin |
| 8 | `UnsolicitedFilter` | RewriteEmptyToIsNull (all passes) + StripUnsolicitedStatusOnSuperlative + StripFiltersOnAllTime |
| 9 | `AmbiguousLimit` | ForceTopNLimit + ForceTopNRowsOverMaxMin + ConvertTopNAggregationToList |
| 10 | `OverJoin` | DetectOverJoin + ForceCountDistinctOnFanOutJoin + UpgradeInnerJoinToAntiJoin |
| 11 | `InvalidSelectGroupBy` | EnsureSelectInGroupBy + DropFilterContradictingGroupBy + DropSpuriousGroupByForScalarAggregation |
| 12 | `DisplayColumnsMissing` | EnsureDisplayColumns + EnrichSelectWithLabels |

**50 phases → 12 rules. ~7000 LOC deleted on cutover.**

## RepairBus algorithm

1. Topological-sort rules by `Requires`.
2. For each rule in order:
   - Call `Detect(spec, ctx)`.
   - If `Diagnosis`, call `Apply(spec, dx)` → new spec. Emit `FaultRepaired` event.
   - If `NoFault`, skip silently.
3. After every rule, run validator: spec still well-formed?

## Consequences

- Adding a fix = add a rule file (≤80 LOC) + test. No DI ordering surprise.
- Removing a fix = delete one file.
- Trace shows "rule X detected fault Y, applied Z" — readable.
- Performance: 12 typed calls vs 50 mutating phases — net faster.

## Alternatives rejected

- **Keep 50 phases, add a coordinator.** Patches the symptom; the 50 files still exist with their inline regexes.
- **Single mega-rule that does everything.** Loses the fault taxonomy that makes trace events meaningful.

## Amendments (post-team-review)

### A1. `RepairFaultKind` is distinct from `FaultKind`

ADR-002's `FaultKind` classifies *what blew up* (LLM unavailable, schema unknown, validation
failed). This ADR's `RepairFaultKind` classifies *what the spec is missing* (root, lookup
filter, ordering). They share no enum values. Code keeps them separate types. Trace events
carry whichever applies to that event — `FaultRaised` → `FaultKind`; `FaultRepaired` →
`RepairFaultKind`.

### A2. `Detect` returns `Result<Diagnosis, Fault>` — not `Result<Diagnosis, NoFault>`

Original draft had a phantom `NoFault` type. Actual contract: `Detect` returns
`Result.Ok(Diagnosis)` whose `Kind` may be `RepairFaultKind.None` (a sentinel meaning "I
looked, no fix needed"). A real `Fault` is only returned when detection cannot proceed
(semantic layer down, missing input — these are ADR-002 faults). Repair rules never invent
new fault kinds.

### A3. Sub-rule pattern (one fault class, N actions) is allowed

Some fault classes wrap multiple sub-actions (see `UnsolicitedFilterRule`'s
RewriteEqEmptyToIsNull / StripDangling / StripStatus-on-AllTime / StripDate-on-AllTime /
StripStatus-on-Superlative). Acceptable when:

- The sub-actions share a Detect trigger family (here: "the user said the filter shouldn't be there").
- The combined rule stays under 200 LOC.

When neither condition holds, the rule MUST split. `WrongAggregationShape` (proposed to
absorb 5 v2 phases) is on the line — the implementer chooses.

### A4. `Requires` cycle detection runs at startup

`RepairBus` throws on (a) a cycle and (b) a `Requires` referencing a non-registered fault
class. Both are programmer errors; no silent drop.
