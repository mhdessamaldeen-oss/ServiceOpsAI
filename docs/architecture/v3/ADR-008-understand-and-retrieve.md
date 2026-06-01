# ADR-008 — Understand + Retrieve stages

**Status:** DRAFT (post-review skeleton — to be expanded next session)
**Date:** 2026-05-29

## Context

ADR-001 named the 7 stages but only ADR-005 fully specified one (Repair). The team review
flagged that Understand + Retrieve carry critical mechanisms (scope gate, verified
queries, schema-link retrieval, past-trace RAG) and need their own ADR before code lands.

## Decision (provisional, to be ratified)

### Understand stage

Input: raw question string, locale (en/ar/auto), optional `SessionContext`.

Output: `ParsedQuestion`:
- `Question` (string, original)
- `Locale` (detected — en/ar)
- `ShapeHint` (best-guess shape: SEL / FLT / AGG / GRP / TIM / ANT / LKP / TXT — informational only)
- `EntityMentions` (typed mentions via Registry)
- `TimeIntent` (typed temporal scope)
- `Faults` (any out-of-scope / policy-refused faults raised early)

Implementations consulted (Strategy):
- `ScopeGatePredicate` — port of v2's `ScopeConfidenceGate`. Returns `OutOfScopeFault` when
  triggered.
- `LocaleDetector` — port of v2's `QuestionLanguageDetector`.
- `TimeIntentExtractor` — port of v2's existing extractor; returns the same `TimeIntent`
  shape v3 already declares.

Faults: `OutOfScopeFault`, `MissingInputFault`, `PolicyRefusedFault`.

### Retrieve stage

Input: `ParsedQuestion`.

Output: `RetrievalSet`:
- `CandidateTables` (top-K from semantic retriever)
- `VerifiedQueryHit` (optional — exact / near-exact match from `verified-queries.json`)
- `SimilarPastTraces` (top-K from past-trace RAG)
- `FewShotExamples` (top-K from `few-shot-examples.json`)

Implementations (Composite):
- `SchemaSemanticRetriever` (port from v2 — wraps the embedder + auxiliary-penalty config).
- `VerifiedQueryMatcher` (port from v2 — compare-shape guard intact).
- `PastTraceRagRetriever` (port from v2).
- `FewShotRetriever` (port from v2).

Each implementation is a `IRetrievalSource` and contributes to the `RetrievalSet`. The
stage runs them in parallel (`Task.WhenAll`) and collates.

Faults: `RetrievalEmptyFault` (zero candidate tables across all sources).

## Consequences

- The Plan stage receives a typed `RetrievalSet` with everything it needs. No more "the
  retriever is a static singleton called by 4 phases."
- `VerifiedQueryStore` migrates verbatim — the JSON file stays authoritative.
- A new "retrieval source" = one class implementing `IRetrievalSource`. Pluggable.

## Open questions for next session

1. Does the Composite pattern compose retrieval sources well, or do we want a more explicit
   Router (verified-query > past-trace > schema retrieval)? Router probably wins for
   short-circuit efficiency.
2. How does the past-trace RAG interact with v3 events vs v2 rows during the shadow phase?
   See ADR-006 amendment notes.
3. Embedder timeout / fail-open invariant (from memory `embedder-fail-open`) — Retrieve must
   preserve it. Spec the test.
