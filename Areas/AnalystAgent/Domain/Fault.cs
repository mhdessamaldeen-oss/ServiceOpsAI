namespace AnalystAgent.Domain;

/// <summary>
/// Classification of every domain-level failure mode. Used as the discriminator on
/// <see cref="Fault"/>. The enum stays small on purpose — if a new value is needed, an ADR
/// must justify it. See ADR-002.
/// </summary>
public enum FaultKind
{
    /// <summary>Required input was missing or whitespace.</summary>
    MissingInput,

    /// <summary>A configured regex failed to compile at startup.</summary>
    RegexCompileFailed,

    /// <summary>A table or column referenced in the spec doesn't exist in the catalog.</summary>
    SchemaUnknown,

    /// <summary>The spec was emitted but fails semantic validation (e.g. unsupported op).</summary>
    ValidationFailed,

    /// <summary>SQL execution returned a database error.</summary>
    SqlExecError,

    /// <summary>The LLM provider was unreachable or returned an error.</summary>
    LlmUnavailable,

    /// <summary>The scope gate rejected the question.</summary>
    OutOfScope,

    /// <summary>Estimated cost exceeds the per-question budget.</summary>
    CostBudgetExceeded,

    /// <summary>Retrieval returned zero candidate tables — pipeline cannot proceed.</summary>
    RetrievalEmpty,

    /// <summary>SQL execution exceeded the command-timeout window.</summary>
    ExecutionTimeout,

    /// <summary>Policy gate (WriteIntent / OperationalGuard / AccessPolicy) refused the question.</summary>
    PolicyRefused,
}

/// <summary>
/// A domain failure: a typed payload describing one of the <see cref="FaultKind"/> classes.
/// Discriminated-union-style — pattern-match via the derived records below.
/// </summary>
public abstract record Fault(FaultKind Kind, string Detail);

public sealed record MissingInputFault(string Field)
    : Fault(FaultKind.MissingInput, $"Required input '{Field}' was missing.");

public sealed record RegexCompileFault(string Pattern, string Error)
    : Fault(FaultKind.RegexCompileFailed, $"Pattern /{Pattern}/ failed: {Error}");

public sealed record SchemaUnknownFault(string Table, string? Column = null)
    : Fault(FaultKind.SchemaUnknown,
            Column is null ? $"Table '{Table}' not found in catalog."
                           : $"Column '{Table}.{Column}' not found in catalog.");

public sealed record ValidationFault(string Reason)
    : Fault(FaultKind.ValidationFailed, Reason);

public sealed record SqlExecFault(string Sql, string DbError)
    : Fault(FaultKind.SqlExecError, $"SQL exec failed: {DbError}");

public sealed record LlmUnavailableFault(string Model, string Error)
    : Fault(FaultKind.LlmUnavailable, $"LLM '{Model}' unavailable: {Error}");

public sealed record OutOfScopeFault(string Reason)
    : Fault(FaultKind.OutOfScope, Reason);

public sealed record CostBudgetFault(decimal ActualUsd, decimal BudgetUsd)
    : Fault(FaultKind.CostBudgetExceeded,
            $"Cost ${ActualUsd:F4} exceeds budget ${BudgetUsd:F4}.");

public sealed record RetrievalEmptyFault(string Reason)
    : Fault(FaultKind.RetrievalEmpty, $"Retrieval empty: {Reason}");

public sealed record ExecutionTimeoutFault(int SecondsElapsed, int CapSeconds)
    : Fault(FaultKind.ExecutionTimeout, $"SQL exceeded {CapSeconds}s timeout (ran for {SecondsElapsed}s).");

public sealed record PolicyRefusedFault(string Gate, string Reason)
    : Fault(FaultKind.PolicyRefused, $"Refused by {Gate}: {Reason}");
