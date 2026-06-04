# ServiceOps AI — Utility Operations Platform with a Natural-Language Copilot

An ASP.NET (.NET 10) operations platform for a multi-service utility provider (electricity, water,
internet, gas, government services). Staff manage **customers, bills, outages, regions, and support
tickets** — where a ticket is a real complaint about a specific service, bill, or outage.

Its centerpiece is the **SuperAdmin Copilot**: a deterministic, configuration-driven
**natural-language → SQL** engine (Arabic **and** English) that lets an administrator ask questions
of the live operational database in plain language and get verified, source-grounded answers.

> Status: active engineering project. The copilot pipeline is the focus of ongoing work; see
> `docs/architecture/` for the design records.

**📸 [See SHOWCASE.md](SHOWCASE.md) for a screenshot tour** of the copilot and the platform.

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
  → preflight gates (write-intent / scope / safety, deterministic)
  → shape classification (8 shapes, config-driven)
  → spec extraction (LLM → a canonical QuerySpec)
  → SpecRepair (typed repair rules, tier-gated)
  → SQL compile → AST validate (read-only allowlist) → execute
  → explain (EN/AR)
  → coverage check → escape valve (raw-SQL fallback for shapes the form-filler can't express)
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

---

## Tech stack

- **.NET 10**, ASP.NET Core MVC
- **Entity Framework Core** over **SQL Server**
- **AI providers:** [Ollama](https://ollama.com) (local, e.g. `qwen2.5-coder:7b`) and Google
  **Gemini** (`gemini-2.5-flash-lite`); embeddings via local `bge-m3`
- **xUnit** test suite (`Tests/AnalystAgent.Tests`)

---

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **SQL Server** (Express is fine) reachable from the app
- For the copilot:
  - **Ollama** running locally with the models you want (`ollama pull qwen2.5-coder:7b`,
    `ollama pull bge-m3`), and/or
  - a **Google Gemini** API key (added via the in-app Settings → AI engines page)

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

Then open the app, go to **Settings → AI engines** to pick the Copilot provider/model (Ollama or
Gemini) and, for Gemini, add one or more API keys to the key pool.

### Test

```bash
dotnet test Tests/AnalystAgent.Tests/AnalystAgent.Tests.csproj
```

---

## Project layout

```
Areas/AnalystAgent/        the NL→SQL copilot
  Pipeline/                     orchestration, stages, single-question executor, escape valve
  Application/Repair/           typed SpecRepair rules + the repair bus
  Compilation/                  QuerySpec → SQL compiler (+ dialect abstraction)
  Retrieval/                    semantic / keyword / verified-query retrieval
  Schema/ Semantic/             live schema introspection + the semantic layer
  Validation/                   read-only SQL AST validator
  Configuration/                ALL the JSON knobs (semantic-layer, shape-examples,
                                advanced-shape-keywords, few-shot, linguistic-cues, question suites…)
Services/ Controllers/ Views/   the surrounding utility-ops web application
Tests/AnalystAgent.Tests/  xUnit tests (unit + architecture guards)
docs/architecture/              ADRs and design notes
```

---

## Switching / tuning the copilot without code

- **Pick the model:** Settings → AI engines (Ollama or Gemini). The capability tier auto-derives
  from the model name.
- **Retarget a new database:** point the connection string at it, then edit the JSON under
  `Areas/AnalystAgent/Configuration/` — chiefly `semantic-layer.json` (entities, synonyms,
  display/sensitive columns), and the worked-SQL example banks (`shape-examples.json`,
  `advanced-shape-keywords.json`). The engine introspects tables/columns automatically.
- **Tune linguistics:** `linguistic-cues.json`, `shape-classifier.json` (per-locale, no recompile).
- **Switch SQL engine:** the compiler targets an `ISqlDialect`; set `"Database": "SqlServer"` or
  `"Postgres"` in `copilot-options.json` (SQL Server is the default; the PostgreSQL dialect is
  implemented and the compiler emits valid PostgreSQL — full Postgres execution wiring is in progress).

---

## License

License to be decided — see `LICENSE`. Until a license is added, all rights are reserved by the
author.
