namespace AISupportAnalysisPlatform.Models.AI
{
    /// <summary>
    /// JSON-serializable snapshot of the validated catalog query that produced a SQL execution.
    /// Persisted on every DataQuery sub-execution so senior audits can prove what plan ran.
    /// </summary>
    public class CopilotCatalogQuerySnapshot
    {
        public string PrimaryEntity { get; set; } = string.Empty;
        public string PrimaryAlias { get; set; } = string.Empty;
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> JoinFragments { get; set; } = new();
        public List<CopilotCatalogProjectionSnapshot> Projections { get; set; } = new();
        public List<CopilotCatalogPredicateSnapshot> Predicates { get; set; } = new();
        public List<CopilotCatalogPredicateSnapshot> HavingPredicates { get; set; } = new();
        public List<string> GroupBy { get; set; } = new();
        public List<CopilotCatalogOrderSnapshot> OrderBy { get; set; } = new();
        public int? Limit { get; set; }
        public string OutputShape { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public bool HasGrouping { get; set; }
        public bool HasAggregation { get; set; }
        public bool UseDistinct { get; set; }
        public List<string> InvolvedEntities { get; set; } = new();
    }

    public class CopilotCatalogProjectionSnapshot
    {
        public string SqlExpression { get; set; } = string.Empty;
        public string OutputName { get; set; } = string.Empty;
        public string SourceEntity { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public class CopilotCatalogPredicateSnapshot
    {
        public string SqlFragment { get; set; } = string.Empty;
        public List<CopilotCatalogSqlParameterSnapshot> Parameters { get; set; } = new();
    }

    public class CopilotCatalogOrderSnapshot
    {
        public string SqlExpression { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
    }

    public class CopilotCatalogSqlParameterSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
        public string? ClrType { get; set; }
    }

    /// <summary>
    /// Catalog-driven reference token resolution outcome.
    /// Token is resolved against any catalog entity whose declared identifier semantics
    /// match — Ticket, Entity, User, Invoice, etc. — never via hardcoded entity names.
    /// </summary>
    public class CopilotEntityReferenceResolution
    {
        public string Token { get; set; } = string.Empty;
        public bool Resolved { get; set; }
        /// <summary>The catalog entity that the token resolved against (e.g. "Ticket", "Invoice", "User").</summary>
        public string? ResolvedEntity { get; set; }
        /// <summary>Primary key value of the resolved row (string-encoded so int/Guid/string keys all fit).</summary>
        public string? ResolvedId { get; set; }
        /// <summary>Human-friendly representation of the resolved row (the entity's identifier field value).</summary>
        public string? ResolvedDisplay { get; set; }
        /// <summary>Catalog field name the token matched against (e.g. "TicketNumber", "Email", "Name").</summary>
        public string? ResolvedField { get; set; }
        /// <summary>Every "EntityName.FieldName" that was tried, in order.</summary>
        public List<string> AttemptedFields { get; set; } = new();
        public string? Notes { get; set; }
    }
}
