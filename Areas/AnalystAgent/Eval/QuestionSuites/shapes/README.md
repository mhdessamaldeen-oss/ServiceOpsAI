# Shape-Isolated Test Suites

15 focused JSON suites, one per question shape, designed to validate each shape's coverage independently without firing the full 192-question monolith. Run any single suite in ≤30 minutes; pinpoint regressions per shape.

## File layout

```
shapes/
├── suite-shape-COUNT-2026-05-28.json        ── 21 Q
├── suite-shape-TOPN-2026-05-28.json         ── 16 Q
├── suite-shape-AGGREGATE-2026-05-28.json    ── 17 Q
├── suite-shape-TIMESERIES-2026-05-28.json   ── 15 Q
├── suite-shape-COMPARE-2026-05-28.json      ── 16 Q
├── suite-shape-LOOKUP-2026-05-28.json       ── 16 Q
├── suite-shape-JOIN-2026-05-28.json         ── 16 Q
├── suite-shape-FILTER-2026-05-28.json       ── 17 Q
├── suite-shape-WINDOW-2026-05-28.json       ── 12 Q
├── suite-shape-RECURSIVE-2026-05-28.json    ── 7 Q
├── suite-shape-SELFJOIN-2026-05-28.json     ── 6 Q
├── suite-shape-EXISTS-2026-05-28.json       ── 10 Q
├── suite-shape-UNION-2026-05-28.json        ── 6 Q
├── suite-shape-HAVING-2026-05-28.json       ── 7 Q
└── suite-shape-SAFETY-2026-05-28.json       ── 16 Q
```

**Total ≈ 200 questions** across 15 focused suites.

## Team-curation rationale (whose lens shaped which decision)

### Data Analyst (writes the questions)
- Questions phrased as a Syrian-utility-ops user would actually ask: "how many bills issued this week", "who owes us money", not synthetic test phrasing
- Difficulty levels reflect real operational complexity, not arbitrary tiers
- Mix of EN + AR per shape so we measure bilingual parity at the shape level

### QA Engineer (calibrates difficulty + ensures coverage)
- Each shape has ~3–4 questions per difficulty band (simple/medium/hard) for statistical signal
- Edge cases (typos, casual, vague) explicitly carried — these are real production phrasings
- Multi-part questions test the Decomposer; we want to know if it correctly splits OR correctly avoids splitting

### Prompt Engineer (ensures each question hits its intended shape)
- Each question's `Category` field names the shape + sub-variation it's meant to exercise (e.g. `shape:COUNT/multi-filter`)
- Easy questions test pure shape recognition; hard questions test combined-pattern handling (shape + filter + lookup + temporal)

### AI Engineer (designs schema for measurability)
- Each question carries `ExpectedPrimaryEntity` / `ExpectedOperation` / `ExpectedAggregations` / `ExpectedFilters` — these are Golden-Test Parity Extensions that already exist in `CopilotAssessmentCase`
- A future "expectation verifier" service can compare generated SQL to the expectations to score semantic correctness, not just execution-pass
- Per-shape isolation means a regression in WINDOW doesn't get hidden by passing-COUNT cases

### DB Engineer (validates expectations are valid T-SQL idioms)
- All `Expected*` fields express what the canonical T-SQL should contain — column qualifications, datetime functions, soft-delete invariants
- For B-* shapes (WINDOW/RECURSIVE/SELFJOIN/EXISTS/UNION/HAVING), expectations are operation-level only because the SQL shape is constrained by the gold examples in `LlmDirectSqlEmitter`

### NL Specialist (Arabic / casual / typo variants)
- Each shape suite includes 1–2 Arabic variants of medium-difficulty questions
- Edge cases include typo variants ("tikets count") and casual phrasings ("give me the overdue stuff")
- Arabic variants use idiomatic phrasing, not literal translation of English

## How to use

### Per-shape regression check (the main workflow)

1. **Make a code change** (new SpecRepair phase, prompt edit, grounder tweak)
2. **Pick the suite of the affected shape** — UI selector multi-pick OR pass via API
3. **Run** — ≤30 min per suite, vs 4+ hours for the monolith
4. **Inspect failures** — limited blast radius, easy to attribute the cause

### Baseline lock after a series of changes

1. Run ALL 15 suites (one at a time over a day if needed)
2. Per-shape pass rates lock as separate baselines
3. Future regressions show up against the matching shape's baseline

### Cross-cutting (full coverage) run

When you want a comprehensive baseline, run all 15. Combined run ≈ same volume as old monolith but you get per-shape numbers, not a single rolled-up percentage.

## Schema (per question)

| Field | Meaning |
|---|---|
| `Code` | Stable case ID — `SHAPE-DIFFICULTY-N` |
| `Question` | The user-facing question in NL |
| `Category` | Free-text shape + sub-variation tag |
| `Difficulty` | `Easy` / `Medium` / `Hard` / `Complicated` |
| `ExpectedIntent` | `DataQuery` / `Refusal` / `Conversational` / `Knowledge` / `Metadata` / `Clarification` |
| `ExpectedPrimaryEntity` | Expected root table name (Tickets, Bills, Outages, …) |
| `ExpectedOperation` | Expected high-level operation (COUNT, LIST, SUM, TIMESERIES, COMPARE, EXISTS, …) |
| `ExpectedLimit` | Expected TOP-N value, when known |
| `ExpectedAggregations` | List of `{function, column, distinct?, alias?}` |
| `ExpectedFilters` | List of `{Column, Op, Value}` |
| `ExpectedFields` | List of `Table.Column` that should appear in SELECT |
| `ExpectedGroupBy` | List of expressions that should appear in GROUP BY |
| `ExpectedSorts` | List of `{Column, Direction}` |
| `ExpectedHavingFilters` | List for HAVING-clause aggregates |
| `ExpectedDecomposition` | Number of sub-questions when compound |
| `MaxLatencyMs` | Per-question timeout (default 180000 = 3 min; safety/conversational use 30000–60000) |

## Phase 2 (deferred): expectation verifier

These expectations are inert until a verifier service is wired in. The plan:

1. Parse the generated SQL (using `Microsoft.SqlServer.TransactSql.ScriptDom` — already in the project)
2. Walk the AST and extract the actual: root table, filters, aggregations, joins, group-by, order-by
3. Compare against the expectations → emit per-question PASS / PARTIAL / FAIL
4. Wire results into `CopilotAssessmentRunSummary` so the report shows semantic correctness alongside execution-pass

Estimated cost: ~1 session of focused work. Once it's in place, every future run produces TWO numbers (execution vs semantic) — that's the real baseline.

## Sources informing this design

- **DIN-SQL** (decomposition + classification per shape) — [arxiv.org/pdf/2304.11015](https://arxiv.org/pdf/2304.11015)
- **CHESS** (entity & value linking; shape-aware few-shot) — [arxiv.org/html/2405.16755v1](https://arxiv.org/html/2405.16755v1)
- **E-SQL** (question enrichment; pre-grounded prompt) — [arxiv.org/abs/2409.16751](https://arxiv.org/abs/2409.16751)
- **Spider / BIRD benchmark conventions** — shape categories, gold-SQL standards
- **Looker / Holistics / Honeydew BI tools** — fan-out join handling, COUNT(*) → COUNT(DISTINCT col) convention
