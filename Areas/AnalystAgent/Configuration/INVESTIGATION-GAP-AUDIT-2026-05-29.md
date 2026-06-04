# Investigation page gap audit — 2026-05-29

## What's captured today (the 30%)

| Concern | Captured | Stored where |
|---|---|---|
| Question text + final SQL + row count | ✓ | `CopilotTraceHistories.Question` / `.GeneratedScript` / `ExecutionPlan.LastResult` |
| Per-stage name, status, elapsed-ms, detail summary | ✓ | `ExecutionPlan.Steps[]` |
| Aggregate LLM call count, total tokens, est cost | ✓ | `CopilotTraceHistories.LlmCallCount` / `TotalPromptTokens` / `TotalCompletionTokens` / `EstimatedCostUsd` |
| Detected intent + route reason | ✓ | `ExecutionPlan.DetectedIntent` / `RouteReason` |
| Pipeline branch taken (which path fired) | ✓ | Inferred by `WorkflowGraphBuilder.DetectPath()` |
| Refusal reason (write/PII/OOS) | ✓ | `PipelineStep.TechnicalData.details.fullReason` |
| Question embedding | ✓ | `CopilotTraceHistories.QuestionEmbeddingJson` |
| Two-tab Razor UI (Run Trace timeline + Workflow Mermaid graph) | ✓ | `Investigation.cshtml` + `_InvestigationRunTrace.cshtml` + `_InvestigationPipelineGraph.cshtml` |

## What's missing (the 70%)

| # | Gap | Why it matters | Complexity |
|--:|---|---|---|
| 1 | **Per-LLM-call prompt + response text** | Today only tokens + elapsed + model are recorded. The actual prompt sent and response received are invisible. Impossible to debug a wrong answer without log greps. | **S** |
| 2 | **Decision-point scores** (VerifiedQuery top-K cosine, IntentClassifier confidence, Tool routing alternatives, Retriever top-K) | The trace records the WINNER. The runner-ups + their scores vanish. "Why did Tool X win?" → unknowable. "Was VerifiedQuery close to matching?" → unknowable. | **M** |
| 3 | **SpecRepair phase diagnostics** | `SpecRepair.Apply()` produces a `List<SpecRepairDiagnostic>` with `phase + detail` — only goes to ILogger, never reaches the trace JSON. Every "auto-fix" is invisible. | **M** |
| 4 | **Schema retrieval top-K + scores** | `SchemaSemanticRetriever` returns 3 tables with cosine scores. Trace surfaces table NAMES only. Can't explain ranking. | **S** |
| 5 | **Validator rejection details** | Errors are unstructured strings. No `[ {rule, column, reason, line} ]`. Hard to debug. | **M** |
| 6 | **LLM call failures** (safety block, timeout, parse error) | Recorded only to logs. No structured stamp on the step. | **S** |
| 7 | **Refinement context** (why retry fired, previous SQL + error diff) | Previous error truncated to 80 chars in detail. No diff between attempts. | **S** |
| 8 | **Few-shot examples injected** | When SpecExtractor includes past-question examples, those examples + their similarities are not logged. | **M** |
| 9 | **Decomposer split reasoning** | Decomposer source ("regex"/"llm") and sub-questions captured, but not the LLM's reasoning prompt/response if it fired. | **M** |
| 10 | **Executor SQL statistics** (logical reads, compile time) | Distinguishing DB-side vs pipeline bottleneck is currently impossible. | **M** |
| 11 | **Compiler parameter values** (`@p0=X`) | SQL shown without parameter binding. Reproduction requires re-running. | **S** |
| 12 | **Trace schema versioning** | No `_traceVersion` field. Old traces will deserialize ambiguously as schema evolves. | **S** |

## What I'll implement now (prioritised)

**Tier 1 — highest leverage, do first:**
- #1 LLM prompt+response capture per call
- #3 SpecRepair diagnostics surfacing  
- #2 Decision-point scores (top-K for VerifiedQuery / Retriever / Tool / IntentClassifier)

**Tier 2 — done in same pass since cheap:**
- #6 LLM failure capture
- #11 Parameter values
- #12 Trace versioning

**Tier 3 — defer to a follow-up (touches more files):**
- #5 Structured validator errors
- #10 Executor SQL statistics
- #8 Few-shot examples in trace
- #9 Decomposer reasoning

## Razor page changes

Two new collapsible sections per stage in `_InvestigationRunTrace.cshtml`:
- **"LLM calls"** — list of `{ model, promptTokens, completionTokens, elapsedMs, costUsd, viewPrompt, viewResponse }` with click-to-expand
- **"Decisions"** — table of `{ chosen, alternatives: [name, score], reason }`
- **"Phases that fired"** (for SpecRepair stage) — list of `{ phase, detail, before→after diff }`

Plus a header strip showing aggregate "X LLM calls / Y stages / Z auto-fixes".
