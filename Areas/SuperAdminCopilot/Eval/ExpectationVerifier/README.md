# ExpectationVerifier

Semantic-correctness checker for assessment traces. Reads the `Expected*` fields on a `CopilotAssessmentCase` and verifies the generated SQL contains the matching facets.

## What it checks

| Expected field | Verifier behavior |
|---|---|
| `ExpectedPrimaryEntity` | Entity name appears in/after FROM clause |
| `ExpectedOperation` | Operation pattern present (COUNT regex, TOPN→TOP/RANK, WINDOW→OVER, EXISTS→EXISTS, etc.) |
| `ExpectedLimit` | TOP N / FETCH NEXT N matches |
| `ExpectedFilters` | Each filter's column appears in SQL; IS NULL filters validated structurally |
| `ExpectedFields` | Each projected column appears in SELECT |
| `ExpectedAggregations` | Function + column pair appears in `fn(col)` shape; DISTINCT recognized |
| `ExpectedGroupBy` | Each GROUP BY expression's column appears after GROUP BY |
| `ExpectedIntent` (Refusal/Conversational/Knowledge/Metadata) | No SQL generated → pass; SQL generated → violation |

## Verdict logic

- **Pass** — ≥ 99% of checks passed (essentially all)
- **Partial** — ≥ 60% passed
- **Fail** — < 60% (or root entity wrong / SQL on a refusal intent)
- **NotApplicable** — no expectations declared (test runs but isn't graded)

## Why string-matching, not full AST

The expectations are coarse-grained on purpose. We don't want to false-fail on equivalent SQL formulations (LEFT JOIN+IS NULL vs NOT EXISTS, IN-clause vs OR-chain, qualified vs aliased columns). The verifier asks "did the SQL touch the expected facets" — a permissive but useful signal.

## Wiring (not yet done)

The verifier is registered in DI as `IExpectationVerifier`. To plug it into the assessment report:
1. Inject `IExpectationVerifier` into `CopilotAssessmentHandler`
2. After each trace is persisted, call `Verify(case, generatedSql)` and store the verdict alongside `CaseCode` / `ErrorMessage` (e.g. add `SemanticVerdict` column to `CopilotTraceHistories`)
3. Aggregate per-shape pass rates: `(Pass + 0.5*Partial) / Total` is a reasonable semantic-correctness rate

For now the verifier is a standalone service — call it from a console script or a one-off report to generate per-shape semantic numbers from already-stored traces.
