namespace SuperAdminCopilot.Semantic;

/// <summary>
/// Root config loaded from <c>Areas/SuperAdminCopilot/Configuration/semantic-layer.json</c>.
/// Maps the user's vocabulary to canonical entities, metrics, dimensions, and synonyms so the
/// planner doesn't have to invent SQL expressions for "active customer", "urgent", or "this
/// month's revenue".
/// </summary>
public sealed class SemanticLayerConfig
{
    public List<EntityDefinition> Entities { get; set; } = new();
    public List<MetricDefinition> Metrics { get; set; } = new();
    public List<DimensionDefinition> Dimensions { get; set; } = new();
    public List<SynonymGroup> Synonyms { get; set; } = new();

    /// <summary>
    /// Cross-entity defaults the compiler reads when an entity definition is missing a value
    /// (e.g. no explicit <see cref="EntityDefinition.LabelColumn"/>). Lets new schemas override
    /// the framework's generic SQL conventions without touching code.
    /// </summary>
    public SemanticDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Semantic concept patterns for the <see cref="Pipeline.Stages.QuestionRewriter"/>.
    /// Each pattern maps natural-language triggers ("unattended", "stale", "backlog") to
    /// concrete database operations (filters, metrics, query shapes). New concepts are
    /// added here — zero code changes required.
    /// </summary>
    public List<ConceptPattern> ConceptPatterns { get; set; } = new();
}

/// <summary>
/// Cross-entity defaults from semantic-layer.json's top-level <c>defaults</c> section.
/// Each list is consulted by the compiler as a fallback when the entity doesn't declare its
/// own value. Editing this section is the way to tune generic-SQL conventions for a new
/// schema — no code change required.
/// </summary>
public sealed class SemanticDefaults
{
    /// <summary>
    /// Ordered preference list for picking a row-label column when the entity has no explicit
    /// <see cref="EntityDefinition.LabelColumn"/>. The compiler walks this list, returns the
    /// first column that exists on the table.
    ///
    /// <para><b>Single-source convention:</b> the C# default is intentionally empty. Values
    /// live in <c>semantic-layer.json</c>'s <c>defaults.labelColumnPreference</c> block.
    /// Empty list = no fallback; entities without an explicit <see cref="EntityDefinition.LabelColumn"/>
    /// won't get FK-name rewrite or default-label SELECT (safe degradation, not a crash).</para>
    /// </summary>
    public List<string> LabelColumnPreference { get; set; } = new();

    /// <summary>
    /// Ordered preference list for picking a numeric column to aggregate (SUM/AVG/MAX/MIN)
    /// when the planner didn't specify. First column from this list that exists on the root
    /// wins. Lives in <c>semantic-layer.json</c>'s <c>defaults.numericColumnPreference</c>.
    /// Edit there to teach the pipeline about new financial / measurement columns; no recompile.
    /// </summary>
    public List<string> NumericColumnPreference { get; set; } = new();

    /// <summary>
    /// Substring hints (case-insensitive contains) that signal a column probably holds a
    /// numeric value. Used as a fallback when nothing from <see cref="NumericColumnPreference"/>
    /// matches. Lives in <c>semantic-layer.json</c>'s <c>defaults.numericColumnHints</c>.
    /// </summary>
    public List<string> NumericColumnHints { get; set; } = new();

    /// <summary>
    /// Table-name suffixes that mark a table as auxiliary / satellite (history, audit, log,
    /// notification, snapshot, event). Schema retrievers apply a small score penalty to these
    /// so that when the user asks "show me outages", the main <c>Outages</c> entity ranks
    /// above <c>OutageHistories</c> / <c>OutageNotifications</c>. Lives in
    /// <c>semantic-layer.json</c>'s <c>defaults.auxiliaryTableSuffixes</c>. Universal —
    /// works for any future satellite table without naming it in code.
    /// </summary>
    public List<string> AuxiliaryTableSuffixes { get; set; } = new();
}

