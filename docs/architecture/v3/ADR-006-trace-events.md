# ADR-006 — Event-sourced trace

**Status:** Accepted
**Date:** 2026-05-29

## Context

V2's trace is one giant JSON blob (`ExecutionPlan`) inside one row in `CopilotTraceHistories`. The investigation page parses it and guesses. Multi-LLM-call stages collapse into one column (`ModelName`); the user can't see "which model did what."

## Decision

V3 emits **typed events** during pipeline execution. Each event is a record implementing `IPipelineEvent`:

```csharp
public record QuestionReceived(Guid TraceId, DateTime At, string Question, string? CaseCode) : IPipelineEvent;
public record StageStarted(Guid TraceId, DateTime At, string Stage) : IPipelineEvent;
public record LlmCalled(Guid TraceId, DateTime At, string Stage, string Model,
                       int PromptTokens, int CompletionTokens, decimal CostUsd,
                       long ElapsedMs, string? PromptPreview, string? ResponsePreview) : IPipelineEvent;
public record FaultRepaired(Guid TraceId, DateTime At, FaultKind Fault, string RuleName,
                            string Detail, string BeforeSpecHash, string AfterSpecHash) : IPipelineEvent;
public record SqlCompiled(Guid TraceId, DateTime At, string Sql,
                          ImmutableDictionary<string, object?> Parameters) : IPipelineEvent;
public record SqlExecuted(Guid TraceId, DateTime At, int RowCount, long ElapsedMs) : IPipelineEvent;
public record StageCompleted(Guid TraceId, DateTime At, string Stage, long ElapsedMs, string Status) : IPipelineEvent;
public record AnswerProduced(Guid TraceId, DateTime At, string Answer) : IPipelineEvent;
public record FaultRaised(Guid TraceId, DateTime At, FaultKind Kind, string Detail) : IPipelineEvent;
```

Events flow through `IPipelineEventBus`, batched by `TraceEventWriter` (hosted service), persisted to a new `CopilotTraceEvents` table:

```sql
CREATE TABLE CopilotTraceEvents (
    Id BIGINT IDENTITY PRIMARY KEY,
    TraceId UNIQUEIDENTIFIER NOT NULL,
    OccurredAt DATETIME2 NOT NULL,
    EventType NVARCHAR(64) NOT NULL,
    PayloadJson NVARCHAR(MAX) NOT NULL,
    INDEX IX_CopilotTraceEvents_TraceId (TraceId, OccurredAt),
    INDEX IX_CopilotTraceEvents_OccurredAt (OccurredAt) /* retention sweep */
);
```

The legacy `CopilotTraceHistories` row becomes a **projection** built by an event-handler — it remains the row the assessment grid queries, but it's derived data, not authoritative.

## Per-step model question — solved naturally

The user's "save 6 models" requirement is satisfied: there is one `LlmCalled` event per LLM call. Each carries its own `Model` field. The grid shows the set; the investigation page lists them in order.

## Consequences

- Investigation page reads events for a `TraceId`, groups by `Stage`, renders chronologically. No JSON parsing.
- Multi-model deployments transparent.
- Replayable: given the events, you can rebuild any intermediate state.
- Storage grows linearly with question volume — bounded by retention sweep (existing job, repointed).

## Alternatives rejected

- **Extend `CopilotTraceHistories` with more columns.** Would have added `StepModelsJson` (we did, in v2 for compatibility) and more — but you still parse JSON to render the page. Doesn't fix the underlying shape problem.
- **OpenTelemetry spans.** Promising but adds infra weight and ties trace shape to OTLP. Defer.
