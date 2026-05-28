# Fine-Tuning Requirements — what no patch can fix

These are failure cases where the team verdict is **"the LLM has to learn this"** — they cannot be fixed by adding another SpecRepair phase, semantic-layer synonym, or prompt rule without becoming a worse problem. They are the explicit candidates for the LoRA / fine-tune training set when you get the model-training pipeline set up.

Each entry includes:
- **Question** — exact text the user typed
- **What the LLM produces** — the broken `QuerySpec` we keep seeing in traces
- **What it should produce** — the correct `QuerySpec`
- **Why this is fine-tune-only** — what makes patching impossible / counter-productive
- **Training pair** — ready-to-use entry for a JSONL training file

---

## 1. Filter hallucination on aggregation — `AB-AGG-SumOverdue`

**Question:** `"total amount of overdue bills"`

**What the LLM produces (consistent across runs):**
```json
{
  "intent": "data_query",
  "root": "Bills",
  "aggregations": [{ "function": "SUM", "column": "Bills.TotalAmount", "alias": "total" }],
  "filters": [
    { "column": "Bills.Status", "op": "eq", "value": "Overdue" },
    { "column": "Bills.TotalAmount", "op": "gt", "value": 0 }
  ]
}
```

The second filter (`TotalAmount > 0`) is **hallucinated** — the user never asked for it. Result: a subset of overdue bills, returning 2,973,000 instead of the correct 10,952,100.

**What it should produce:**
```json
{
  "intent": "data_query",
  "root": "Bills",
  "aggregations": [{ "function": "SUM", "column": "Bills.TotalAmount", "alias": "total" }],
  "filters": [{ "column": "Bills.Status", "op": "eq", "value": "Overdue" }]
}
```

**Why fine-tune-only:** A patch that strips "extra" filters would also strip legitimate ones. There is no syntactic signal that distinguishes "user wanted this filter" from "LLM invented it" — it's a semantic judgment the model has to make.

**Training pair:**
```jsonl
{"q": "total amount of overdue bills", "spec": {"root":"Bills","aggregations":[{"function":"SUM","column":"Bills.TotalAmount","alias":"Total"}],"filters":[{"column":"Bills.Status","op":"eq","value":"Overdue"}]}}
```

---

## 2. Group dimension vs filter confusion — `AB-GRP-RegionsByType` / `AF1-GRP-TixByPriority`

**Questions:**
- `"region counts by region type"`
- `"complaint counts broken down per priority level"`

**What the LLM produces:**
```json
{
  "root": "Regions",
  "select": ["Regions.NameEn"],
  "aggregations": [{ "function": "COUNT", "column": "*", "alias": "Counts" }],
  "filters": [{ "column": "Regions.RegionType", "op": "eq", "value": "Damascus" }],
  "groupBy": ["Regions.NameEn"]
}
```

LLM read "by region type" and put `RegionType` into a **filter** (with a hallucinated value!) instead of into `groupBy`. It then groups by `NameEn` (the displayed name) — which counts each region's own row → 14 rows, all with count=1.

**What it should produce:**
```json
{
  "root": "Regions",
  "select": ["Regions.RegionType"],
  "aggregations": [{ "function": "COUNT", "column": "*", "alias": "Count" }],
  "groupBy": ["Regions.RegionType"]
}
```

**Why fine-tune-only:** The fix requires understanding that "by X" is a *grouping* signal, not a *filtering* signal. Detecting "by X" → groupBy via patches would help, but breaks for "show me bills issued by Aleppo region" where "by" is a filter context.

**Training pairs:**
```jsonl
{"q": "region counts by region type", "spec": {"root":"Regions","select":["Regions.RegionType"],"aggregations":[{"function":"COUNT","column":"*","alias":"Count"}],"groupBy":["Regions.RegionType"]}}
{"q": "complaint counts broken down per priority level", "spec": {"root":"Tickets","select":["TicketPriorities.Name"],"aggregations":[{"function":"COUNT","column":"*","alias":"Count"}],"groupBy":["TicketPriorities.Name"]}}
```

---

## 3. Column name hallucination — `AF1-CNT-OutagesElectricity`

**Question:** `"outage count for the power service"`