/// <summary>
/// Semantic concept pattern: maps natural-language trigger phrases to concrete DB
/// operations. Used by the <see cref="Pipeline.Stages.QuestionRewriter"/> to bridge
/// the gap between business vocabulary and database schema.
/// </summary>
public sealed class ConceptPattern
{
    /// <summary>Natural-language phrases that activate this concept (e.g. "unattended", "no assignee").</summary>
    public List<string> Triggers { get; set; } = new();
    /// <summary>Human-readable explanation of what this concept means in database terms.</summary>
    public string Meaning { get; set; } = "";
    /// <summary>Filters to inject when this concept is matched (uses QuerySpec FilterSpec format).</summary>
    public List<ConceptFilter> Filters { get; set; } = new();
    /// <summary>If set, the metric from the Metrics list to associate with this concept.</summary>
    public string? Metric { get; set; }
    /// <summary>If set, implies a GROUP BY on this dimension/column for the query.</summary>
    public string? ImpliedGroupBy { get; set; }
    /// <summary>If set, overrides the inferred query shape (topN, aggregate, list, count).</summary>
    public string? QueryShape { get; set; }
    /// <summary>If set, overrides the default ORDER BY direction (asc/desc).</summary>
    public string? OrderDirection { get; set; }
}

/// <summary>Filter used inside a <see cref="ConceptPattern"/>.</summary>
public sealed class ConceptFilter
{
    public string Column { get; set; } = "";
    public string Op { get; set; } = "eq";
    public object? Value { get; set; }
}

/// <summary>
/// A canonical entity (typically backed by one table). Carries the soft-delete column declaration
/// so the compiler can auto-inject a "WHERE IsDeleted = 0" filter without the LLM having to
/// remember it.
/// </summary>
public sealed class EntityDefinition
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public List<string> Synonyms { get; set; } = new();
    public string? SoftDeleteColumn { get; set; }
    public object? SoftDeleteFilterValue { get; set; }
    public string Description { get; set; } = "";

    /// <summary>
    /// True for small lookup / dimension tables (TicketStatuses, TicketPriorities, etc.) whose
    /// rows are categories rather than facts. Used by FactRootGuard to detect the "Categories
    /// with at least 3 tickets" inversion where the planner picks the dimension as root and
    /// COUNT(*) ends up counting categories instead of the fact (Tickets) related to them.
    /// </summary>
    public bool IsLookup { get; set; }

    /// <summary>
    /// Columns that should be searched when the user asks "X about &lt;phrase&gt;" /
    /// "X containing &lt;phrase&gt;" without naming a specific column. The compiler's
    /// text_search filter op fans a single value across ALL columns listed here with OR.
    /// Example: for Tickets, ["Title", "Description"] means a text_search hits both fields.
    /// Empty list = no cross-column search; the user must name the column explicitly.
    /// </summary>
    public List<string> SearchableColumns { get; set; } = new();

    /// <summary>
    /// Columns whose values must NEVER be returned to users. The compiler refuses to put them in
    /// SELECT; the executor masks them in result rows; the explainer skips them in narratives.
    /// Examples: AspNetUsers.PasswordHash, AspNetUsers.SecurityStamp, AspNetUsers.ConcurrencyStamp,
    /// TicketAiAnalyses.DiagnosticMetadata. Lower-case match by column name (case-insensitive).
    /// </summary>
    public List<string> SensitiveColumns { get; set; } = new();

    /// <summary>
    /// The entity's natural-key column — a human-friendly identifier the user types instead of
    /// the surrogate primary key. For Tickets it's "TicketNumber" (e.g. "TCK-2026-00050"); for
    /// Orders it'd be "OrderNumber"; for Customers it could be "CustomerCode". The natural-key
    /// lookup question-shape uses this column when the user mentions an ID in the question, and
    /// the planner prompt advertises it as the canonical filter column for ID lookups.
    /// Empty / null means the entity has no externally-meaningful natural key.
    /// </summary>
    public string? NaturalKeyColumn { get; set; }

    /// <summary>
    /// Regex describing the natural-key value format (the part the user types). Used by the
    /// natural-key lookup shape to detect IDs in free text and by the planner prompt to teach
    /// the model what an ID looks like. Examples:
    ///   • Tickets: <c>"^[A-Za-z]{1,6}-\d{4}-\d{3,7}$"</c>  (TCK-2026-00050, INC-2026-001)
    ///   • Orders:  <c>"^ORD-\d{4}-\d{5}$"</c>
    ///   • Generic alphanumeric: <c>"^[A-Z]{2,5}-\d{3,10}$"</c>
    /// When null, the shape falls back to a generic <c>[A-Za-z]+-\d+</c> detector.
    /// </summary>
    public string? NaturalKeyFormat { get; set; }

    /// <summary>
    /// The single column the explainer / list shapes use to identify a row in plain prose.
    /// Examples: "Title" for Tickets, "UserName" for Users, "Name" for Categories.
    /// When null, the shapes fall back to the entity's natural-key column or the first
    /// short string-typed column.
    /// </summary>
    public string? LabelColumn { get; set; }

    /// <summary>
    /// Default columns to project when the user asks "show me X" without naming columns.
    /// Order matters — these are emitted in SELECT in this order. Empty list = the planner /
    /// compiler picks a sensible default (PK + natural key + label).
    /// </summary>
    public List<string> DisplayColumns { get; set; } = new();

    /// <summary>
    /// Maps a date-role keyword (verbs the user types — "created", "updated", "closed",
    /// "resolved", "shipped", "due") to the actual column name on this entity. The temporal
    /// shapes and <c>LiteralDateGuard</c> consult this map before falling back to "CreatedAt",
    /// so "tickets closed last month" filters on <c>ClosedAt</c> and not <c>CreatedAt</c>.
    /// Special key <c>"default"</c> sets the column used when the user names no verb at all
    /// ("tickets in the last 7 days"). When the dictionary is empty, callers default to
    /// "CreatedAt" if the table has that column; otherwise the first DATE/DATETIME column.
    /// </summary>
    public Dictionary<string, string> DateRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Templated follow-up prompts the SuggestedPromptProvider offers after a successful answer
    /// on this entity. Use <c>{entity}</c> as a placeholder for the entity's display name.
    /// Examples for Tickets: ["Break down {entity} by status", "Show only open {entity}",
    /// "Top 5 {entity} by recent activity"]. When empty, the provider falls back to its generic
    /// pool from <see cref="Configuration.CopilotTextCatalog.SuggestedFollowupTemplates"/>.
    /// </summary>
    public List<string> SuggestedFollowupTemplates { get; set; } = new();

    /// <summary>
    /// Whether this entity has semantic-vector embeddings available. Today: Tickets only
    /// (see TicketSemanticEmbeddings table). The <c>SemanticSearchHandler</c> gates its
    /// similarity-and-text-search paths on this flag — when false, the request falls
    /// through to the SQL planner which uses LIKE/=/IN against the entity's
    /// <see cref="SearchableColumns"/>. This is the policy from the embedding-fallback-policy
    /// memory: don't refuse questions just because the entity isn't vectorized.
    /// </summary>
    public bool HasEmbeddings { get; set; }
}

