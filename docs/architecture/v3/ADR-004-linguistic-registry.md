# ADR-004 — One LinguisticRegistry, no inline regex

**Status:** Accepted
**Date:** 2026-05-29

## Context

The locked principle says "no hardcoded vocab in C#." V2 ships with 21 incidents of regex/string-literal vocab living inside repair phases despite a `linguistic-cues.json` provider already existing. Why? Because nothing in the architecture **forced** phases to use the provider — each phase was free to inline its own regex.

## Decision

V3 introduces `ILinguisticRegistry` as the **only** abstraction repair rules may consult for question text patterns:

```csharp
public interface ILinguisticRegistry
{
    bool HasCue(string question, CueKind kind);
    IReadOnlyList<TemporalSpan> ExtractTemporal(string question);
    IReadOnlyList<StatusMention> ExtractStatus(string question);
    IReadOnlyList<EntityMention> ExtractEntityMentions(string question);
    Superlative? ExtractSuperlative(string question);
    AntiJoinMention? ExtractAntiJoin(string question);
    TextSearchMention? ExtractTextSearch(string question);
    NumericRange? ExtractRange(string question);
}

public enum CueKind { Absence, AllTime, Distinct, Negation, Comparison }
```

The registry **internally** uses `ILinguisticCuesProvider` (the v2 provider), `ISemanticLayer`, and small parser helpers. Repair rules see only `ILinguisticRegistry`.

## Architecture test

```csharp
[Fact]
public void NoRegexLiteralsOutsideRegistry()
{
    var phases = typeof(IRepairRule).Assembly.GetTypes()
        .Where(t => typeof(IRepairRule).IsAssignableFrom(t));
    // Use Roslyn to scan source; assert no `new Regex(...)` outside Registry namespace.
}
```

(Implementation in `Tests/V3.Tests/Architecture/NoHardcodedVocabTests.cs` — uses NetArchTest to enforce.)

## Consequences

- Adding a new locale or dialect = JSON edit only.
- Repair rules never see the question's raw bytes — they see typed "mentions."
- Drift back to inline regex caught by CI before the PR merges.

## Alternatives rejected

- **Trust code review.** Tried — Reviewer D found 21 violations in v2. Doesn't scale.
- **Roslyn analyzer.** Heavier than NetArchTest; same outcome.
