# AnalystAgent — Configuration

Every file here is **config-driven on purpose**: you adapt the copilot to a different database, a new
language, or different behavior by editing JSON — not by recompiling. That's the standing mandate
(no hardcoded vocabulary or per-table logic in code).

Each file is loaded by live code (verified 2026-06-03). Below is what each one is *for*, grouped by
how permanent it is.

> ⚠️ Do **not** add `//` comments inside the `.json` files — several are loaded by strict-JSON
> providers that reject comments. Document here instead.

---

## 1. Schema & domain knowledge — PERMANENT (every NL→SQL system needs this)

The model knows SQL but not *your* database. These teach it your schema and vocabulary.

| File | Purpose | Consumer |
|---|---|---|
| `schema-inferred.json` | Auto-generated map of the DB: tables, columns, keys, soft-delete column, PII flags, lookup/fact/bridge classification, date roles. **Generated** — don't hand-edit. | `SchemaKnowledge` |
| `schema-overrides.json` | Your **manual corrections** to the inferred map (survives a regenerate). | `SchemaKnowledge` |
| `semantic-layer.json` | Business dictionary: "complaints"→`Tickets`, "income"→`Bills.TotalAmount`, synonyms (incl. Arabic), metrics. | `SemanticLayer` |
| `fk-role-patterns.json` | FK verb-roles by naming: `CreatedBy*`=creator, `AssignedTo*`=assignee → "created by X" vs "assigned to X" resolve correctly. | `SchemaInferenceGenerator` |

## 2. Control & catalogs — STANDARD (normal for any LLM app)

| File | Purpose | Consumer |
|---|---|---|
| `copilot-options.json` | The knobs: table-slice size, similarity thresholds, SQL dialect, retry limits, etc. | `CopilotOptionsLoader` |
| `copilot-text.json` | The LLM instructions (SQL-gen prompt, explainer prompt) + the words the copilot says (refusals, replies). | `CopilotTextCatalog` |
| `verified-queries.json` | Curated (question→SQL) cheat-sheet. A close match uses the SQL directly and skips the LLM. | `VerifiedQueryStore` |
| `copilot-tools.json` | External tool registry (weather, FX, …). **Seed-only** — read once to populate the DB table. | `DbSeeder` |
| `embeddings/` | Persisted similarity vectors (table linking). Generated; gitignored. | `SchemaEmbeddingStore` |

## 3. Safety — PERMANENT (must be code/config, never a model judgment)

| File | Purpose | Consumer |
|---|---|---|
| `write-intent-verbs.json` | The danger words (delete/drop/update/truncate, EN+AR). Refuse write-intent **before** any LLM call. | `WriteIntentGuard` |

## 4. ⚠️ Weak-model scaffolding — CANDIDATES TO RETIRE on a stronger model

These do NLU a capable model does by itself — intent/shape classification, "is this a greeting?",
"is this compound?". They exist to pre-chew the question for the local **7B (qwen2.5-coder)**. On a
stronger model (cloud, or a larger local), the model should do this and these files can be retired —
fewer files, and truer to "the model is not the weak link; stop building crutches."

**Decision (2026-06-03):** keep for now (real weight on the 7B); retire as the model tier rises.
Retiring is *behavior-affecting* — verify intent/shape detection against a live model run first.

| File | Purpose | Consumer | Retire when |
|---|---|---|---|
| `linguistic-cues.json` | Intent vocab: count/average/compare/"this month"/lifecycle verbs (EN+AR). | `LinguisticRegistry` → `QuestionGrounder` | model classifies intent reliably |
| `conversational-cues.json` | Greeting / "what can you do" / thanks detection → canned reply, no SQL. | `ConversationalHandler` | model (or a cheap classifier) routes these |
| `decomposition-cues.json` | Compound-question split patterns ("open AND closed …"). | `HeuristicDecomposer` | model splits compound questions |
| `shape-classifier.json` | Labels question shape (COUNT/TOPN/JOIN/TIMESERIES…). | `QuestionGrounder` | model infers shape from the question |