/// <summary>
/// A canonical metric: a named aggregate plus optional baked-in filters.
/// Example: "open_ticket_count" → COUNT(*) WHERE TicketStatuses.Name = 'Open'.
/// The planner can emit { "function": "metric:open_ticket_count" } and the compiler expands it.
/// </summary>
public sealed class MetricDefinition
{
    public string Name { get; set; } = "";
    /// <summary>
    /// SQL aggregate fragment. Examples: "COUNT(*)", "SUM(Tickets.Hours)",
    /// "AVG(DATEDIFF(hour, Tickets.CreatedAt, Tickets.ResolvedAt))".
    /// </summary>
    public string Expression { get; set; } = "";
    public List<MetricFilter> Filters { get; set; } = new();
    public List<string> Synonyms { get; set; } = new();
    public string Description { get; set; } = "";
}

/// <summary>
/// A canonical dimension. Either a plain column reference (Column) or a derived expression
/// (Expression). The planner can emit "dimension:&lt;name&gt;" and the compiler renders it.
/// </summary>
public sealed class DimensionDefinition
{
    public string Name { get; set; } = "";
    /// <summary>Column reference, e.g. "TicketStatuses.Name". Set this OR <see cref="Expression"/>.</summary>
    public string? Column { get; set; }
    /// <summary>Raw SQL expression, e.g. "DATEDIFF(day, Tickets.CreatedAt, GETDATE())".</summary>
    public string? Expression { get; set; }
    public List<string> Synonyms { get; set; } = new();
    public string Description { get; set; } = "";
}

/// <summary>
/// A vocabulary mapping. Aliases ("urgent", "asap", "p0") all resolve to a canonical value
/// ("Critical") in a specific context ("TicketPriorities.Name"). The planner sees these listed
/// in the system prompt; the compiler can also rewrite filter values that are still in alias form.
/// </summary>
public sealed class SynonymGroup
{
    public string Canonical { get; set; } = "";
    /// <summary>"value" | "table" | "column" — what kind of token the canonical refers to.</summary>
    public string Type { get; set; } = "value";
    /// <summary>For value-type synonyms: the column the canonical applies to. Lets the compiler
    /// rewrite filter values that target this column.</summary>
    public string? Context { get; set; }
    public List<string> Aliases { get; set; } = new();
}

public sealed class MetricFilter
{
    public string Column { get; set; } = "";
    public string Op { get; set; } = "eq";
    public object? Value { get; set; }
}
