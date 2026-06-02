# Minimal Analyst Path — Action Plan

*The thin copilot: route → ground → generate → verify → explain. 2 LLM calls on the happy path. Built BESIDE the existing pipeline, flag-gated, with the heavy pipeline as the fallback — so it cannot regress today's behavior.*

## The safety guarantee (read this first)
The new path is **additive, OFF by default, and falls through to the current pipeline on any failure**. Worst case it produces nothing and the existing engine answers exactly as it does today. **There is no way for this to make things worse than now** — that's the whole point of building it as a flag-gated front-path with fallback.

## What already exists (we reuse, we don't rebuild)
| Step | Already in your code | Signature |
|---|---|---|
| **Route** | `IntentClassifier` | `ClassifyAsync(question, ct) → {Intent: Sql/Chat/Tool/OutOfScope/Refinement, Confidence}` |
| **Ground** *(the moat, deterministic, no LLM)* | `QuestionGrounder` | `GroundAsync(question, linkedTables, ct) → QuestionGroundingContext {LinkedTables, LinkedValues[{Table,Column,Value,Confidence}], LinkedTemporal, LinkedNaturalKeys, PromptShape, DateRoleHint}` |
| **Generate** | `LlmDirectSqlEmitter` (today's "escape valve") | `EmitAsync(question, candidateTableNames, ct) → {Sql, Error, Prompt, RawLlmOutput}` |
| **Verify** | `SqlAstValidator` + read-only executor chain | `Validate(CompiledSql) → {IsValid, Errors}` (rejects DML/multi-statement/system-tables/unknown-tables) |
| **Execute** | `IExecutor` | `ExecuteAsync(CompiledSql, ct) → {Rows, RowCount, Error, ...}` |
| **Explain** | `IExplainer` | `ExplainAsync(question, result, compiled, ct) → {Reply}` |

The existing `SingleQuestionExecutor.TryEscapeValveAsync()` **already chains Emit→Validate→Execute→Explain**. The minimal path = run that shape FIRST (before the heavy `SpecExtractor`), with grounding injected into the prompt.

## Call budget
- **Minimal happy path: 2 calls** — route (`IntentClassifier`) + generate (`LlmDirectSqlEmitter`). +1 retry only on execution error. +1 optional explain (only when rows warrant it).
- **Today: 3–6 calls** — classifier + spec-extractor(+retries) + (decomposer) + explainer + (coverage). The IR, the 23 repair rules, and the compiler are skipped entirely on the happy path.

## The honest correctness split
- **The model owns SQL shape** (your Ollama test proved it — it nailed COUNT/LIKE/GROUP BY/above-average/Arabic).
- **Your grounding owns the schema** — it's what stops the model inventing `Customers.TotalBills`, guessing `IssuedTo='Malki'` (Malki is a *region*), or `'%electricity%'` (your data says `كهرباء`). **Phase 2 wires grounding into the prompt — that is the single most important step.**
- **Safety net**: invented columns → SQL Server errors → one retry feeding the error back (the model self-corrects). Wrong-but-real columns → prevented by grounding, not the validator.

---

## Phases (small, each with tests + hour estimate)

### Phase 1 — Wire the thin path behind a flag, with fallback `~2–3h`
- Add `CopilotOptions.EnableDirectSqlPath` (default **false**) + `DirectSqlPathMinConfidence`.
- In `SingleQuestionExecutor.ExecuteAsync`, after the fast-paths and before `SpecExtractor`: when the flag is on and candidate tables exist, run **Ground → Emit → Validate → Execute (+1 retry on error) → Explain**. On *any* failure, `return null`-equivalent and **fall through to the existing pipeline** unchanged.
- Reuse the existing `TryEscapeValveAsync` chain; don't duplicate it.
- **Tests:** unit tests (xUnit+Moq, mirroring `RepairRuleHarness`) for: happy path returns a reply; validation failure falls through to heavy path; execution error triggers exactly one retry then falls through; flag off = byte-identical to today.

### Phase 2 — Connect grounding to the generator (the moat) `~2h`
- Add `EmitAsync(question, QuestionGroundingContext grounding, ct)` overload that appends a **"Resolved context — use these verbatim"** block to the prompt: linked values (`Malki → Regions.Name = 'Malki'`), bilingual column hints (`title → TitleEn/TitleAr`), temporal slots, natural keys, the date-role hint.
- **Tests:** prompt-assembly test — given a grounding context with `LinkedValues = [Malki→Regions.Name]`, assert the emitted prompt contains that binding (locks the moat so it can't silently break).

### Phase 3 — Acceptance gold suite from YOUR questions `~1–2h`
- Add `Configuration/QuestionSuites/gold-minimal.json` with the exact questions you showed (EN + AR): tickets-in-Malki, title-mentions-electricity, رتب المناطق, المناطق فوق المتوسط, outages-by-affected, customers-above-avg-bills, regions-above-avg, users+roles+ticket-count — each with `ExpectedSql`/expected-shape.
- Runnable through the existing assessment harness (`CopilotAssessmentHandler.RunAssessmentAsync`) with the flag on vs off, so you **A/B the thin path against the heavy path on your real DB**.
- **Test/acceptance gate:** the thin path must match or beat the heavy path on this suite before the flag flips on by default.

### Phase 4 — Remove the unused AI workload/role providers `~2–3h`
Verified dead (declared, settings-bound, **never consumed**) — remove enum value + `RoleSettingsKeys` entry + `SettingKeys` + appsettings binding, each confirmed by a no-consumer grep before deletion:
- `AiRole.SelfCorrector`, `AiRole.Paraphraser`, `AiRole.SyntheticGenerator`, `AiRole.Frontier`
- Dormant, not DI-registered: `SchemaLinker` + `StructuralCueParser` stages (and their roles).
- **Verify before delete** `AiWorkloadType.Analysis` (may be used elsewhere in the host app — grep app-wide; remove only if unused).
- **Tests:** build green + full suite green after each removal; a DI-resolves smoke test.

### Phase 5 — (Optional) small UX lifts from the big plan `~2–3h`
- **Calibrated abstain** instead of never-refuse: if grounding maps nothing or execution fails after retry, reply *"I couldn't map 'X' to your schema — did you mean &lt;closest table/column&gt;?"* rather than guessing.
- **Transparency**: surface the generated SQL + which tables/values were matched in the response/trace (you see *why* it answered).
- **One-line grounded insight**: total / top contributor computed from the actual rows, appended to the reply.

---

## What we do NOT touch
The QuerySpec IR, the SQL compiler (and my recent refactor), the 23 repair rules, the decomposer, the coverage checker — **all stay as the fallback**, nothing deleted. If the thin path wins on your gold suite, we retire them later, deliberately, behind the same flag.

## Risks (honest, small)
- **Grounding is the real work** — if value/column linking is weak for a question, the model guesses. Mitigation: Phase 2 + the gold suite expose exactly where, and abstain (Phase 5) beats guessing.
- **Provider deletions** touch enums/settings/migrations — a missed consumer breaks startup. Mitigation: grep-confirm no consumer before each delete; build+suite green after each.
- **No fan-out/grain guard in v1** — `SUM` over a 1:N join can still over-count. Mitigation: grounding picks the right grain for most; a cheap fan-out check is a fast-follow, not v1.

## Time
- **Core (Phases 1–3): ~half a day** — and it's testable + A/B-able on your DB at that point.
- **+ Phase 4 (cleanup): ~1 day total.** Phase 5 optional.
