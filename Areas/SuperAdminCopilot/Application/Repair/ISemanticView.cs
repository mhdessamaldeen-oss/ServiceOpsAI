namespace SuperAdminCopilot.Application.Repair.Semantic;

using System.Collections.Generic;
using SuperAdminCopilot.Application.Repair;       // NaturalKeyFormat

/// <summary>
/// Read-only view of the semantic layer (entity declarations, display columns, label columns,
/// natural-key columns, searchable columns) that repair rules consult. Wraps v2's
/// <c>ISemanticLayer</c> with a smaller, rule-friendly surface.
/// </summary>
public interface ISemanticView
{
    EntityView? GetEntity(string table);
    string? LabelColumnFor(string table);
    string? NaturalKeyColumnFor(string table);
    IReadOnlyList<string> SearchableColumnsFor(string table);
    IReadOnlyList<string> DisplayColumnsFor(string table);

    /// <summary>Default date column for <paramref name="table"/> (e.g. <c>"CreatedAt"</c>).
    /// Returns null when the entity declares no date semantics. Used by
    /// <c>UnsolicitedFilterRule</c> to strip date filters on "all-time" cues.</summary>
    string? GetDateColumn(string table, string? role = null);

    IReadOnlyList<string> NumericColumnPreference { get; }
    IReadOnlyList<string> AuxiliaryTableSuffixes { get; }

    /// <summary>Resolve a synonym value to its canonical form
    /// ("urgent" → "Critical"). Returns the input unchanged when no synonym matches.</summary>
    string ResolveSynonymValue(string columnRef, string value);

    /// <summary>Tables reachable from <paramref name="root"/> via FK that the catalog identifies
    /// as lookup-shaped (small row count, has a LabelColumn).</summary>
    IReadOnlyList<string> ReachableLookupTables(string root);

    /// <summary>(table, naturalKeyColumn, formatRegex) tuples for every entity declaring a
    /// natural-key format in semantic-layer.json. Driven by config — universal.</summary>
    IReadOnlyList<NaturalKeyFormat> NaturalKeyFormats { get; }

    /// <summary>Derived-metric hints declared in semantic-layer.json
    /// (e.g. "MTTR" → DATEDIFF(MINUTE, CreatedAt, ResolvedAt)). Returns the matching
    /// hint when the question phrasing fires, or null. Driven entirely by config.</summary>
    DerivedMetricHint? ResolveDerivedMetric(string question, string root);

    /// <summary>Concept-pattern filters declared in semantic-layer.json
    /// (e.g. "overdue" → Status='Issued' AND DueDate &lt; today). Returns matching patterns.</summary>
    IReadOnlyList<ConceptPatternMatch> MatchConceptPatterns(string question, string root);

    /// <summary>Recognised lifecycle-verb date-role hints from semantic-layer.json
    /// (e.g. "resolved" → ResolvedAt for Tickets). Returns null when no entity has the verb mapped.</summary>
    string? ResolveDateRoleForVerb(string verb, string root);

    /// <summary>True when <paramref name="column"/> on <paramref name="entity"/> is declared
    /// as a lifecycle status column in semantic-layer.json (<c>temporalStatusColumns</c>).
    /// Used by <c>UnsolicitedFilterRule</c> to decide which columns are safe to strip on
    /// "all-time" cues without resorting to a hardcoded "Status" string check.</summary>
    bool IsTemporalStatusColumn(string entity, string column);
}

public sealed record DerivedMetricHint(string Function, string Expression, string Alias);
public sealed record ConceptPatternMatch(string ConceptName, IReadOnlyList<ConceptFilter> Filters);
public sealed record ConceptFilter(string Column, string Op, string Value);

public sealed record EntityView(
    string Table,
    string Name,
    IReadOnlyList<string> Synonyms,
    string? LabelColumn,
    string? NaturalKeyColumn,
    IReadOnlyList<string> SearchableColumns,
    IReadOnlyList<string> DisplayColumns);
