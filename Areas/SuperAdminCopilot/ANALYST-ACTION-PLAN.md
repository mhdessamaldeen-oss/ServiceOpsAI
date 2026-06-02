# ServiceOpsAI — Analyst Action Plan

*Team design (6 architects + chief synthesis), grounded in the real codebase + an expert panel audit. Owner reviews and approves; the team owns the technical decisions.*

## Why this plan
The current system is **B+ engineering / C+ as a trustworthy analyst**: a strong, well-grounded deterministic NL→SQL engine that (1) is graded by *execution-survival*, not *accuracy*; (2) silently double-counts aggregates across 1:N joins; (3) is still a *translator* (no named measures, no grain); (4) has an MSA-only Arabic layer; and (5) has built-but-switched-off accuracy levers. This plan turns it into a **config-driven, multi-tier intelligent analyst** without throwing away the moat (grounding) or the safety net (IR + repair bus).

Three hard requirements drive every decision: **(A)** add a column / table / new DB / new app by editing **configuration only** — never core logic; **(B)** keep **multi-tier** models (local + strong), each answering within its limits; **(C)** become an **analyst** (measures + grain + insight), not a SQL translator.

## North Star
ONE grounding pass feeds a per-question tier **router**. **Weak/Medium** tiers run the proven `QuerySpec → compiler → tier-gated repair` safety net; the **Strong** tier runs **generate-then-verify** (N-sample direct SQL, execution-vote, self-debug). Every tier ends in one `VerifiedResult` through one shared gate stack: AST validator + intent guard + a **new grain-aware fan-out linter** that reads FK cardinality derived for free from PK/UNIQUE constraints, so `SUM/AVG/COUNT` can never silently multi-count. Truth is **measured** — execution-accuracy on a gold benchmark gates every merge in CI, sliced per tier/shape/tag. Onboarding is **config-only**: the engine auto-introspects everything inferable; the owner authors one per-app manifest for the six things no algorithm can know. Introspection + execution become dialect seams (like the existing compiler dialect), so SQL Server→Postgres and app→app are config moves.

## Multi-tier model
Tiers are **config** (`tiers.json`), each = `{model binding + strategy contract}`, not a hardcoded scalar.
- **WEAK** (local 7B, `ConstrainAndRepair`, repairTier=Weak): full IR→compiler→all-23-repair-rules, single deterministic call. The manifest *is* its competence.
- **MEDIUM** (32B / cloud-mini, `ConstrainAndRepair`, repairTier=Medium): vocab crutches skip; structural+FK rules stay; advanced shapes (WINDOW/RECURSIVE) route to direct SQL with N=2.
- **STRONG** (frontier, `GenerateThenVerify`, repairTier=Strong): direct SQL, N=5 samples at temp>0, execution-vote by result fingerprint, self-debug ≤2 rounds; only universal SQL-law rules (incl. the grain linter) fire.

The **grain/fan-out linter and the EX gate are tier-independent** — join arithmetic is a law, not a crutch. A cheap-first router starts low and **escalates** on a tripped gate; **degrades** to "reduced capability, unverified" when the strong tier is down/over-budget; and **abstains** (naming the gap) rather than ever returning a confident wrong answer. Over time, EX-verified strong answers migrate **down** the ladder via the learned corpus (a verified strong answer becomes a cheap weak-tier hit).