**What the LLM produces:**
```json
{
  "root": "Outages",
  "aggregations": [{ "function": "COUNT", "column": "*", "alias": "OutageCount" }],
  "filters": [{ "column": "Outages.PowerServiceId", "op": "eq", "value": "Electricity" }]
}
```

`Outages.PowerServiceId` **does not exist**. The actual FK is `Outages.ServiceTypeId`. The LLM invented a column name that "sounds right" for the user's phrasing.

**What it should produce:**
```json
{
  "root": "Outages",
  "aggregations": [{ "function": "COUNT", "column": "*", "alias": "Count" }],
  "filters": [{ "column": "ServiceTypes.NameEn", "op": "eq", "value": "Electricity" }]
}
```

**Why fine-tune-only:** We could add validation that rejects unknown columns and asks the LLM to retry, but the retry would likely hallucinate again. The real fix is the LLM **must learn the actual schema**: `Outages.ServiceTypeId` exists, `Outages.PowerServiceId` does not.

**Training pair:**
```jsonl
{"q": "outage count for the power service", "spec": {"root":"Outages","aggregations":[{"function":"COUNT","column":"*","alias":"Count"}],"filters":[{"column":"ServiceTypes.NameEn","op":"eq","value":"Electricity"}]}}
```

---

## 4. Wrong-root-with-confident-SQL — `AB-CNT-Categories` (intermittent)

**Question:** `"how many ticket categories"`

**What the LLM sometimes produces:**
```json
{ "intent": "clarification", "message": "Please specify the columns you want to include in the result." }
```

LLM refuses to extract a spec, falsely thinking the question is ambiguous. (Our `InferRootFromQuestion` override fixes this when LLM picks the wrong root, but doesn't fix it when LLM refuses entirely.)

**What it should produce:**
```json
{
  "root": "TicketCategories",
  "aggregations": [{ "function": "COUNT", "column": "*", "alias": "Count" }]
}
```

**Why fine-tune-only:** The LLM's refusal is a confidence calibration issue. Telling it "do your best" in the prompt sometimes works, sometimes doesn't. Fine-tuning on the correct extraction for canonical phrasings teaches the model that "how many X" is never ambiguous when X matches a table name.

**Training pair:**
```jsonl
{"q": "how many ticket categories", "spec": {"root":"TicketCategories","aggregations":[{"function":"COUNT","column":"*","alias":"Count"}]}}
```

---

## Suggested training-set construction

When you're ready to fine-tune:

1. **Pull from `CopilotTraceHistories`** — every captured trace where `Answer` matches the user-reviewed expected result is a positive example. Every trace where it doesn't is a negative example (the LLM's broken output paired with the correct one).

2. **The 4 entries above are the seed corpus.** Expand to ~200–500 examples by:
   - Adding paraphrases of each (`"give me the total of overdue invoices"`, `"sum of past-due bills"`, etc.) — generate via a stronger LLM (GPT-4 / Claude / Gemini) using the seed entry as the canonical answer.
   - Sampling the fresh-suite questions (already paraphrases) with their hand-checked expected scalars.
   - Including the **frozen-baseline 32 passing examples** as positive examples too — fine-tuning needs to preserve what works, not just fix what's broken.

3. **JSONL format** — `{"messages": [{"role":"user","content":"<question>"},{"role":"assistant","content":"<spec JSON>"}]}` is the most portable format (OpenAI-compatible, Ollama-compatible via Unsloth/Axolotl).

4. **LoRA target**: start with `qwen2.5-coder:7b` for the SpecExtractor role. Rank 8 is usually enough for 200–500 examples; 1 epoch over the seed + 3 epochs over paraphrases.

5. **Eval gate**: after fine-tuning, the model passes only if it scores ≥ baseline on the frozen suite AND ≥ 80% on the fresh suite. Otherwise the LoRA goes back for more data.

## What patches CAN'T learn

These four categories cover the four kinds of LLM failure that no SpecRepair phase can address:

- **Hallucination of fields not asked for** (Category 1) — we can't tell what's legitimate
- **Semantic role confusion** (Category 2) — "by X" can be filter OR group depending on context
- **Schema invention** (Category 3) — model fabricates column names that pattern-match
- **Confidence miscalibration** (Category 4) — model refuses on questions it should answer

These four together account for the persistent gap between our frozen-baseline number (~84%) and our fresh-suite number (~60%). Closing this gap is the fine-tune's job.
