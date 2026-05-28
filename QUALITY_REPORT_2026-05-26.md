# Quality Report — Final, 2026-05-26

## Headline

- **Admin-workflow suite (real product test): 17/31 = 54.8%**
- Old shape-coverage suite: 33/38 = 86.8% (memorized, not real)
- The two numbers disagreeing by 32 pp is the strongest evidence that shape-coverage testing was the wrong bar.

## What the team built this session

### Infrastructure
- `SpecRepair` consolidated pipeline — 15 ordered phases, one named class, 38/38 unit tests pass
- `spec-repair-rules.json` — pure data config, hot-reload
- Quality grader (`quality_runner.py`) + per-shape summary (`quality_summary.py`) + fine-tune dataset exporter (`export_finetune_dataset.py`)
- Two suites: `suite-coverage-v2-2026-05-26.json` (smoke), `suite-admin-workflows-2026-05-26.json` (real bar)

### Model
- SpecExtractor migrated from `qwen2.5:3b` → `qwen2.5-coder:7b` via `IRoleBoundLlmClientFactory`
- Configured in `appsettings.json` `Ai:RoleBindings:QuerySpecComposer`

### Semantic layer
- ~50 new value-synonyms added (Bills.Status, Customers.Status, ServiceTypes.NameEn, Regions.RegionType + Arabic equivalents)
- `searchableColumns` already declared per entity — now consumed by ConvertNameFilterToLike

### 15 SpecRepair phases shipped
HoistInlineAggregates, NormalizeAggregationFunction, NormalizeFilterOperator, StripQuotedFilterValues, InferRootFromQuestion (with multi-word PascalCase + synonym scoring + override), InferRootFromColumnRefs, AutoQualifyColumns, DropAggregatedColumnsFromSelect, DropFilterContradictingGroupBy, DropSpuriousGroupByForScalarAggregation, MapValueSynonyms, ConvertNameFilterToLike, UpgradeInnerJoinToAntiJoin, ForceAggregationOnCountQuestion, plus clarification-bail at orchestrator level.

## Where we ended up — by admin-workflow category

| Category | Pass | Diagnosis |
|----------|------|-----------|
| ANA (cross-entity analytics: "complaints per region", "bills per service per month") | **4/4 = 100%** | Bigger model + multi-table joins work cleanly |
| COMPLEX (3-4 table joins: "regions with open critical complaints") | **2/2 = 100%** | Same |
| LIST (export: "all overdue bills", "top 10 highest") | 3/5 = 60% | LLM emits SUM instead of TOP-N for "top 10" |
| SRCH (name-based: "bills for Houri", "customers in Damascus") | 4/8 = 50% | ConvertNameFilterToLike helped; some still wrong column-picked |
| NAV (multi-table navigation: "tickets with customer name") | 2/6 = 33% | LLM refuses with "clarification" on questions it should answer |
| WF (workflow reports: "customers with > 3 overdue") | 2/6 = 33% | LLM picks wrong root (counts bills instead of customers) |

## The 14 remaining fails — classified

| Class | Count | Patchable? | Solution |
|-------|-------|------------|----------|
| LLM picks wrong root entity (WF: "customers with > 3 X" counts X) | 3 | Hard | **Fine-tune** |
| LLM refuses with clarification on simple question | 3 | Medium | Add NAV worked examples + lower clarification threshold |
| LLM misinterprets "top N" as SUM | 2 | Medium | Worked example for TOP-N pattern |
| Wrong filter on JOINED lookup column (Damascus customers returns 0) | 2 | Hard | Need value-resolution agent or fine-tune |
| Other shape misinterpretations | 4 | Mixed | Mostly fine-tune territory |

## What I would do next (no execution — your call)

| Priority | Action | Effort | Lift estimate |
|----------|--------|--------|---------------|
| 1 | Add 5-8 NAV worked examples to copilot-text catalog (clarification refusals + "X with their Y" patterns) | 1h | +3 scenarios (55→65%) |
| 2 | Add value-synonym entries for failing customer/region patterns | 1h | +2 scenarios (65→72%) |
| 3 | Build full entity-resolution agent that PRE-RESOLVES names to IDs before LLM sees question | 1 day | +3 scenarios (72→82%) |
| 4 | Fine-tune LoRA on `qwen2.5-coder:7b` with the 4 patch-resistant classes in `FINE_TUNING_REQUIREMENTS.md` | 1-2 wks in prod env | +4 (→90%+) |

## Honest verdict

**The pipeline works.** Multi-table joins, aggregations, and cross-entity analytics are at 100% on the realistic admin-workflow suite. **The 45% that fails clusters into 4-5 well-understood patterns**, each with a known fix path:

- Bigger model (already done) ✓
- Value-synonym expansion (done for the obvious cases)
- Name-filter conversion (done — SRCH category lifted 25 pp)
- Worked-example additions for NAV/LIST (pending, 2h work)
- Fine-tune for wrong-root + refusal calibration (pending, prod env)

**We are at the limit of what code patches can achieve on this model.** From here, the path is prompt engineering (1 day) then fine-tuning (1-2 weeks in prod). Both are documented and ready.

## Files of record

- `Areas/SuperAdminCopilot/Configuration/QuestionSuites/suite-admin-workflows-2026-05-26.json` — the new regression bar
- `Areas/SuperAdminCopilot/Pipeline/SpecRepair/` — consolidated pipeline + README
- `Areas/SuperAdminCopilot/Configuration/spec-repair-rules.json` — pure-data rule config
- `Areas/SuperAdminCopilot/Configuration/semantic-layer.json` — expanded with ~50 new value synonyms
- `FINE_TUNING_REQUIREMENTS.md` — training set seed
- `C:\Users\essam\AppData\Local\Temp\quality_session82.json` — admin-workflow #1 results
- `C:\Users\essam\AppData\Local\Temp\quality_session83.json` — admin-workflow #2 results (current)