## The Config Contract — *what you add, where, what's auto-derived*
| To add… | You edit… | The system auto-derives… |
|---|---|---|
| **A column** | Nothing (re-run schema inference). Optional 1-line description/AR synonym in manifest `TableOverride.Columns[]` if the guess is wrong | type, nullability, role (PK/FK/label/natural-key/audit), date-role, PII flag, EN synonyms; both query paths + fan-out linter see it |
| **A table** | Nothing for basic Q&A (auto-synthesized entity). Add an `EntityDefinition` only for business meaning, AR synonyms, sensitive cols, measures | entity skeleton, FK join edges **and their cardinality (1:1/N:1/1:N)**, lookup/bridge/person class, soft-delete column; linter protects it |
| **A named measure** (revenue, collection_rate, MTTR) | `metrics[]`: `{name, entity, agg, expression, grain?, filters?, format?, synonymsEn, synonymsAr}` | alias formatting (currency/%/duration), planner-prompt advertisement, grain anchor, fan-out safety (pre-agg subquery across 1:N) |
| **A synonym / concept** | manifest `synonyms[]` (bilingual values) or `conceptPatterns[]` ("overdue"/"stale") | EN synonyms auto-generated; case-insensitive index; you add only AR/jargon a heuristic can't |
| **A model / capability tier** | `tiers.json`: `{id, role, strategy, repairTier, selfConsistencyN, verify, maxCostUsd, timeoutSec}` | role→provider→model resolution, router ladder position, repairTier default, trace labelling |
| **Routing policy** | `copilot-options.json routerMode` + `tier-routing.json` (shape→tier, tag→tier) | difficulty signals (shape, candidate-table count, decomposer count, confidence, Arabic flag) already computed; router reads + traces *why* |
| **A gold eval case / raise the CI bar** | `Configuration/QuestionSuites/gold/*.json` `{Question(En/Ar), Shape, Tags, Tier, Grain, gold.{sqlMssql|sqlPostgres|answerValue}, facets, abstain?}` + `eval-thresholds.json` | grading modality, partial-credit facet score, per-tier/shape/tag scorecard, CI grade |
| **Row-level / tenant security** | manifest `entities[].tenantColumn` + tenant-claim mapping | candidate tenant columns flagged by the validator; injected like the existing soft-delete filter |
| **A new DB / new engine** | New DB: connection string + `apps/<appId>/` bundle + `active-app.json`. New engine: `Database: 'Postgres'` (one knob) | entire `schema-inferred.json`; catalog + execution + compile dialect from the one knob |
| **Port to another app / tune UX** | Copy `apps/<appId>/`, author `manifest.json`, point `active-app.json`; UX via `analyst-ux.json` `{confidence thresholds, chartHints[], insights, clarificationTemplates EN/AR}` | validator lists residual authored gaps; chart/confidence fall back to today's behavior when absent (never a regression) |

## Roadmap

### Phase 0 — Truth signal: gold benchmark + EX/facet wiring + CI gate `[L]`
**Goal:** Stop shipping blind — execution-*accuracy* on a gold set becomes the headline, graded per tier/shape/tag, enforced in CI.
**Ships:** additive `GoldCase` schema; wire the dead `ExpectationVerifier` for partial credit; `ErrorClassifier` + taxonomy; headless `EvalRunner` + `.github/workflows/eval-gate.yml` + `eval-thresholds.json` + PR-diff comment; migrate existing suites into `gold/`.
**Owner gains:** append a `GoldCase`; raise/lower the bar; add a taxonomy bucket — all zero-code.
**Proves:** CI fails a PR when EX drops or a must-pass case regresses; metric flips from survival (94.3%) to accuracy. **Depends on:** none.

### Phase 1 — Grain keystone + fan-out linter (the #1 silent-wrong-answer fix) `[L]`
**Goal:** Derive FK cardinality for free; add a grain-aware safety linter as a final pre-execution gate on both paths.
**Ships:** `Cardinality {1:1,N:1,1:N}` in `ForeignKeyGraph` (zero config); `JoinPlan.CardinalityByEdge/HasFanOut`; `ISqlGrainLinter` (verifier, never rewrites, reuses ScriptDom); fan-out BLOCK rule → remedy or **abstain**; optional `grain.json` override; `GRAIN_FANOUT` gold cases.
**Owner gains:** `grain.json` escape-hatch; `GrainLinterFanOutSeverity` + `Remedy` knobs.
**Proves:** fan-out suite goes silently-wrong → correct/abstained; `GRAIN_FANOUT` count → ~0; no EX regression. **Depends on:** Phase 0.

