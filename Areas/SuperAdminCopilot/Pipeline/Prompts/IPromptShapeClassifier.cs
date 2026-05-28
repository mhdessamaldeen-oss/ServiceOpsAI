namespace SuperAdminCopilot.Pipeline.Prompts;

/// <summary>
/// Phase 7a — deterministic prompt-shape classifier.
///
/// <para>Reads the user's question BEFORE prompt assembly and predicts which "shape" of
/// SQL it wants. The classification then drives which example bank the prompt-assembler
/// pulls few-shots from (Phase 7b/7c), so a COUNT question doesn't get TOPN examples in
/// its prompt — smaller prompts, sharper signal, better local-model performance.</para>
///
/// <para>This is intentionally separate from the existing
/// <c>IQuestionShapeClassifier</c> (which is a binary <i>Simple vs ComplexAnalytics</i>
/// router for the LlmDirectSqlEmitter escape valve). Different concern, different
/// taxonomy.</para>
/// </summary>
public interface IPromptShapeClassifier
{
    /// <summary>Classify the question into one of <see cref="PromptShape"/>. Always returns
    /// a value — the configured <c>default</c> shape applies when nothing matches.</summary>
    PromptShape Classify(string question);
}

/// <summary>
/// The 8-shape taxonomy. Phase 7b's example banks map 1:1 to these names.
///
/// <para>Ordering of detection (most-specific to least-specific) lives in the JSON config —
/// see <c>Configuration/Prompts/shape-classifier.json</c>. New shapes are added by:
/// (1) adding an enum entry here, (2) adding a pattern block to the JSON, (3) adding a
/// <c>shapes/&lt;shape&gt;.json</c> example bank in Phase 7b.</para>
/// </summary>
public enum PromptShape
{
    /// <summary>"how many X", "count of Y" — single-row COUNT result.</summary>
    COUNT,
    /// <summary>"top 10", "highest by X" — ORDER BY + TOP/LIMIT.</summary>
    TOPN,
    /// <summary>"average X", "sum of Y" — aggregate without time-grouping.</summary>
    AGGREGATE,
    /// <summary>"by month", "over time", "trend" — date-bucketed aggregation.</summary>
    TIMESERIES,
    /// <summary>"this month vs last", "compare X to Y" — cross-period comparison.</summary>
    COMPARE,
    /// <summary>"details of TCK-2026-001", "status of X" — single-row retrieval by ID.</summary>
    LOOKUP,
    /// <summary>"customers with their bills" — explicit multi-entity join request.</summary>
    JOIN,
    /// <summary>"list X where Y", "show me Z" — list-with-conditions, the default catch-all.</summary>
    FILTER,
}
