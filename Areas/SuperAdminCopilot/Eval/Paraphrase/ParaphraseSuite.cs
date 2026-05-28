namespace SuperAdminCopilot.Eval.Paraphrase;

using System.Text.Json.Serialization;

/// <summary>
/// Paraphrase-robustness suite. <b>Shape matches the existing assessment suites</b> in
/// <c>Areas/SuperAdminCopilot/Configuration/QuestionSuites/</c> — a flat <see cref="Scenarios"/>
/// array of independent test cases — so the live <c>CopilotAssessmentHandler</c> can
/// list and run these suites unchanged, alongside every other suite.
///
/// <para><b>Cluster grouping is a property of scenarios, not of the file shape.</b> Each
/// scenario carries its own <see cref="ParaphraseScenario.ClusterId"/> and
/// <see cref="ParaphraseScenario.Perturbation"/>. The paraphrase runner groups by
/// <c>ClusterId</c> at report-build time to compute per-cluster and per-perturbation
/// metrics; the existing handler ignores those extra fields (System.Text.Json drops
/// unknown properties by default).</para>
/// </summary>
public sealed record ParaphraseSuite
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0";
    public string Description { get; init; } = "";

    /// <summary>Flat list of scenarios. <b>Same shape</b> as the existing suites' top-level
    /// <c>Scenarios</c> array. Scenarios sharing a <see cref="ParaphraseScenario.ClusterId"/>
    /// express the same intent in different phrasings.</summary>
    public IReadOnlyList<ParaphraseScenario> Scenarios { get; init; } = Array.Empty<ParaphraseScenario>();
}

/// <summary>
/// One test scenario. Field set is a superset of <c>CopilotAssessmentCase</c> so the
/// existing handler deserialises it cleanly; the paraphrase-specific fields
/// (<see cref="ClusterId"/>, <see cref="Perturbation"/>, <see cref="Language"/>) are
/// extras the existing handler ignores.
/// </summary>
public sealed record ParaphraseScenario
{
    // ── Fields the existing CopilotAssessmentHandler consumes ────────────────────────────
    public string Code { get; init; } = "";
    public string Question { get; init; } = "";
    public string Category { get; init; } = "General";
    public string Difficulty { get; init; } = "Medium";

    /// <summary>Must be one of the <c>CopilotIntentKind</c> enum values
    /// (DataQuery / GeneralChat / ExternalToolQuery / Unsupported / …). Almost always
    /// <c>"DataQuery"</c> for paraphrase scenarios.</summary>
    public string ExpectedIntent { get; init; } = "DataQuery";

    /// <summary>Primary entity the question targets. Single-valued by convention of the
    /// existing suites — accepted as imperfect metadata, not used by any logic. For multi-
    /// entity queries, name the most prominent entity; downstream code parses the actual
    /// joins from <see cref="ExpectedSql"/>.</summary>
    public string EntityFocus { get; init; } = "";

    public string ExpectedSql { get; init; } = "";
    public int? ExpectedMinRows { get; init; }
    public int? ExpectedMaxRows { get; init; }
    public int? MaxLatencyMs { get; init; }

    // ── Paraphrase-runner extension fields (ignored by the standard handler) ─────────────

    /// <summary>Stable identifier that groups intent-equivalent paraphrases. The runner
    /// uses this to compute cluster-level pass rates and the per-cluster section of the
    /// report. Scenarios with the same <see cref="ClusterId"/> SHOULD share the same
    /// <see cref="ExpectedSql"/> (duplicated per scenario).</summary>
    public string ClusterId { get; init; } = "";

    /// <summary>Dimension being tested by this paraphrase (e.g. <c>"base"</c>,
    /// <c>"bracket-square"</c>, <c>"typo"</c>, <c>"synonym-ar"</c>). The runner aggregates
    /// pass rates by this label to produce the Dr.Spider-style per-perturbation drop
    /// report. Keep labels stable across suites so cross-run comparison works.</summary>
    public string Perturbation { get; init; } = "base";

    public string Language { get; init; } = "en";

    [JsonPropertyName("_note")]
    public string? Note { get; init; }
}
