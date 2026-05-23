# Building a Copilot — An Abstraction-Level Engineering Guide

A self-contained playbook for designing and shipping a production-grade copilot. This document is **portable** — it does not depend on any specific codebase. Read it end-to-end the first time; use the table of contents as a reference afterward.

---

## Table of Contents

1. [What Is a Copilot (Abstraction)](#1-what-is-a-copilot-abstraction)
2. [The Universal Pipeline](#2-the-universal-pipeline)
3. [The AI vs. Deterministic Split](#3-the-ai-vs-deterministic-split)
4. [The Semantic Layer (the secret weapon)](#4-the-semantic-layer-the-secret-weapon)
5. [Schema-Aware Copilots: Introspection + FK Graph](#5-schema-aware-copilots-introspection--fk-graph)
6. [Recommended Packages — The "Don't Build From Scratch" Stack](#6-recommended-packages--the-dont-build-from-scratch-stack)
7. [Question Taxonomy (10 Levels)](#7-question-taxonomy-10-levels)
8. [Guardrails as Code](#8-guardrails-as-code)
9. [Evaluation Harness](#9-evaluation-harness)
10. [Caching Strategy](#10-caching-strategy)
11. [Multi-Agent Decomposition](#11-multi-agent-decomposition)
12. [Configuration Per Domain (the reuse story)](#12-configuration-per-domain-the-reuse-story)
13. [Anti-Patterns](#13-anti-patterns)
14. [Decision Checklist Before You Start](#14-decision-checklist-before-you-start)
15. [Glossary](#15-glossary)

---

## 1. What Is a Copilot (Abstraction)

A **copilot** is an *intent-driven orchestrator* that turns natural-language questions into grounded, validated actions or answers using one or more domain backends (a database, a docs corpus, a ticket system, a code repository, an API, or several of these together).

A copilot is **not** just an LLM with a prompt. It is a system in which the LLM is one component among several, and most of the safety, accuracy, and reliability come from the *non-LLM* parts.

### The Universal Pattern

```
Question  →  Intent  →  Plan  →  Grounded Action  →  Validated Result  →  Explanation
```

Every successful copilot — text-to-SQL, docs Q&A, ticket triage, code assistant, ops bot — is a specialization of this pattern.

### Copilot vs. Chatbot vs. Agent

| Term | What it is | Example |
|---|---|---|
| **Chatbot** | LLM + prompt; conversational only | A customer-support FAQ bot |
| **Copilot** | LLM orchestrated with retrieval, tools, validation, guardrails | Talk-to-your-database, code copilot, docs copilot |
| **Agent** | A copilot that plans multi-step actions and acts autonomously over time | A research agent, an SRE auto-remediation bot |

A copilot is a constrained, purpose-built agent. An agent is a copilot that has been given more autonomy, more tools, and a longer planning horizon. The architectural ideas in this guide apply to all three; copilots are the safe default starting point.

---

## 2. The Universal Pipeline

```
┌────────────────────────────────────────────────────────────────────┐
│                         User Question                              │
└────────────────────────────────────────────────────────────────────┘
                                ↓
                    ┌───────────────────────┐
                    │ [1] Intent Classifier │   AI (LLM)
                    └───────────────────────┘
                                ↓
                    ┌──────────────────────────────────┐
                    │ [2] Schema/Context Retriever     │   AI (embeddings + vector search)
                    └──────────────────────────────────┘
                                ↓
                    ┌───────────────────────────────────────┐
                    │ [3] Planner — emits structured spec   │   AI (LLM, JSON-constrained)
                    └───────────────────────────────────────┘
                                ↓
                    ┌────────────────────────────────────────┐
                    │ [4] Compiler — spec → SQL/API/tool     │   Deterministic
                    └────────────────────────────────────────┘
                                ↓
                    ┌──────────────────────────────────────┐
                    │ [5] Validator — parse, allowlist     │   Deterministic
                    └──────────────────────────────────────┘
                                ↓
                    ┌────────────────────────────────────────┐
                    │ [6] Executor — sandbox, read-only      │   Deterministic
                    └────────────────────────────────────────┘
                                ↓
              error?  ────────► [7] Self-Corrector (capped retries) → back to [3] or [4]
                                ↓ ok
                    ┌───────────────────────────────────────┐
                    │ [8] Explainer — natural-language reply│   AI (LLM)
                    └───────────────────────────────────────┘
                                ↓
                         Answer + citations + SQL/tool trace
```

### Where the cross-cutting concerns live

- **Caching**: at [2] (schema retrieval cache), at the input of [3] (semantic cache: question-embedding → spec/SQL), and at [6] (result cache).
- **Guardrails**: applied at [4] (inject security filters), [5] (parse and allowlist), [6] (read-only role, cost cap, timeout), and [8] (PII redaction).
- **Telemetry**: log at every stage. Every step's input, output, latency, token cost, and error must be queryable.

### Annotations on each step

- **[1] Intent Classifier** — Decide if this is a data lookup, a metadata question, a troubleshooting question, an unsupported request (write/predict/opinion), or chit-chat. Cheap, fast LLM call.
- **[2] Schema/Context Retriever** — Pull only the *relevant* slice of the domain (top-K tables, columns, FKs, docs, examples). Never dump the whole schema.
- **[3] Planner** — The LLM emits a **structured JSON spec** (entities, metrics, filters, group-by, time range, top-N) — never raw SQL or raw API calls. JSON-mode / function-calling / structured outputs is mandatory here.
- **[4] Compiler** — Plain code translates the JSON spec into the target language (SQL, GraphQL, REST call, tool invocation). This is the choke point: predictable, auditable, secure.
- **[5] Validator** — Parse the compiled output (e.g., AST-level SQL parse), check every referenced table/column exists, enforce the operation allowlist, run static cost analysis (`EXPLAIN`).
- **[6] Executor** — Run under a least-privileged identity. Sandbox. Hard timeouts. Hard row caps.
- **[7] Self-Corrector** — On error or empty/anomalous result, feed the error back to the LLM with the schema and ask for a single revision. Cap retries (2–3) to bound latency and cost.
- **[8] Explainer** — Summarize the result in the user's language, cite the tables/docs touched, and surface the generated SQL/call for trust.

---

## 3. The AI vs. Deterministic Split

The single most important architectural decision: **decide which steps use a model and which steps are plain code, and never blur the line.**

| Step | AI? | Why |
|---|---|---|
| Intent classification | LLM | Natural language understanding |
| Schema/context retrieval | Embedding model + vector search | Similarity ranking |
| Plan generation | LLM (constrained to JSON schema) | Reasoning over user intent |
| Compilation to SQL/API | **Deterministic** | Predictability, security, auditability |
| Validation | **Deterministic** (parser, allowlist) | Safety guarantees must be provable |
| Execution | **Deterministic** | Auditable, controllable |
| Self-correction | LLM (bounded retries) | Reasoning over errors |
| Answer summarization | LLM | Natural language generation |

### The rule, in one line

> **LLMs pick intent and entities. Deterministic code does the dangerous work.**

If a step needs to be *correct under adversarial inputs* (security filtering, SQL parsing, permission enforcement, cost capping), it must be deterministic. If a step needs to be *flexible under fuzzy inputs* (understanding the question, ranking schema relevance, summarizing results), it can be an LLM.

### What "constrained" means for the planner

Do not let the LLM emit free-form SQL. Force it through a structured-output mode (JSON schema, function calling, tool calling). Example shape:

```json
{
  "intent": "aggregate",
  "entities": ["Orders", "Customers"],
  "metric": { "fn": "SUM", "column": "Orders.Total" },
  "filters": [
    { "column": "Orders.Status", "op": "=", "value": "paid" },
    { "column": "Orders.CreatedAt", "op": "between", "value": ["2026-04-01", "2026-04-30"] }
  ],
  "group_by": ["Customers.Region"],
  "order_by": [{ "column": "metric", "direction": "desc" }],
  "limit": 10
}
```

The compiler turns this into SQL. The LLM never types `SELECT`.

---

## 4. The Semantic Layer (the secret weapon)

The semantic layer is the single biggest accuracy multiplier in a copilot. It is a per-domain configuration that maps the user's vocabulary to canonical entities, metrics, dimensions, and join paths.

Without a semantic layer, the LLM invents column names, invents joins, invents aggregation rules. With a semantic layer, the LLM only *picks* names from a finite, declared menu.

### What goes in a semantic layer

```yaml
entities:
  Customer:
    table: dbo.Customers
    primary_key: Id
    synonyms: [client, buyer, account, user]
  Order:
    table: dbo.SalesOrders
    primary_key: Id
    synonyms: [purchase, sale, transaction]

dimensions:
  region:
    column: Customer.Region
    synonyms: [area, territory, geo]
  month:
    expression: "DATETRUNC(month, Order.CreatedAt)"

metrics:
  revenue:
    expression: "SUM(Order.Total)"
    filters: [{ column: "Order.Status", op: "=", value: "paid" }]
    synonyms: [sales, income, earnings]
  order_count:
    expression: "COUNT(Order.Id)"
    synonyms: [orders, transactions]

relationships:
  - from: Order.CustomerId
    to:   Customer.Id
    kind: many_to_one

synonyms:
  active_customer:
    definition: "Customer with at least one paid order in the last 90 days"
    expression: "EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerId = Customer.Id AND o.Status = 'paid' AND o.CreatedAt > DATEADD(day, -90, GETDATE()))"
```

### Why this matters

When the user asks *"how much did each client spend last month"*:

- `client` → `Customer` (synonym lookup)
- `spend` → `revenue` metric → `SUM(Order.Total) WHERE Status='paid'` (no LLM invention)
- `last month` → `BETWEEN <start of last month> AND <end of last month>` (deterministic date math)
- Join `Order.CustomerId → Customer.Id` (declared relationship)

The LLM never invents `SUM`, never picks the wrong join, never confuses `Total` with `Subtotal`.

### Reference frameworks (study these, even if you build your own)

- **Cube.js** — open-source semantic layer, headless analytics
- **dbt Semantic Layer** — defines metrics on top of dbt models
- **LookML** (Looker) — the original; entities, measures, dimensions, joins
- **MetricFlow** (Transform → dbt) — metric definitions decoupled from BI tool

You do not have to use these — but **read their docs**. The patterns transfer.

---

## 5. Schema-Aware Copilots: Introspection + FK Graph

For DB-backed copilots, the engine should *learn* each database's structure automatically. You do not hardcode tables.

### Introspection

SQL Server gives you everything via `INFORMATION_SCHEMA` and `sys.*`:

```sql
-- Tables
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE';

-- Columns + types
SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS;

-- Foreign keys (the gold)
SELECT
    fk.name                    AS fk_name,
    sp.name + '.' + tp.name    AS parent_table,
    cp.name                    AS parent_column,
    sr.name + '.' + tr.name    AS referenced_table,
    cr.name                    AS referenced_column
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables    tp ON fkc.parent_object_id = tp.object_id
JOIN sys.schemas   sp ON tp.schema_id = sp.schema_id
JOIN sys.columns   cp ON fkc.parent_object_id = cp.object_id  AND fkc.parent_column_id = cp.column_id
JOIN sys.tables    tr ON fkc.referenced_object_id = tr.object_id
JOIN sys.schemas   sr ON tr.schema_id = sr.schema_id
JOIN sys.columns   cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id;
```

PostgreSQL: `information_schema.tables`, `information_schema.columns`, `information_schema.referential_constraints` + `key_column_usage`. Other engines have equivalents.

### Build an FK Graph

Turn the FK list into a graph in memory:

```
Customer ──CustomerId──> Order ──OrderId──> OrderLine ──ProductId──> Product
                              └──PaymentId──> Payment
```

Now when the user asks *"orders with customer email"*:

1. Identify entities: `Order`, `Customer`.
2. Walk the graph; find path `Order.CustomerId → Customer.Id`.
3. Compiler emits the JOIN automatically.

### Embed each table + its relationships

For each table, embed a description into the vector store:

```
Table: dbo.SalesOrders
Purpose: One row per customer purchase.
Columns:
  - Id (int, PK)
  - CustomerId (int, FK → dbo.Customers.Id)
  - Total (decimal(18,2))
  - Status (varchar; sample values: 'pending', 'paid', 'cancelled')
  - CreatedAt (datetime)
Related to:
  - dbo.Customers (via CustomerId)
  - dbo.SalesOrderLines (via OrderId)
  - dbo.Payments (via PaymentId)
Approx. row count: 2,300,000
```

### Edge cases experts handle

| Problem | Solution |
|---|---|
| No FKs declared (legacy schema) | Infer FKs by name match (`X.CustomerId` ≈ `Customers.Id`); have the LLM confirm during onboarding |
| Multiple paths between two tables | Default to shortest path; if ambiguous, ask the user once and remember the choice |
| Junction tables (many-to-many) | Detect via 2 FKs + composite PK pattern; treat as transparent join hops |
| Soft deletes (`IsDeleted` column) | Auto-inject `WHERE IsDeleted = 0` at compile time via the semantic layer |
| Views vs. tables | Treat views as first-class entities — they are often cleaner than raw tables |
| Ambiguous column names (`Id` everywhere) | Always alias tables in compiled SQL; never emit unqualified columns |
| Reserved words / case sensitivity | Always quote identifiers per dialect (`[Order]`, `"order"`, `` `order` ``) |
| Very wide tables | Embed columns in groups; retrieve only the columns relevant to the question |

### The reuse story

Same engine, different DB, **zero code change**. To onboard a new database:

1. Provide a connection string.
2. Engine introspects → builds FK graph → embeds schema → ready in minutes.
3. Optional human review of synonyms and metric definitions.

---

## 6. Recommended Packages — The "Don't Build From Scratch" Stack

You should write the *glue, the semantic layer, and the guardrails*. Everything else exists.

### .NET-first stack

| Package | Role | What it handles for you |
|---|---|---|
| `Microsoft.SemanticKernel` | Orchestration | Prompt templating, function/tool calling, planners, conversation memory, retry policies, plugin model. Use it for the whole copilot orchestration layer. |
| `Microsoft.Extensions.AI` | Provider abstraction | Unified `IChatClient` / `IEmbeddingGenerator` interface across OpenAI, Azure OpenAI, Anthropic, Ollama, AWS Bedrock. Swap providers without rewriting logic. |
| `Microsoft.KernelMemory` | RAG pipeline | End-to-end document/schema ingestion, chunking, embedding, vector storage, retrieval, citation tracking. The fastest way to ship RAG in .NET. |
| `Qdrant.Client`, Azure AI Search SDK, `Pgvector.EntityFrameworkCore` | Vector DB clients | Approximate nearest-neighbor search at millisecond latency over millions of vectors. Pick one based on your hosting. |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | T-SQL parser | Parses SQL into an AST; lets you statically validate every referenced table/column, enforce a `SELECT`-only allowlist, and reject dynamic SQL. Ships with SQL Server tooling. |
| `Polly` | Resilience | Retry, circuit breaker, timeout, fallback, bulkhead. Wrap every LLM call and every DB call. They will fail. |
| `QuikGraph` (optional) | Graph algorithms | FK graph, shortest-path joins, cycle detection. Useful when join inference gets non-trivial. |
| `System.Text.Json` source generators | Structured outputs | Use with the LLM's JSON-mode / function-calling to deserialize planner output safely. |

### Python equivalents (for reference and study)

| Package | Role |
|---|---|
| **LangChain / LangGraph** | Agent orchestration; LangGraph is the state-machine variant for multi-step agents |
| **LlamaIndex** | RAG-first framework; strong indexing + retrieval primitives |
| **Vanna.AI** | Purpose-built text-to-SQL with RAG over schema; the cleanest open-source reference for DB copilots |
| **NeMo Guardrails** (NVIDIA) | Programmable rails for input/output filtering, topic restriction, jailbreak defense |
| **Instructor / Pydantic AI** | Constrained, type-safe structured outputs from LLMs |
| **DSPy** | Declarative LLM programs; optimizes prompts via examples and metrics |

### What to choose

For a .NET host: **Semantic Kernel + Kernel Memory + a vector DB + ScriptDom + Polly** covers ~90% of the heavy lifting. Add `Microsoft.Extensions.AI` if you may switch LLM providers later.

For a Python host: **LangGraph + LlamaIndex + Pydantic + a vector DB + sqlglot (for SQL parsing) + tenacity (retries)**.

The pattern is the same across stacks; only the package names change.

---

## 7. Question Taxonomy (10 Levels)

Build incrementally. Levels 1–4 cover roughly 70% of real analyst traffic.

### Level 1 — Simple Lookup
```
"How many customers do we have?"
"Show me 10 records from Orders."
"List all products in the Electronics category."
"What is the email of customer 4521?"
"Give me the last 20 tickets."
"How many orders today?"
```
Pattern: `SELECT ... FROM one_table WHERE simple_filter LIMIT n`

### Level 2 — Aggregation
```
"Total revenue this month."
"Count of tickets by status."
"Average order value last quarter."
"Sum of sales per region."
"How many active users yesterday?"
"Min/max price of products."
```
Pattern: `SELECT agg(col) FROM table GROUP BY dim`

### Level 3 — Filtering + Time Range
```
"Orders between Jan 1 and Mar 31."
"Customers who signed up in the last 7 days."
"Tickets opened this week but not closed."
"Products with stock < 10."
"Failed payments in the last hour."
"Top 5 most expensive orders today."
```
Pattern: date math + `WHERE` + `ORDER BY` + `LIMIT`

### Level 4 — Joins (2 tables)
```
"Show me customers with their last order."
"List tickets with the agent name."
"Products and their category names."
"Orders with customer email."
"Invoices with payment status."
```
Pattern: a single declared `JOIN` between two known entities.

### Level 5 — Multi-Join + Grouping
```
"Revenue per customer per month."
"Top 10 customers by total spend."
"Tickets per agent per priority."
"Average resolution time per category."
"Sales by product by region."
```
Pattern: 3+ tables, `GROUP BY` multiple dimensions, aggregations.

### Level 6 — Comparison / Trends
```
"Compare this month's revenue vs last month."
"Sales growth quarter over quarter."
"Tickets opened today vs the same day last week."
"Which products dropped in sales this month?"
"Year-over-year customer churn."
```
Pattern: window functions, `LAG`, CTEs, date bucketing.

### Level 7 — Top-N / Ranking
```
"Top 5 selling products per category."
"Best 3 agents by resolution time."
"Worst-performing regions last quarter."
"Most active customers this year."
"Slowest queries in the system."
```
Pattern: `ROW_NUMBER() OVER (PARTITION BY ...)` or `RANK()`.

### Level 8 — Cohort / Behavioral
```
"Customers who bought product A also bought what?"
"Retention rate of users who signed up in March."
"Funnel: visited → added to cart → purchased."
"Customers inactive for 90 days."
"First-time vs returning buyers this month."
```
Pattern: self-joins, multi-CTE, complex windowing.

### Level 9 — Schema / Metadata
```
"What tables do we have?"
"What columns are in the Orders table?"
"How are customers and orders related?"
"Where is the email stored?"
"What does 'status' mean in tickets?"
```
Pattern: query the introspected catalog / semantic layer — **no business data is touched**.

### Level 10 — Troubleshooting / Anomaly
```
"Why did revenue drop yesterday?"
"Are there duplicate customer records?"
"Find orders with missing payment."
"Tickets with no assigned agent."
"Customers with negative balance."
```
Pattern: `NULL` checks, duplicate detection, outlier scans, multi-step diagnostic queries.

### Out of Scope — Reject Politely

```
"Predict next month's revenue."        → ML model, not SQL
"Should we discontinue product X?"     → opinion, not data
"Email all customers about the sale."  → action, not query
"Delete old records."                  → write op; block at the connection level
"What's the CEO's salary?"             → permission/PII
```

### Roadmap guidance

- **MVP (week 1–4):** Levels 1–4. Cover the common case.
- **v1 (month 2–3):** Levels 5–7. Most analytics dashboards live here.
- **v2 (later):** Levels 8–10. Require a mature semantic layer and a strong eval harness.
- Track which level fails most in production — that is where you invest next.

---

## 8. Guardrails as Code

Guardrails belong in deterministic code, not in the prompt. Prompt-only safety fails under adversarial input.

### Database guardrails

- **Read-only role**: connect under a DB user that physically cannot write. Enforced at the DB, not the app.
- **Statement allowlist**: parse the compiled SQL; reject anything that is not a single `SELECT` (or an allowed set of read-only statements). Block `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `TRUNCATE`, `EXEC`, `xp_*`.
- **Schema allowlist**: reject queries that touch unlisted schemas/tables.
- **Tenant filter injection**: the compiler — not the LLM — adds `WHERE tenant_id = @currentTenantId` to every query. Verify with a post-compile check.
- **Row caps**: every query gets a hard `TOP N` / `LIMIT n` injected if absent.
- **Cost cap**: run `SET STATISTICS PROFILE` / `EXPLAIN`; reject queries above a cost threshold.
- **Timeout**: per-query and per-request timeouts at the connection level.

### LLM guardrails

- **System prompt isolation**: never concatenate user input directly into system instructions. Use a clearly delimited "user message" channel.
- **Output validation**: validate every LLM output against the JSON schema; reject and retry on parse failure.
- **Topic restriction**: an upstream classifier rejects off-topic, write-intent, or harmful prompts before they reach the planner.
- **Prompt-injection defense**: strip or escape suspicious instructions in retrieved context (e.g., "Ignore previous instructions…").
- **PII redaction**: post-process query results — mask SSNs, credit cards, tokens — before sending to the explainer LLM.

### Operational guardrails

- **Audit log**: every question, every compiled SQL/tool call, every result hash, every user — append-only, queryable.
- **Rate limits**: per user and per tenant.
- **Kill switch**: a global flag that disables LLM calls (for incident response, cost runaway, model outage).
- **Confidence floor**: if the planner's confidence is below a threshold, do not execute — ask the user a clarifying question instead.

---

## 9. Evaluation Harness

If you do not have an evaluation harness, you do not have a copilot — you have a demo.

### What to maintain

A **golden set** of 200–1000 entries:

```yaml
- id: q-0142
  question: "Top 5 customers by revenue this month"
  expected_intent: aggregate_topn
  expected_entities: [Customer, Order]
  expected_metric: revenue
  expected_sql: |
    SELECT TOP 5 c.Id, c.Name, SUM(o.Total) AS revenue
    FROM Customers c
    JOIN Orders o ON o.CustomerId = c.Id
    WHERE o.Status = 'paid'
      AND o.CreatedAt >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)
    GROUP BY c.Id, c.Name
    ORDER BY revenue DESC
  expected_result_shape: [{ Id: int, Name: string, revenue: decimal }]
```

Cover every taxonomy level. Include adversarial cases (write attempts, prompt injection, ambiguous wording, empty results, very large results).

### Metrics

- **Execution accuracy**: Did the SQL run successfully?
- **Result-set match**: Did the result equal the expected result (exact, or set-equal up to ordering)?
- **Exact-SQL match**: Did the generated SQL match a canonical form? (Loose metric — many SQLs are correct.)
- **Plan-level match**: Did the JSON spec match? (More robust than SQL match.)
- **Latency p50 / p95**.
- **Cost per question** (tokens, USD).
- **Refusal accuracy**: Did out-of-scope questions get refused?

### When to run

- On every prompt change.
- On every model change.
- On every schema change.
- Nightly in CI against a representative dataset.
- Before every production deploy — gated.

### The discipline

A change that improves one metric but regresses another by more than X% is a regression — even if the headline number went up. Track all metrics together.

---

## 10. Caching Strategy

Caching reduces latency, cost, and variance. Three layers, in order of value:

### Layer 1 — Semantic cache (question → spec/SQL)

- Embed the user's question.
- Look up nearest neighbor in a small vector store of *previously answered* questions.
- If similarity > threshold (e.g., 0.92): return the cached spec/SQL directly; skip the planner.
- Hit rate in mature copilots: 30–60%.

### Layer 2 — Result cache (SQL → result)

- Hash the compiled SQL + parameter set.
- Cache the result with a TTL (seconds for "today's data", minutes for slower-moving facts, hours for static dimensions).
- Invalidate on known data-change events when possible.

### Layer 3 — Schema retrieval cache

- Per-session: the retrieved schema slice for a conversation rarely changes turn-to-turn.
- Cache it for the session lifetime.

### Don't cache

- Anything containing PII without a clear retention policy.
- Personalized results when the user identity is part of the filter (or include user id in the cache key).

---

## 11. Multi-Agent Decomposition

Single-agent copilots solve the 80% case. The remaining 20% — comparison-of-comparisons, cross-domain answers, multi-step diagnostics — benefits from decomposition.

### The pattern

```
Router  →  Decomposer  →  N Specialist Agents (in parallel)  →  Synthesizer
```

- **Router**: classifies the question domain (data lookup vs. metadata vs. troubleshooting vs. cross-domain).
- **Decomposer**: breaks compound questions into independent sub-questions ("Q1 vs Q2 by region for top 5 products" → 2 sub-queries + a join step).
- **Specialists**: each is itself a copilot pipeline, scoped to one domain (DB-A, DB-B, docs, tickets).
- **Synthesizer**: stitches sub-results into a single coherent answer.

### When to escalate to multi-agent

- Single-agent accuracy on Levels 6–8 plateaus despite better prompts and more few-shot examples.
- Questions span multiple data sources you cannot reasonably join in one query.
- Latency is not your tightest constraint.

### Cautions

- Each extra agent multiplies latency, cost, and failure surface.
- Decomposers hallucinate sub-questions. Constrain decomposer output with a JSON schema, just like the planner.
- Synthesizers can drift from the underlying data. Always carry the source data into the synthesis prompt — never let the synthesizer invent numbers.

---

## 12. Configuration Per Domain (the reuse story)

Build one engine. Configure it per domain.

### Engine (write once)

- Orchestrator (LLM calls, function calling, retries)
- Vector store + RAG pipeline
- SQL parser / API-call validator
- Guardrail framework
- Cache layer
- Eval runner
- Telemetry

### Per-domain config (small, declarative, often auto-generated)

```yaml
domain: support_db
connection:
  provider: sqlserver
  conn_string_secret: AISupport-DB-ReadOnly
  dialect: tsql

dialect_quirks:
  date_today: "CAST(GETDATE() AS date)"
  top_n_syntax: "TOP {n}"
  identifier_quote: "[]"

semantic_layer:
  path: ./semantic/support_db.yaml

few_shot_examples:
  path: ./examples/support_db.jsonl
  retrieve_top_k: 5

permissions:
  allowed_schemas: [dbo, support]
  blocked_columns: [Customers.Ssn, Users.PasswordHash]
  tenant_filter_column: tenant_id

guardrails:
  read_only: true
  max_rows: 1000
  query_timeout_seconds: 30
  daily_cost_cap_usd: 50

cache:
  semantic_threshold: 0.92
  result_ttl_seconds: 60
```

### Onboarding a new domain

1. Run introspection → produces a baseline schema snapshot.
2. Auto-generate a draft semantic layer (LLM-assisted at setup time only).
3. Human reviews entities, metrics, synonyms. (1–2 hours for small domains.)
4. Seed few-shot examples — start with 10 hand-written, grow with real traffic.
5. Run the eval harness against this domain.
6. Ship.

No new code. New config.

---

## 13. Anti-Patterns

What experts avoid:

- **Letting the LLM emit raw SQL** with no compiler in between. Almost every production failure traces to this.
- **Stuffing the entire schema into the prompt.** Use retrieval. Schemas grow; context windows are a budget, not a place to dump.
- **Skipping the semantic layer** ("we'll add it later"). You won't. The copilot will be permanently mediocre instead.
- **No eval harness.** Every prompt tweak becomes a vibe check. You will regress silently.
- **Trusting the LLM's self-reported confidence.** It is poorly calibrated. Use external signals (validator pass rate, result-row count, retry count).
- **Mixing read and write operations in one agent.** Separate agents, separate credentials, separate audit trails. Read agents never get write capability — even "just for this one feature."
- **Hardcoding tables and columns.** Introspect. Or you will rewrite the engine for every new tenant.
- **Building Levels 8–10 before Levels 1–4 work.** You will impress nobody with a half-broken cohort analysis when "list 10 customers" is flaky.
- **Treating prompt engineering as a substitute for architecture.** No prompt rescues a system without retrieval, a semantic layer, validation, and eval.
- **Embedding too coarsely.** One vector per entire table loses column-level signal. One vector per (table, column-group) is usually right.
- **Ignoring dialect.** A T-SQL planner asked to talk to Postgres will produce confidently wrong SQL. Make dialect explicit in the system prompt.
- **One giant prompt.** Break orchestration into small, named, individually-tested LLM calls. Each becomes debuggable and replaceable.
- **No kill switch.** When (not if) the model misbehaves or costs spike, you need a single flag to disable LLM calls.

---

## 14. Decision Checklist Before You Start

Answer these before writing code. The answers determine the architecture.

- **Backends**: SQL? API? Documents? Multiple? Which engine(s)?
- **Read or read+write**: Read-only is a different (and much safer) product than read+write. Do not blur them.
- **Tenancy**: Single-tenant or multi-tenant? If multi, where is the tenant boundary enforced?
- **Dialects**: T-SQL, PostgreSQL, MySQL, BigQuery, Snowflake? If more than one, semantic layer and dialect rendering must be separable.
- **Eval set**: Who writes it? Who maintains it? When does it run? What is the gating threshold?
- **Semantic layer ownership**: Engineering? Analytics? A central data team? Whoever owns it must commit to maintaining it as the schema evolves.
- **Cost ceiling**: USD per question (p50/p95). Token ceilings per call. Daily/monthly caps per tenant.
- **Latency SLA**: p50 and p95 in milliseconds. Drives caching and decomposition decisions.
- **PII surface**: Which columns/tables contain PII? Redaction strategy? Logging policy?
- **Failure modes**: What happens on LLM outage? Vector-DB outage? DB outage? Each needs a documented response.
- **Audit requirements**: SOC 2? HIPAA? GDPR? Drives logging retention, encryption, and access policy.
- **Model strategy**: Single provider or multi-provider? On-prem or hosted? Drives the abstraction layer choice.
- **UI surface**: Chat? Embedded in an analytics tool? Slack? Each surface has different latency and formatting expectations.
- **Feedback loop**: How does a user mark an answer wrong? Where do corrections go? How do they become few-shot examples?

If you cannot answer most of these, do that work first. Building first and answering later is how copilot projects miss their goals.

---

## 15. Glossary

- **RAG** — Retrieval-Augmented Generation. Inject relevant retrieved context into the prompt at query time. The dominant pattern for grounding LLMs in private data.
- **Embedding** — A numeric vector representing the semantic content of text. Similar texts → similar vectors.
- **Vector DB** — A database optimized for nearest-neighbor search over embeddings. Examples: Qdrant, Milvus, Weaviate, pgvector, Azure AI Search.
- **Semantic layer** — A declarative mapping from user vocabulary to canonical entities, metrics, dimensions, and joins. The single biggest accuracy multiplier in a copilot.
- **Few-shot** — Including a handful of example (input, output) pairs in the prompt to steer the model. Retrieved dynamically based on similarity to the current question.
- **Planner** — The LLM call that turns a user question into a structured spec (JSON), not raw output.
- **Compiler** — Deterministic code that turns the spec into the target language (SQL, GraphQL, REST, tool call).
- **Agent loop** — A control loop in which the LLM proposes an action, executes a tool, observes the result, and decides the next action. Bounded by step count and cost.
- **ReAct** — A specific agent-loop pattern: interleave Reasoning and Acting steps. The LLM emits a thought, then a tool call, then observes, then iterates.
- **Function calling / Tool calling** — The provider-supported mechanism for the LLM to emit a structured tool invocation rather than free-form text. Mandatory for safe orchestration.
- **Structured output / JSON mode** — Constrained generation that guarantees the LLM's output parses as a given JSON schema.
- **Guardrails** — Programmatic checks (input filtering, output validation, allowlists, redaction) that enforce safety and correctness independent of the LLM.
- **Golden set** — A curated, hand-verified set of (question, expected answer) pairs used as the eval baseline.
- **Self-correction** — Feeding an execution error back to the LLM so it can revise the spec or the SQL. Capped retries.
- **Semantic cache** — A cache keyed on question embeddings (rather than exact strings) so paraphrases hit the same entry.
- **FK graph** — An in-memory representation of table relationships used to auto-derive joins.
- **Junction table** — A table that exists only to connect two other tables in a many-to-many relationship.
- **Soft delete** — A row-deletion convention that sets a flag (`IsDeleted`, `DeletedAt`) instead of removing the row. Must be respected by every generated query.
- **Multi-tenant** — A single deployment serving multiple isolated customer datasets. Tenant filtering is non-negotiable and belongs in the compiler, not the prompt.

---

## Closing Note

A copilot that ships is built on this discipline:

> **Retrieve only what's relevant. Plan in JSON, not SQL. Compile deterministically. Validate before executing. Execute under least privilege. Self-correct once or twice. Explain with citations. Measure everything. Configure per domain. Add agents only when single-agent plateaus.**

Everything else is decoration.
