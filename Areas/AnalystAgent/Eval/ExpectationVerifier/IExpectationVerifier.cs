namespace AnalystAgent.Eval.ExpectationVerifier;

using ServiceOpsAI.Models.AI;

/// <summary>
/// Verifies a generated SQL query against the semantic expectations declared on a
/// <see cref="CopilotAssessmentCase"/> (Expected* fields). Produces a per-question
/// verdict (Pass / Partial / Fail) and per-expectation diagnostics so the assessment
/// summary reports SEMANTIC correctness, not just execution success.
///
/// <para>The verifier is intentionally string-and-shape-based, not full T-SQL AST parsing,
/// for two reasons: (1) the Expected* fields are coarse-grained anyway (column names + ops,
/// not exact syntax); (2) we don't want to false-fail on equivalent SQL formulations
/// (LEFT JOIN + IS NULL vs NOT EXISTS, IN vs OR-chain, etc.). The verifier asks: did the
/// SQL touch the expected entity, mention each expected filter column with the right
/// operator semantic, and emit the expected aggregation function? If yes, it's a pass.</para>
/// </summary>
public interface IExpectationVerifier
{
    VerificationResult Verify(CopilotAssessmentCase testCase, string? generatedSql);
}

public sealed record VerificationResult(
    VerificationVerdict Verdict,
    int ChecksPassed,
    int ChecksTotal,
    IReadOnlyList<string> PassedChecks,
    IReadOnlyList<string> FailedChecks)
{
    public string Summary => $"{Verdict} ({ChecksPassed}/{ChecksTotal})";
}

public enum VerificationVerdict
{
    /// <summary>All expected facets present in the generated SQL.</summary>
    Pass,
    /// <summary>≥ 60% of facets present.</summary>
    Partial,
    /// <summary>Less than 60% present (or the entity itself is wrong).</summary>
    Fail,
    /// <summary>Nothing to verify (e.g. SAFETY refusal — no generated SQL).</summary>
    NotApplicable,
}
