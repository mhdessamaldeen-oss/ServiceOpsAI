# SpecRepair

Single owner of all LLM-output mutation between **SpecExtractor** (parses raw LLM JSON into a `QuerySpec`) and **SqlCompiler** (synthesises T-SQL).

## Contract

The `QuerySpec` returned by `SpecRepair.Apply` is well-formed and self-consistent. The compiler trusts it.

## Phase order (registration order in `ServiceCollectionExtensions.cs`)

| # | Phase | Covers |
|---|-------|--------|
| 1 | HoistInlineAggregates | `select: ["SUM(x) AS y"]` → moves to `aggregations[]` |
| 2 | NormalizeAggregationFunction | `"COUNT(DISTINCT)"` → `COUNT` + `distinct=true` |
| 3 | NormalizeFilterOperator | `=`/`!=`/`>` → `eq`/`neq`/`gt` |
| 4 | StripQuotedFilterValues | `value: "'foo'"` → `"foo"` |
| 5 | InferRootFromQuestion | Set/override root via question-text scoring + entity synonyms |
| 6 | InferRootFromColumnRefs | Fallback: first qualified column ref |
| 7 | AutoQualifyColumns | Bare `Column` → `Root.Column` |
| 8 | DropAggregatedColumnsFromSelect | Strip metric column from select when also aggregated (no GROUP BY) |
| 9 | DropFilterContradictingGroupBy | Drop eq-filter on a column also in GROUP BY |
| 10 | DropSpuriousGroupByForScalarAggregation | MAX/MIN with GROUP BY on PK/NK → drop GROUP BY |
| 11 | MapValueSynonyms | Filter values: casual → canonical via semantic layer |
| 12 | ConvertNameFilterToLike | eq on a searchable name column → like `%value%` |
| 13 | UpgradeInnerJoinToAntiJoin | Question matches anti-intent + INNER JOIN → flip to `anti` |
| 14 | ConvertTopNAggregationToList | "top N largest X" + scalar agg → list shape |
| 15 | EnforceCountOuterEntity | "how many X have Y" → root=X + COUNT(DISTINCT X.Id) |
| 16 | ForceAggregationOnCountQuestion | Question implies aggregation but spec lacks one → inject |

## Adding a new phase

1. Create `Phases/NewName.cs` implementing `ISpecRepairPhase`. One-line `<summary>`. No multi-line blocks.
2. Unit test in `Tests/SuperAdminCopilot.Tests/SpecRepair/SpecRepairPhasesTests.cs` using captured-trace fixture.
3. Register in `ServiceCollectionExtensions` at the right position.
4. Update this README's table.

## Configuration

Patterns / aliases / operator maps live in `Areas/SuperAdminCopilot/Configuration/spec-repair-rules.json` — pure data, hot-reload via `IOptionsMonitor<SpecRepairOptions>`.

## Observability

Each phase appends a `SpecRepairDiagnostic` to `ctx.Diagnostics` when it mutates. `SpecRepair.Apply` emits one `LogInformation` per run summarising fired phases.
