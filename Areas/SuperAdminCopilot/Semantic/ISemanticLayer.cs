namespace SuperAdminCopilot.Semantic;

/// <summary>
/// Lookup interface over the loaded semantic-layer config. The compiler asks for soft-delete
/// filters per table; the planner-prompt builder asks for the metrics/dimensions/synonyms
/// summary; the compiler asks for metric/dimension expansions when the planner emits
/// "metric:&lt;name&gt;" / "dimension:&lt;name&gt;" tokens.
/// </summary>
public interface ISemanticLayer
{
    SemanticLayerConfig Config { get; }

    /// <summary>Resolve a name OR synonym to an entity (case-insensitive).</summary>
    EntityDefinition? GetEntityByNameOrSynonym(string nameOrSynonym);

    /// <summary>Resolve a table back to its semantic entity, if one is declared.</summary>
    EntityDefinition? GetEntityForTable(string table);

    /// <summary>Resolve a metric by canonical name OR synonym.</summary>
    MetricDefinition? GetMetric(string nameOrSynonym);

    /// <summary>Resolve a dimension by canonical name OR synonym.</summary>
    DimensionDefinition? GetDimension(string nameOrSynonym);

    /// <summary>
    /// If the table has a soft-delete column declared, returns the (column, value) pair the
    /// compiler should auto-inject as a WHERE filter. Returns null if no soft-delete is configured.
    /// </summary>
    (string Column, object? Value)? GetSoftDeleteFilter(string table);

    /// <summary>
    /// If <paramref name="value"/> is a known alias for a column-context synonym (e.g. "urgent"
    /// targeting TicketPriorities.Name → "Critical"), returns the canonical value. Otherwise
    /// returns the input unchanged. Used by the compiler to rewrite filter values.
    /// </summary>
    string ResolveSynonymValue(string column, string value);

    /// <summary>
    /// True when the column on the given table is declared sensitive (PII, secrets, audit-only)
    /// in the semantic layer's <see cref="EntityDefinition.SensitiveColumns"/>. Compiler refuses
    /// to project such columns; executor masks any rows that slip through; explainer skips them
    /// in narratives. Match is case-insensitive on both table and column names.
    /// </summary>
    bool IsSensitiveColumn(string table, string column);

    /// <summary>Sensitive-column lookup for an entire table — used by the executor's row-level
    /// redaction pass to know which keys to mask in result dictionaries.</summary>
    IReadOnlySet<string> GetSensitiveColumns(string table);

    /// <summary>
    /// Resolve a date-role verb ("created", "updated", "closed", "resolved", "shipped",
    /// "due", null for default) to the actual column name on <paramref name="table"/>.
    /// Reads <see cref="EntityDefinition.DateRoles"/> first, then the entity's "default" role,
    /// then falls back to the literal "CreatedAt" name. Returns null when the entity is unknown.
    /// Caller is responsible for verifying the returned column actually exists on the table
    /// (use <c>IEntityCatalog.ColumnExists</c>) — semantic config can outdate the schema.
    /// </summary>
    string? GetDateColumn(string table, string? role);

    /// <summary>
    /// One block summarizing metrics/dimensions/synonyms for inclusion in the planner system
    /// prompt. Returns empty string when nothing is configured.
    /// </summary>
    string BuildPromptSummary();
}
