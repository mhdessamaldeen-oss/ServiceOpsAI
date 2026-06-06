# ServiceOps AI — Utility Operations Platform with a Natural-Language Copilot

An ASP.NET (.NET 10) operations platform for a multi-service utility provider (electricity, water,
internet, gas, government services). Staff manage **customers, bills, outages, regions, and support
tickets** — where a ticket is a real complaint about a specific service, bill, or outage.

Its centerpiece is **AnalystAgent**: a deterministic, configuration-driven
**natural-language → SQL** data-analyst copilot (Arabic **and** English) that lets an administrator ask
questions of the live operational database in plain language and get verified, source-grounded answers.

![AnalystAgent — ask in English or Arabic and get validated, read-only SQL with a live, stage-by-stage pipeline timeline](docs/screenshots/02-copilot.png)

> Status: active engineering project. The copilot pipeline is the focus of ongoing work; see
> `docs/architecture/` for the design records.

**📸 [See SHOWCASE.md](SHOWCASE.md) for the full screenshot tour** (17 views of the copilot and platform).

| Dashboard | Assessment lab | Ticket AI investigation |
|:---:|:---:|:---:|
| [![Dashboard](docs/screenshots/01-dashboard.png)](docs/screenshots/01-dashboard.png) | [![Assessment lab](docs/screenshots/05-copilot-assessment.png)](docs/screenshots/05-copilot-assessment.png) | [![Ticket AI investigation](docs/screenshots/08-ticket-details.png)](docs/screenshots/08-ticket-details.png) |

---

## What the copilot does

Ask, in English or Arabic:

- "how many tickets are open?" / "كم عدد التذاكر المفتوحة؟"
- "top 5 customers by total billed amount"
- "which regions have more tickets than the average region?"
- "outages by severity this month"

…and it plans, compiles, validates, executes (read-only), and explains a SQL query over the
database — refusing safely when a question is out of scope or unsafe.

### How it's built (high level)

```
question (EN/AR)
  → router (deterministic cascade): chat · knowledge · semantic-search · external-tool · data-query
  → data query: scope gate (refuse safely if out of scope) → optional decompose of compound asks
  → DIRECT path (primary): schema-link → ground real DB values → emit SQL (LLM)
       → deterministic self-validating repairs → read-only AST validate → execute → explain (EN/AR)
  → falls through to a heavier spec-compile pipeline only for shapes the direct path can't express
```

Design principles that make it robust and portable:

- **Configuration-driven, not hardcoded.** The schema is learned by **live introspection**
  (`INFORMATION_SCHEMA`/`sys.*`), and domain knowledge (entities, synonyms, metrics, natural-key
  formats, sensitive columns, few-shot/worked SQL examples, shape keywords) lives in JSON under
  `Areas/AnalystAgent/Configuration/`. Targeting a different database is largely a config
  exercise, not a recompile.
- **Model-agnostic with capability tiers.** A `PlannerTier` (Weak / Medium / Strong) is
  **auto-derived from the active model**; weak-model "crutch" repair rules switch off automatically
  on stronger models, while SQL-correctness rules always run.
- **Multilingual by design.** Gates and classifiers are config/LLM-driven rather than English
  keyword lists.
- **Read-only and guarded.** Generated SQL passes an AST allowlist (no DML/DDL, no `EXEC`,
  no `OPENROWSET`/`OPENQUERY`, no multi-statement, no `sys.*`) and a PII-redaction layer before and
  after execution.
- **Self-correcting by construction.** *Grounding* — binding question terms to real values in the
  database — is the single source of truth on what to filter. A deterministic repair layer then fixes
  the model's recurring SQL mistakes (unrequested filters, wrong aggregation grain) using only schema +
  grounding facts, never hardcoded vocabulary; and every rewrite is re-parsed before it is accepted, so
  a bad fix degrades to a no-op instead of corrupting the query.

---

## Tech stack

- **.NET 10**, ASP.NET Core MVC
- **Entity Framework Core** over **SQL Server**
- **AI providers (pluggable):** [Ollama](https://ollama.com) local (e.g. `qwen2.5-coder:7b`) and a
  cloud LLM — **Groq**, **Gemini**, or **OpenAI**; embeddings via local `bge-m3`
- **xUnit** test suite (`Tests/AnalystAgent.Tests`) — 453 tests (unit + architecture guards)

---

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **SQL Server** (Express is fine) reachable from the app
- For the copilot:
  - **Ollama** running locally with the models you want (`ollama pull qwen2.5-coder:7b`,
    `ollama pull bge-m3`), and/or
  - a **cloud LLM** API key — Groq, Gemini, or OpenAI — added via the in-app Settings → AI engines page

### Configure the database connection

The default connection string lives in `appsettings.json`. **Override it without editing the file**
via the standard ASP.NET environment variable (recommended for any non-dev machine):

```bash
# PowerShell
$env:ConnectionStrings__DefaultConnection = "Server=YOUR_SERVER;Database=AISupportAnalysisDB;Trusted_Connection=True;TrustServerCertificate=True"

# bash
export ConnectionStrings__DefaultConnection="Server=YOUR_SERVER;Database=AISupportAnalysisDB;Trusted_Connection=True;TrustServerCertificate=True"
```

### Run

```bash
dotnet restore
dotnet ef database update     # apply EF Core migrations (seed data included)
dotnet run --project ServiceOpsAI.csproj
```

Then open the app, go to **Settings → AI engines** to pick the Copilot provider/model (Ollama or a
cloud LLM) and, for a cloud provider, add one or more API keys to the key pool.

### Test

```bash
dotnet test Tests/AnalystAgent.Tests/AnalystAgent.Tests.csproj
```

---

## Project layout

```
Areas/AnalystAgent/        the NL→SQL copilot
  Pipeline/                     router orchestration, stages, the direct analyst path + repair layer
  Grounding/                    bind question terms to real DB values (the source-of-truth moat)
  Sql/Dialects/                 SQL compiler + dialect abstraction (SqlServer / Postgres)
  Retrieval/ Semantic/          semantic / keyword / verified-query retrieval + the semantic layer
  Schema/                       live schema introspection, inference + drift detection
  Validation/                   read-only SQL AST validator
  Tools/                        external-tool registry + selection
  Configuration/                ALL the JSON knobs (copilot-options, semantic-layer, schema-overrides,
                                linguistic-cues, shape-classifier, verified-queries…)
Services/ Controllers/ Views/   the surrounding utility-ops web application
Tests/AnalystAgent.Tests/  xUnit tests (unit + architecture guards)
docs/architecture/              ADRs and design notes
```

---

## Switching / tuning the copilot without code

- **Pick the model:** Settings → AI engines (Ollama or Gemini). The capability tier auto-derives
  from the model name.
- **Retarget a new database:** point the connection string at it and run schema inference — the engine
  introspects tables, columns, keys and relations automatically into `schema-inferred.json`. Then add any
  human corrections (domain synonyms, descriptions, sensitive columns, natural keys) in
  `schema-overrides.json`, merged additively on top. No recompile.
- **Tune linguistics:** `linguistic-cues.json`, `shape-classifier.json` (per-locale, no recompile).
- **Switch SQL engine:** the compiler targets an `ISqlDialect`; set `"Database": "SqlServer"` or
  `"Postgres"` in `copilot-options.json` (SQL Server is the default; the PostgreSQL dialect is
  implemented and the compiler emits valid PostgreSQL — full Postgres execution wiring is in progress).

---

## License

License to be decided — see `LICENSE`. Until a license is added, all rights are reserved by the
author.
