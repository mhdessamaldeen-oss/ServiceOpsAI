namespace SuperAdminCopilot.Application.Repair;

using System.Collections.Generic;
using SuperAdminCopilot.Domain;
using SuperAdminCopilot.Models;

/// <summary>
/// One typed fix for one fault class. See ADR-005.
///
/// <para>A rule is <see cref="Detect"/> + <see cref="Apply"/>. <see cref="Detect"/> never
/// mutates. <see cref="Apply"/> mutates the spec IN PLACE and returns it for chaining —
/// the 2026-06-01 single-spec collapse eliminated the v3 immutable form and the lossy
/// converter; the bus, all rules, and the compiler now share one canonical QuerySpec.</para>
/// </summary>
public interface IRepairRule
{
    /// <summary>The fault class this rule corrects. One rule per class.</summary>
    RepairFaultKind FaultClass { get; }

    /// <summary>Maximum planner tier at which this rule fires. Stronger planners skip more rules.</summary>
    PlannerTier MaxTier { get; }

    /// <summary>Other fault classes that must be repaired BEFORE this rule runs.</summary>
    IReadOnlyList<RepairFaultKind> Requires { get; }

    /// <summary>
    /// True if a diagnosable instance of <see cref="FaultClass"/> is present in
    /// <paramref name="spec"/>. Never mutates. Returns the diagnosis payload (or a fault for
    /// inconvertible context — e.g. semantic layer unavailable).
    /// </summary>
    Result<Diagnosis, Fault> Detect(QuerySpec spec, RepairContext ctx);

    /// <summary>
    /// Apply the repair. Mutates <paramref name="spec"/> in place and returns the same
    /// instance for chaining. Implementations should preserve identity (return spec) so the
    /// bus can rely on a single object reference threading through every rule.
    /// </summary>
    QuerySpec Apply(QuerySpec spec, Diagnosis diagnosis);
}

/// <summary>One rule's view of the world. Frozen for the call. Repair rules don't talk to the DB.</summary>
public sealed record RepairContext(
    string Question,
    ILinguisticRegistry Linguistics,
    Schema.ISchemaView Schema,
    Semantic.ISemanticView Semantic,
    PlannerTier ActiveTier,
    // Classified prompt shape (CNT / GRP / SUB / WIN / SEL / TIM / TXT / LKP / etc.). Carried
    // through so the SpecRepair bus can log rule firings tagged by shape — operators can then
    // answer "for COUNT-shape questions, which rules fire most?" and "for SUB-shape, which
    // rules never fire so should be retired?". Empty when not yet classified (early stages).
    string PromptShape = "");

/// <summary>What a rule reports when it sees a fault. Carries enough info for <c>Apply</c>.</summary>
public sealed record Diagnosis(RepairFaultKind Kind, string Detail, object? Payload = null)
{
    /// <summary>Sentinel — "I looked, no fault to fix." Distinct from a Fault.</summary>
    public static readonly Diagnosis NoFault = new(RepairFaultKind.None, "no fault detected");
}

public enum RepairFaultKind
{
    None = 0,
    MissingRoot,
    DanglingColumnReference,
    WrongAggregationShape,
    MissingTemporalScope,
    MissingLookupFilter,
    MissingTextSearch,
    MissingAntiJoin,
    UnsolicitedFilter,
    AmbiguousLimit,
    OverJoin,
    InvalidSelectGroupBy,
    DisplayColumnsMissing,
    // Phase B.5 — additional fault classes covering the 21 v2 phases that were not in
    // the original ADR-005 mapping. Each rule still ≤ 200 LOC, vocab from JSON.
    ValueSynonym,                 // ValueSynonymRule
    FkNameToJoin,                 // FkNameToJoinRule
    NaturalKeyToken,              // NaturalKeyTokenRule
    TimeSeriesBucketing,          // TimeSeriesBucketingRule
    DerivedMetric,                // DerivedMetricRule (deferred — currently no-op)
    LifecycleVerbDate,            // LifecycleVerbDateRule
    MissingOrderByForLimit,       // MissingOrderByForLimitRule
    OuterEntityCount,             // OuterEntityCountRule
    NegationFilter,               // NegationFilterRule
    NumericRange,                 // NumericRangeRule
    ConceptPattern,               // ConceptPatternRule
    FilterDedupe,                 // FilterDedupeRule
    ProjectLabelForFkGroupBy,     // ProjectLabelForFkGroupByRule (2026-05-30 data-driven addition)
    DropUnresolvedSelectColumns,  // DropUnresolvedSelectColumnsRule (2026-05-30 data-driven addition)
    NumericAggregationOnNonNumeric, // NumericAggregationOnNonNumericRule (2026-05-30 iter 4)
    InferSelfJoinFromUnresolvedAlias, // InferSelfJoinFromUnresolvedAliasRule (2026-05-30 close-out Phase 2.1)
    DropUnsafeComputedExpressions,    // DropUnsafeComputedExpressionsRule (2026-05-30 evening pipeline-crash fix for SUB shape)
    SwapPureCountForListWhenQuestionIsListShape, // SwapPureCountForListWhenQuestionIsListShapeRule (2026-05-30 evening; pre-empts SqlIntentGuard for "complaints in Damascus this month" LIST pattern)
    DropDistinctWhenAggregated,       // DropDistinctWhenAggregatedRule (2026-05-30 evening; pre-empts SqlIntentGuard CheckDistinctWithAggregations refusal)
    WarnUnknownFilterOps,             // WarnUnknownFilterOpsRule (2026-06-01; rewrites typo'd FilterSpec.Op values to "eq" so filters aren't silently dropped)
}

public enum PlannerTier { Weak, Medium, Strong }
