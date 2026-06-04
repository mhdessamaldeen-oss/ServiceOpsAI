# Areas/AnalystAgent

In-host AnalystAgent implementation. Lives inside `ServiceOpsAI.csproj` (no separate assembly) but is structured so it can be extracted to a standalone DLL later with minimal effort. Built strictly to [docs/abstraction-build-copilot.md](../../docs/abstraction-build-copilot.md).

## Public surface

- `IAnalystAgent` — single orchestrator, one `AskAsync` method.
- `POST /api/analyst-agent/ask` — body `{ "question": "..." }` → `{ reply, sql, rowCount, rows, trace, traceId, error }`.
- `GET  /api/analyst-agent/eval/suites` — list available question suites.
- `POST /api/analyst-agent/eval/run` — run all suites; returns the assessment report.
- `POST /api/analyst-agent/eval/run/{suiteName}` — run one suite by name.

## Host integration points

The new copilot reuses host infrastructure for three things — and only three. Every host dependency is funneled through the `HostBridge/` folder. The rest of the code only sees internal abstractions (`ILlmClient`, `ITraceSink`, `IConnectionStringProvider`).

| Bridge file | Wraps host type | Purpose |
|---|---|---|
| `HostBridge/HostAiProviderLlmClient.cs` | `IAiProviderFactory` + `WorkloadAwareProvider` | Planner LLM call uses whatever model is configured for `AiWorkloadType.Copilot` in the host's settings UI. Same workload as the legacy copilot — one knob controls both. |
| `HostBridge/HostTraceSink.cs` | `CopilotTraceHistoryStore.SaveAsync` | Every question + SQL + result is persisted to the existing `CopilotTraceHistories` table so it shows up in the existing investigation tree, tagged with `SourceSuite="analyst-agent"`. |
| `HostBridge/HostConnectionStringProvider.cs` | `IConfiguration.GetConnectionString("DefaultConnection")` | Reuses the host's primary DB connection. No separate connection string. |

## Phase status

- [x] Phase 0 — skeleton + stub (now superseded by full pipeline).
- [x] Phase 1 — schema introspection (`INFORMATION_SCHEMA` + `sys.foreign_keys`) + FK graph (`QuikGraph`) + `EntityCatalog`.
- [x] Phase 2 *(lite)* — keyword retriever; vector DB / Qdrant deferred.
- [ ] Phase 3 — semantic layer (named metrics / dimensions / synonyms).
- [x] Phase 4 — JSON-constrained `LlmPlanner`.
- [x] Phase 5 — deterministic `SqlCompiler` + `JoinResolver`. Calculated-column expression grammar deferred.
- [x] Phase 6 — `SqlAstValidator` (ScriptDom AST + statement allowlist) + `ReadOnlyExecutor`.
- [x] Phase 7 *(lite)* — `TemplatedExplainer`. LLM explainer deferred.
- [ ] Phase 8 — Decomposer + Synthesizer for compound questions.
- [x] Phase 9 — `GoldenSetRunner` + four YAML question suites + eval API.
- [ ] Phase 10–12 — cutover prep, ramp, cleanup.

## How to extract this folder back to a standalone DLL later

The whole point of the `HostBridge/` boundary is that future-you can reverse this decision in an afternoon:

1. Create a new `AnalystAgent/` class library project.
2. **Move every folder except `HostBridge/`** into the new project.
3. Add NuGet refs: `Microsoft.SqlServer.TransactSql.ScriptDom`, `Microsoft.Data.SqlClient`, `QuikGraph`, `YamlDotNet`, `NJsonSchema`, plus the `Microsoft.AspNetCore.App` framework reference.
4. Keep `HostBridge/` in the host. Its three classes implement the new DLL's `ILlmClient` / `ITraceSink` / `IConnectionStringProvider`. The host references the DLL.
5. The host's `Program.cs` line `builder.Services.AddAnalystAgent(builder.Configuration)` is unchanged — same namespace, same call. The bridges are still registered alongside.

Namespaces stay `AnalystAgent.*` exactly so this works without touching any using directive.

## Configuration

Optional `appsettings.json` block (all keys optional — defaults apply):

```json
"AnalystAgent": {
  "RetrieverTopK": 5,
  "MaxRows": 1000,
  "CommandTimeoutSeconds": 30,
  "QuestionSuitesPath": "Areas/AnalystAgent/Configuration/QuestionSuites"
}
```

No `ConnectionString`, no `Llm` block — those come from the host (`ConnectionStrings:DefaultConnection` and the `AiWorkloadType.Copilot` model selected in the host's settings UI, respectively).

## How to run the assessment

1. Make sure the host's settings UI has a model selected for the **Copilot** workload (Ollama, OpenAI, etc.).
2. Run the host app.
3. `GET /api/analyst-agent/eval/suites` — confirm the suite JSONs were copied to `bin\Areas\AnalystAgent\Configuration\QuestionSuites\`.
4. `POST /api/analyst-agent/eval/run` (empty body) — returns a JSON `EvalReport`.
5. `POST /api/analyst-agent/eval/run/{suiteName}` — run a single suite by name.
6. Open the existing investigation tree UI — every question above appears as a new trace row tagged `analyst-agent`.

## Evaluation modes

### Execution Accuracy (EX) — industry standard (recommended)

When a test case has a `GoldSql` field, the runner uses **Execution Accuracy**: it executes both the gold SQL and the copilot's SQL against the live database, then compares the result sets (ignoring column order and row order). The copilot **passes if and only if it produces the same rows**, regardless of SQL syntax differences.

This is the same methodology used by Spider, BIRD, and production copilot systems (Microsoft Copilot, Vanna AI). It eliminates false failures from valid SQL alternatives (e.g., `LEFT JOIN` vs `NOT EXISTS`, different column aliases, different aggregation syntax).

```json
{
  "Code": "EX-001",
  "Question": "How many open tickets?",
  "GoldSql": "SELECT COUNT(*) AS Count FROM Tickets t JOIN TicketStatuses ts ON ts.Id = t.StatusId WHERE ts.Name = 'Open' AND t.IsDeleted = 0",
  "ExpectedMaxRows": 1
}
```

The API response includes EX stats: `ExTotal`, `ExPassed` at both suite and report level.

### SQL Token Matching — legacy fallback

When `GoldSql` is not provided, the runner falls back to `ExpectedSqlContains` / `ExpectedSqlNotContains` / `ExpectedSqlAnyOf` token checks. These check for substring presence in the generated SQL. Use this for:
- Security tests (ensure `DROP TABLE` is NOT in the SQL)
- Shape guards (ensure `COUNT` appears for count questions)
- Negative tests (`ExpectedInvalid` + `ExpectedFailedStage`)

### Migration path

Gradually add `GoldSql` to existing suite files. Both modes can coexist — when `GoldSql` is present it takes priority; `ExpectedSqlNotContains` (banned tokens) still runs for security.

## What's untouched

- Shared host services under `Services/AI/Copilot/` — assessment catalogs, tool registry, trace storage, and trace analysis.
- Legacy `Services/AI/Investigation/` — investigation flow unchanged.
- Sibling `AnalystAgent.csproj` (the original standalone project) — deleted as of 2026-05-11. The in-host area is now the only AnalystAgent implementation.