### Phase 2 — Analyst brain: named measures, symmetric aggregates, utility-ops domain pack, bilingual `[L]`
**Goal:** Turn translator → analyst. Every aggregate becomes a named, grain-aware, fan-out-safe, bilingual measure in one JSON block.
**Ships:** extended `MetricDefinition` (grain, format, EN/AR synonyms); `SemanticExpander` wraps `agg()` + format; **additive-at-grain guard** (pre-aggregated subquery across 1:N, portable default; `SUM(DISTINCT)` symmetric-aggregate on strong tier); `domain-packs/utility-ops.json` (revenue, collection_rate, overdue, payment_lag, outage_duration/frequency, affected_customers, consumption, CSAT) as copy-paste config; `insights[]` (trend/anomaly/compare); formatted narration (1240000 → 1.24M SYP).
**Owner gains:** `metrics[]` with grain+format+synonyms; `domainPackPaths[]`; `insights[]`. **Proves:** measure gold cases pass EX incl. cross-grain; measures resolve by name in the planner prompt. **Depends on:** Phases 1, 0.

### Phase 3 — Config-only onboarding: introspection dialect seam + app bundle + validator `[L]`
**Goal:** Adding a column/table/DB/engine/app is config-only.
**Ships:** `ISchemaDialect` seam (MSSQL = today's exact SQL; Postgres = pg_catalog) + factory on the `Database` knob; widen `IDbConnectionFactory` to `IDbConnection`; cardinality captured at introspection into `schema-inferred.json`; `Configuration/apps/<appId>/` bundles + `active-app.json`; unify `schema-overrides.json` + authored `semantic-layer.json` into one `manifest.json` (dual-load deprecation window); `OnboardingValidator` (ranked, capped checklist); **stand up Postgres end-to-end** behind the knob.
**Owner gains:** one `manifest.json`; `active-app.json`; `tenantColumn`; the engine knob drives introspection+execution+compile. **Proves:** copy a bundle + point at a fresh Postgres DB → same gold suite passes → zero core edits. **Depends on:** Phases 1, 0.

### Phase 4 — Multi-tier router + Strong-tier generate-then-verify, unified behind `VerifiedResult` `[XL]`
**Goal:** Replace the global model-strength scalar with a config tier catalog + cheap-first router; promote direct SQL to a first-class Strong path; demote IR+compiler to the Weak/Medium net.
**Ships:** `tiers.json`; wire `AiRole.Frontier/SelfCorrector`; collapse the two tier enums (with a repair-firing snapshot test first); `ITierRouter` (cheap-first + escalation, `routerMode:predict`); `GenerateJsonManyAsync` (N-sample) in the single host bridge; promote `LlmDirectSqlEmitter` → `IStrongSqlGenerator`; `IExecutionVoter` (result-fingerprint plurality); bounded self-debug; unified `VerifiedResult {Sql, ExecutionResult, LintVerdict, Provenance, Confidence, AgreementRatio, GrainNotes}`; graceful degradation + abstain.
**Owner gains:** `tiers.json` (strategy + sampling + budgets); `routerMode` + per-shape start-tier; sampling temperature. **Proves:** lands as a behavior-preserving no-op first (baseline stays green), then the scorecard shows hard shapes gain EX on Strong while easy shapes stay correct at Weak cost. **Depends on:** Phases 0 (mandatory) + 1.

### Phase 5 — Analyst UX: multi-turn memory, calibrated abstention, charts, grounded narration `[L]`
**Goal:** Replace never-refuse + hardcoded confidence with a calibrated analyst.
**Ships:** `IConversationContext` (bounded turn ring, anaphora, add-vs-replace); `IConfidenceCalibrator` (retriever margin, retry-fired, coverage gap, fuzzy-entity sim, escape-valve, fan-out risk, strong-tier agreement) replacing the `0.85/0.6/0.7` literals; `analyst-ux.json` thresholds + bilingual clarification templates; externalized `chartHints[]` (heuristic fallback); `IInsightNarrator` (top contributor/share, trend, outliers) feeding *facts* not raw rows; abstention as a first-class scorecard metric.
**Owner gains:** `analyst-ux.json`; `ConversationLookbackTurns`; `carryEntities`. **Proves:** multi-turn + ambiguity gold suite shows calibrated abstention beats both never-refuse and over-clarifying; absent config = today's behavior. **Depends on:** Phases 0, 4, 1.

### Phase 6 — Self-improving memory + drift detection (close the loop) `[M]`
**Goal:** Learn from EX-verified successes; catch schema/data drift.
**Ships:** `ExPass` on traces; tighten `PastQuestionStore` corpus to EX-verified-only; `FixtureMinter` (every gold failure → must-pass fixture); `schema-fingerprint.json` drift trip; gold-VALUE **canaries** (nightly cron, data-moved vs logic-broke); per-tier cost scorecard showing strong→cheap migration.
**Owner gains:** PastQuestionRag knobs; `canary` tags; committed fingerprint. **Proves:** a paraphrase resolves from the EX-verified corpus at a cheaper tier sub-second; a schema rename trips the fingerprint; the corpus provably can't be poisoned. **Depends on:** Phases 0, 4.

## What we reuse (the moat — extended, never retired)
`ExecutionAccuracyChecker` (the grader moat) · `ExpectationVerifier` (→ shared `SqlShapeFacts`) · `SchemaIntrospector`/`SchemaInferenceGenerator`/`SynthesizeMissingEntities`/`SchemaDriftLinter` · `ForeignKeyGraph` + snapshot constraints (cardinality derived from these) · `LlmDirectSqlEmitter` (→ Strong generator verbatim) · `SqlAstValidator`/`SqlIntentGuard`/the executor chain · `RoleBoundLlmClientFactory` + `AiRoleBindings` + `PlannerTierDeriver` · the `SqlDialectFactory` pattern (mirrored for introspection) · `ConversationSpecMemory`/`RefinementDetector` · `PastQuestionStore` + `UsePastQuestionRag` · `CopilotTraceHistory` · `score-correctness.ps1` · the `Database` knob + tested Postgres compiler dialect.

## What we retire / demote (nothing in the IR/compiler/repair stack is deleted)
`schema-overrides.json` + authored `semantic-layer.json` → one `manifest.json` · `SqlConnection`-typed factory → `IDbConnection` · single-app path assumption → app bundles · global model-strength scalar → `tiers.json` authoritative (Profile tier stays as default) · two tier enums → one · hardcoded confidence literals → calibrator · never-refuse → calibrated abstain · survival (94.3%) → accuracy-on-gold as the gate. The 23 repair rules + escape valve **stay** as the Weak/Medium net and Strong backstop.

## Risks & assumptions
- **Measurement gap is the root risk** — until Phase 0 lands, every later change can look green while lowering accuracy. Phase 0 is non-negotiably first; Phases 4 & 5 must not precede it.
- **Cardinality is only as good as DB constraints** — app-enforced 1:1 without a UNIQUE reads as 1:N (safe over-guard); no-FK legacy DBs → linter degrades BLOCK→WARN (never refuse for missing metadata). Mitigation: `grain.json` override + loud `GrainNotes` + fan-out gold suite.
- **Widening to `IDbConnection`** touches every cast site (a miss compiles, throws on Postgres). Mitigation: SqlServer default byte-identical, Postgres gated, per-dialect tests.
- **Collapsing tier enums** touches repair-rule gating. Mitigation: a per-tier repair-firing **snapshot test before** the change.
- **Strong-tier N-sampling** multiplies cost/latency; float/large-set fingerprints can false-split. Mitigation: N=5 strong-only, cost gate, float-rounding + set canonicalization, stamp-divergent-and-abstain.
- **Calibration without gold is guesswork**; over-clarifying erodes trust; anaphora can leak stale filters. Mitigation: tune against Phase-0 gold, answer-leaning default, add-vs-replace via cues.
- **Manifest re-home is security-critical** (thin sensitive/tenant = leaks). Mitigation: dual-load window; undeclared PII/tenant → WARN, can block startup in ConfiguredOnly mode.
- **CI cost**: PR gate runs must-pass + weak/medium subset deterministically; full strong-tier sweep nightly.
- **Assumptions**: a deterministic seeded test DB exists for CI EX; the owner authors gold SQL/values for hard fan-out cases (gold.Grain + value canaries cross-check); multi-turn memory is process-local (production multi-instance needs a shared store); the embedder fail-open invariant is preserved everywhere.
- **Effort honesty**: Phases 0–3, 5–6 are L/M and independently shippable; **Phase 4 is XL and riskiest**, sequenced *after* the gate, grain fix, and analyst brain so it lands on a measured, fan-out-safe foundation.
