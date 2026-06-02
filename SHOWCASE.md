# SupportFlow AI — Showcase

A unified **utility-operations platform** (support tickets, billing, field operations, outages) with a
**natural-language → SQL AI copilot** at its center: it turns plain-English *and Arabic* questions into
**validated, read-only SQL**, runs them against the live database, and explains the result — with a
live, stage-by-stage pipeline timeline.

Built on **ASP.NET Core (.NET 10)** + EF Core + SQL Server, with a local-or-cloud LLM provider
(Ollama / Gemini / Claude / GPT). **322 passing unit tests.** The NL→SQL engine is **config-driven
(no hardcoded vocabulary)** and **database-engine-portable** (SQL Server today, PostgreSQL-ready via a
one-line config switch).

---

## ⭐ The AI Copilot — natural language to SQL

### Copilot workspace
![Copilot](docs/screenshots/02-copilot.png)

Ask in plain English or Arabic — *"how many tickets by priority"*, *"top 5 customers by total billed
amount"*, *"outages this month vs last month"*, *"كم عدد التذاكر المفتوحة"*. The pipeline classifies the
question's **shape**, extracts a structured query **spec**, **repairs** it (gated to the model's
strength), **compiles** to SQL, **validates** it's read-only, **runs** it, and **explains** the answer —
streaming each stage to a Claude-style timeline. The right panel exposes configured **external tools**
(FX rates, public holidays, geo lookups).

**Strength:** zero phrase-matching — every cue, shape, and example lives in JSON config, so the *same
engine* retargets to a different schema or language by editing files, not code.

### Assessment lab
![Assessment lab](docs/screenshots/05-copilot-assessment.png)

Run a suite of **DB-verified** questions against any configured model and **self-grade execution
accuracy per question-shape**. **Strength:** the copilot's quality is *measured and regression-tested*,
not asserted — every refactor is checked against a locked baseline.

### AI Analysis Hub & Retrieval Benchmark
![AI Analysis Hub](docs/screenshots/03-ai-hub.png)
![Retrieval benchmark](docs/screenshots/04-retrieval-benchmark.png)

The hub is the entry point to the AI features; the benchmark measures **semantic-retrieval quality**
(how well embeddings route a paraphrased question to the right tables/tickets). **Strength:** retrieval
is treated as a tunable, measurable component — not a black box.

### AI Insights
![AI Insights](docs/screenshots/06-ai-insights.png)

Aggregate AI analysis across the ticket corpus — themes, root causes, and recommended actions.

---

## The platform

### Dashboard
![Dashboard](docs/screenshots/01-dashboard.png)

KPI cards (total / open / closed / SLA-breached), intake trends, and operational balance — plus an
inline **copilot quick-ask** right on the landing page.

### Tickets & AI investigation
![Tickets](docs/screenshots/07-tickets.png)
![Ticket investigation](docs/screenshots/08-ticket-details.png)

The full ticket lifecycle, and a per-ticket **AI investigation** view (root cause, similar historical
tickets, recommended next action) grounded strictly in the available evidence.

### Billing — Bills & Payments
![Bills](docs/screenshots/09-bills.png)
![Payments](docs/screenshots/10-payments.png)

### Customers
![Customers](docs/screenshots/11-customers.png)

### Field operations — Outages, Work Orders, Meter Readings
![Outages](docs/screenshots/12-outages.png)
![Work orders](docs/screenshots/13-work-orders.png)
![Meter readings](docs/screenshots/14-meter-readings.png)

### Reports
![Reports](docs/screenshots/15-reports.png)

### Admin — Global Configuration
![Global configuration](docs/screenshots/16-settings.png)

Every copilot knob — model tiers, similarity thresholds, the **database-engine switch** — is
configuration. Tune behavior without recompiling.

### Users & access
![Users](docs/screenshots/17-users.png)

Role-based access (Super Admin / Support Agent / End User) over ASP.NET Identity.

---

## Engineering highlights

- **NL→SQL pipeline** modeled on DIN-SQL / CHESS / E-SQL research: 8-shape classifier → spec
  extraction → 32 **tier-gated** repair rules → dialect compile → **read-only AST validation** →
  execute → explain → coverage check → raw-SQL escape valve.
- **Config-driven, zero phrase-matching:** linguistic cues, question shapes, refusal text, worked
  examples — all JSON, hot-reloadable, **EN + AR**. Retarget to a new domain by editing config.
- **Database-portable:** the compiler talks to an `ISqlDialect` (SQL Server **and** PostgreSQL
  implemented); the engine is a one-line config switch (`"Database": "SqlServer" | "Postgres"`).
- **Tier-aware:** weak local models (Ollama `qwen2.5-coder`) get crutch repair rules; strong cloud
  models shed them — auto-derived from the model name.
- **Tested:** **322** unit tests; golden **byte-identity** tests pin behavior through every refactor.
- **Safe by construction:** read-only DB access, PII columns blocked, prompt-injection guard, and the
  escape valve is AST-validated before execution.

## Run it

```bash
# 1. Set the connection string (env var overrides appsettings.json)
setx ConnectionStrings__DefaultConnection "Server=YOUR_SERVER;Database=SupportFlowAI;Trusted_Connection=True;TrustServerCertificate=true"

# 2. Apply migrations + seed (first run seeds demo data + an admin)
dotnet ef database update

# 3. Run
dotnet run

# 4. Sign in at https://localhost:8899  →  admin@tech.local / Admin@123
```

For a local LLM, install [Ollama](https://ollama.com) and pull `qwen2.5-coder:7b` + `bge-m3`; or point
the provider at Gemini / Claude / GPT in **Global Configuration**.

## Tests

```bash
dotnet test Tests/SuperAdminCopilot.Tests
```
