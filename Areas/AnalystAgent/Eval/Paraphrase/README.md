# Paraphrase Robustness Evaluation

Dr.Spider-style measurement infrastructure for the AnalystAgent. **Reuses the
existing suite shape and folder** so the live `CopilotAssessmentHandler` reads paraphrase
suites natively — no forked loader, no separate location.

## Folder & file convention

```
Areas/AnalystAgent/
├── Configuration/QuestionSuites/
│   └── suite-paraphrase-robustness-2026-05-24.json   ← lives here, alongside every other suite
└── Eval/
    ├── ExecutionAccuracyChecker.cs                   (existing — multiset row comparator)
    ├── Reports/                                       (auto-created — runner persists outputs here)
    └── Paraphrase/
        ├── ParaphraseSuite.cs                        (record types)
        ├── ParaphraseRobustnessReport.cs             (report record types)
        ├── IParaphraseRobustnessRunner.cs            (runner)
        ├── OfflineParaphraseGenerator.cs             (offline batch paraphraser)
        ├── VerifiedQuerySeedLoader.cs                (loads verified-queries.json → seeds)
        ├── paraphrase-expansion-prompts.json         (offline-gen prompts, hot-loaded)
        └── README.md                                 (this file)
```

## Suite shape — same as every other suite

Each suite file is the standard `{ name, version, description, Scenarios: [...] }` shape.
Each scenario is a `CopilotAssessmentCase`-compatible record with the standard fields plus
three extras the runner uses (and the standard handler ignores):

```jsonc
{
  "Code": "PR-001-bracket-sq",
  "Question": "show me users [Id, Name] and their role",
  "Category": "DisplayColumns",
  "Difficulty": "Medium",
  "ExpectedIntent": "DataQuery",
  "EntityFocus": "AspNetUsers",
  "ExpectedSql": "SELECT u.Id, u.UserName, r.Name AS RoleName FROM ...",
  "MaxLatencyMs": 180000,

  // Paraphrase-runner extras — invisible to CopilotAssessmentHandler
  "ClusterId": "PR-CLU-001",        // groups intent-equivalent scenarios
  "Perturbation": "bracket-square", // dimension this scenario tests
  "Language": "en"
}
```

Scenarios sharing a `ClusterId` express the SAME intent in different phrasings — by
construction they SHOULD have the same `ExpectedSql` (duplicated per scenario for
self-containment, so any single scenario is runnable in isolation by the standard handler).

## What the runner measures

For each scenario, the runner sends the question through the live copilot, executes the
`ExpectedSql` against the read-only DB, and compares row sets via the existing
`IExecutionAccuracyChecker`. The report aggregates two ways:

- **By perturbation** — pass rate per `Perturbation` label. The Dr.Spider headline metric
  (`WorstPerturbationDropPct`) is the largest gap between the `base` perturbation's pass
  rate and any other perturbation's pass rate. Tells you exactly which natural-language
  phenomenon the pipeline can't handle.
- **By cluster** — pass rate per `ClusterId`. Tells you which intents broke.

Reports are persisted under `Eval/Reports/{suite-stem}-{utc-timestamp}.json` for offline
comparison.

## Principle — what the eval is NOT

The suite is a **sample** from the distribution of how users phrase questions. It is
not "the things the system should handle" and the per-perturbation labels are not a
closed taxonomy. If we ever copy these scenarios into the parser's prompt, we leak the
test set and the numbers stop meaning anything.

The way to improve pass rate is **model strength** (Phase 7 frontier escalation), **broader
training data** (eventual fine-tuning), or **self-consistency** (Phase 4 sampling vote) —
NOT enumerating more example phrasings in any prompt. See
`Pipeline/Stages/Decomposed/README.md` for the prompt-design principle.

## How to run

The `ParaphraseEvalController` exposes:

```
GET  /ParaphraseEval/Suites           → list paraphrase suites (filters by ClusterId marker)
POST /ParaphraseEval/Run?suiteFile=…  → run a suite, return + persist the report
GET  /ParaphraseEval/Reports          → list previously-persisted reports
```

The controller filters `Configuration/QuestionSuites/*.json` to suites whose scenarios
carry a non-empty `ClusterId` — so the standard catalog doesn't pollute the dropdown.

## Offline paraphrase generator

The `OfflineParaphraseGenerator` takes a small seed set of verified Q→SQL pairs and uses
an `ILlmClient` to produce N varied phrasings per seed. The generator's prompt is also
abstract — it asks the LLM for varied phrasings and lets the LLM choose its own short
label per paraphrase. No predefined separator enumeration.

```csharp
var seeds = await VerifiedQuerySeedLoader.LoadFromFileAsync(
    "Areas/AnalystAgent/Configuration/verified-queries.json");

var suite = await generator.GenerateAsync(seeds, new ParaphraseGenerationOptions(
    SuiteName: "Paraphrase suite — generated from verified queries",
    SuiteDescription: "Auto-expanded by OfflineParaphraseGenerator",
    ParaphraseCount: 10));

var json = JsonSerializer.Serialize(suite, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(
    "Areas/AnalystAgent/Configuration/QuestionSuites/suite-paraphrase-from-verified-2026-05-24.json",
    json);
```

Bind the `ILlmClient` to your frontier model for the best paraphrase variety — local
small models produce bland paraphrases that don't actually stress the pipeline.
