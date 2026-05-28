namespace SuperAdminCopilot.Models;

/// <summary>
/// Phase 07 — single owner of temporal intent. Replaces the previous "5 phases race each
/// other" model with one explicit tree. Filled by the deterministic TimeIntentExtractor
/// (which wraps TemporalParser + the regex vocabularies) BEFORE the LLM planner runs.
/// The SQL compiler reads <see cref="TimeIntent"/> and emits the date filter(s); the
/// year-anchored q1_start tokens, the SpecificYearMonthFilter phase, and the
/// InjectTemporalFilterFromQuestion phase ALL retire in favour of this slot.
///
/// <para><b>The shape mirrors the "tree of time" the user requested:</b></para>
/// <code>
///   TimeIntent
///   ├─ Kind = UNQUALIFIED  → no temporal constraint at all
///   ├─ Kind = ABSOLUTE     → Year / Quarter / Month / DateRange
///   ├─ Kind = RELATIVE     → ThisN / LastN / FromNow / Boundary (today/yesterday/tomorrow)
///   └─ Kind = MULTI_PERIOD → list of TimeRange entries, UNION-ALL-ed by the compiler
/// </code>
///
/// <para>One <see cref="TimeIntent"/> instance per QuerySpec. When the question contains
/// no temporal phrase, Kind = UNQUALIFIED and the compiler emits no temporal WHERE.</para>
/// </summary>
public sealed class TimeIntent
{
    public TimeIntentKind Kind { get; set; } = TimeIntentKind.Unqualified;

    /// <summary>For single-range intents (ABSOLUTE + RELATIVE). Null when Kind = MULTI_PERIOD
    /// or Kind = UNQUALIFIED.</summary>
    public TimeRange? Range { get; set; }

    /// <summary>For MULTI_PERIOD intents — each leg becomes a UNION-ALL SELECT with a Label
    /// column carrying the period name. Mirrors the existing <see cref="PeriodSpec"/> shape
    /// so the compiler's existing UNION-ALL path is reused.</summary>
    public List<TimeRange> Periods { get; set; } = new();

    /// <summary>
    /// Name of the date column on the root entity that the intent applies to (resolved via
    /// the entity's <c>DateRoles</c> map). When null, the compiler picks the entity's default
    /// (CreatedAt for legacy, or the date column the role-extractor matched). Unqualified —
    /// the compiler qualifies it at render time.
    /// </summary>
    public string? DateColumn { get; set; }

    /// <summary>Optional role hint ("created" / "paid" / "issued" / …) that informed the
    /// <see cref="DateColumn"/> choice. Preserved for trace + explainer narrative.</summary>
    public string? DateRoleHint { get; set; }

    /// <summary>The original phrase the extractor matched ("Q1 of 2027", "last 30 days").
    /// Used by the explainer to confirm what the copilot understood.</summary>
    public string? SourcePhrase { get; set; }
}

public enum TimeIntentKind
{
    /// <summary>No temporal phrase in the question. Compiler emits no date filter.</summary>
    Unqualified = 0,
    /// <summary>Single absolute period (Q1 2027, Feb 2019, year 2025, between A and B).</summary>
    Absolute = 1,
    /// <summary>Relative to "now" (today, yesterday, last week, last 30 days, this month).</summary>
    Relative = 2,
    /// <summary>Multiple disjoint ranges UNION-ALL'd (Q1 2025 vs Q1 2026, Jan vs Feb).</summary>
    MultiPeriod = 3,
}

/// <summary>
/// A single temporal range as a half-open interval [<see cref="Start"/>, <see cref="End"/>).
/// <see cref="Label"/> identifies the range when used inside <see cref="TimeIntent.Periods"/>
/// for MULTI_PERIOD intents — becomes the SQL projected literal so the user sees which leg
/// each result row belongs to.
/// </summary>
public sealed class TimeRange
{
    /// <summary>Inclusive lower bound. May be null for open-ended ranges (compiler emits no
    /// lower bound — useful for "before 2026" style queries).</summary>
    public DateTime? Start { get; set; }

    /// <summary>Exclusive upper bound. May be null for "since X" style queries.</summary>
    public DateTime? End { get; set; }

    /// <summary>Display name. "Q1 2025" / "Last 30 days" / "February 2026". Required for
    /// MULTI_PERIOD periods (the compiler projects this as a literal column); optional for
    /// single-range. Empty string is fine for single-range.</summary>
    public string Label { get; set; } = string.Empty;
}
