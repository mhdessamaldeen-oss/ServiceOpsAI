namespace ServiceOpsAI.Models.AI
{
    /// <summary>
    /// Approved metadata contract for Admin Copilot data access.
    /// AI can use this catalog to understand available data, but execution must still validate every plan against it.
    /// </summary>
    public class CopilotDataCatalog
    {
        public string Version { get; set; } = "1.0";
        public List<string> AllowedOutputShapes { get; set; } = [.. CopilotDataCatalogSchema.DefaultOutputShapes];
        public List<CopilotOperationDefinition> AllowedOperations { get; set; } = new();
        public List<CopilotEntityDefinition> Entities { get; set; } = new();
        public List<CopilotTemporalLexiconEntry> TemporalLexicon { get; set; } = new();
        public CopilotParserGrammar ParserGrammar { get; set; } = new();
    }

    public class CopilotAgentGuardrailsDocument
    {
        public string Version { get; set; } = "1.0";
        public string RuleScope { get; set; } = string.Empty;
        public List<string> RequiredMetadataFiles { get; set; } = new();
        public List<CopilotDesignRule> DesignPrinciples { get; set; } = new();
        public CopilotAcceptanceCriteria AcceptanceCriteria { get; set; } = new();
    }

    public class CopilotDesignRule
    {
        public string Name { get; set; } = string.Empty;
        public string Rule { get; set; } = string.Empty;
        public List<string> AppliesTo { get; set; } = new();
        public List<string> EnforcedBy { get; set; } = new();
        public string Status { get; set; } = "Documented";
    }

    public class CopilotAcceptanceCriteria
    {
        public string Grade { get; set; } = string.Empty;
        public List<string> Requirements { get; set; } = new();
    }

    public enum CopilotAggregationType
    {
        Count,
        Sum,
        Avg,
        Min,
        Max,
        DistinctCount,
        Unknown
    }

    public enum CopilotTemporalType
    {
        DayOffset,
        StartOfWeek,
        LastWeek,
        StartOfMonth,
        LastMonth,
        LastXDays,
        NextXDays,
        DayXAgo,
        CustomRange,
        Unknown
    }

    public class CopilotParserGrammar
    {
        public string GroupingPattern { get; set; } = string.Empty;
        public string AggregationPattern { get; set; } = string.Empty;
        public Dictionary<string, List<string>> AggregationSynonyms { get; set; } = new();
        public string LimitPattern { get; set; } = string.Empty;
        public string WithinHoursPattern { get; set; } = string.Empty;
        public string MoreThanTicketsPattern { get; set; } = string.Empty;
        public string MoreThanCommentsPattern { get; set; } = string.Empty;
        public string MoreThanAttachmentsPattern { get; set; } = string.Empty;
        public string ResolvedVerbPattern { get; set; } = string.Empty;
        public string ClosedVerbPattern { get; set; } = string.Empty;
        public string UpdatedVerbPattern { get; set; } = string.Empty;
        public string DetailVerbPattern { get; set; } = string.Empty;
        public Dictionary<string, string> BooleanPatterns { get; set; } = new();
        public Dictionary<string, List<string>> SemanticSignals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CopilotKnownLookupValueDefinition> KnownLookupValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class CopilotKnownLookupValueDefinition
    {
        public string Entity { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class CopilotTemporalLexiconEntry
    {
        public string Phrase { get; set; } = string.Empty;
        public CopilotTemporalType Type { get; set; } = CopilotTemporalType.Unknown;
        public double Value { get; set; }
    }

    public class CopilotOperationDefinition
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    /// <summary>
    /// One approved business entity/table that the admin copilot may reason over.
    /// </summary>
    public class CopilotEntityDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Table { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SecurityScope { get; set; } = "AdminOnly";
        /// <summary>Maps to CopilotDataDataset enum by name. W-3: eliminates hardcoded switch.</summary>
        public string DatasetKey { get; set; } = string.Empty;
        /// <summary>Maps to CopilotAnalyticsViewKind enum by name. Eliminates ResolveViewKindAsync switch.</summary>
        public string ViewKind { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
        /// <summary>
        /// When true the executor uses CopilotCatalogSqlBuilder + CopilotRawQueryExecutor
        /// (fully catalog-driven raw SQL). When false (default) the existing EF Core
        /// typed path runs. This enables a safe per-entity rollout.
        /// </summary>
        public bool SupportsRawSql { get; set; } = false;
        /// <summary>Default column for ORDER BY when the plan has no explicit sort.</summary>
        public string DefaultOrderBy { get; set; } = string.Empty;
        /// <summary>"ASC" or "DESC" — used with DefaultOrderBy.</summary>
        public string DefaultOrderDirection { get; set; } = "DESC";
        public int DefaultLimit { get; set; } = 50;
        public int MaxLimit { get; set; } = 200;
        public List<string> AllowedOperations { get; set; } = new();
        public List<string> DefaultFields { get; set; } = new();
        public CopilotLookupEnrichmentDefinition? LookupEnrichment { get; set; }
        public List<string> Aliases { get; set; } = new();
        public List<CopilotFieldDefinition> Fields { get; set; } = new();
        public List<CopilotRelationshipDefinition> Relationships { get; set; } = new();
        public CopilotEntitySemanticsDefinition? Semantics { get; set; }
    }

    /// <summary>
    /// Explicit semantics to remove hardcoded string checks for core behaviors.
    /// </summary>
    public class CopilotEntitySemanticsDefinition
    {
        public string PrimaryDateField { get; set; } = string.Empty;
        public string ResolvedDateField { get; set; } = string.Empty;
        public string ClosedDateField { get; set; } = string.Empty;
        public string UpdatedDateField { get; set; } = string.Empty;
        public List<string> SecondaryDateFields { get; set; } = new();
        public string IdentifierField { get; set; } = string.Empty;
        /// <summary>
        /// Optional ordered list of catalog field names the entity reference resolver should try
        /// when matching a free-form token against this entity (e.g. ["TicketNumber","ExternalReference"]).
        /// When empty, the resolver falls back to <see cref="IdentifierField"/> and any field whose
        /// <see cref="CopilotFieldDefinition.SemanticRoles"/> declares a reference role.
        /// </summary>
        public List<string> LookupFields { get; set; } = new();
        public string StatusField { get; set; } = string.Empty;
        public string SoftDeleteField { get; set; } = string.Empty;
        public Dictionary<string, string> UserRoleRelationships { get; set; } = new();
    }

    /// <summary>
    /// Optional lookup metadata for entities whose values can be matched directly from user wording.
    /// This lets the planner enrich filters from the catalog instead of hardcoding entity-specific queries.
    /// </summary>
    public class CopilotLookupEnrichmentDefinition
    {
        public bool Enabled { get; set; } = true;
        public string ValueField { get; set; } = string.Empty;
        public string ActiveField { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new();
        public int MaxValues { get; set; } = 250;
    }

    /// <summary>
    /// One approved field with its query capabilities.
    /// These flags are the source of truth for filtering, sorting, grouping, aggregation, and display.
    /// </summary>
    public class CopilotFieldDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? SqlExpression { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsKey { get; set; }
        public bool IsNullable { get; set; }
        public bool IsSensitive { get; set; }
        public bool IsDefaultVisible { get; set; }
        public bool IsLookup { get; set; }
        public string SecurityLevel { get; set; } = "Admin";
        public List<string> Aliases { get; set; } = new();
        public List<string> Operators { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public List<string> Aggregations { get; set; } = new();
        public Dictionary<string, object> AllowedValues { get; set; } = new();
        /// <summary>
        /// Catalog-declared semantic roles for the field (e.g. "confidence-score", "attachment-count",
        /// "identifier", "resolution-duration"). Used by the planner to pick aggregation targets by role
        /// rather than by "first non-key numeric." Phase 2 of the 2026-04-30 fix plan.
        /// </summary>
        public List<string> SemanticRoles { get; set; } = new();
    }

    /// <summary>
    /// One approved relationship edge. Dynamic joins must follow these edges only.
    /// </summary>
    public class CopilotRelationshipDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string SourceField { get; set; } = string.Empty;
        public string TargetField { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Via { get; set; } = string.Empty;
        public string ViaSourceField { get; set; } = string.Empty;
        public string ViaTargetField { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsDefaultJoin { get; set; }
    }

    public class CopilotDataCatalogValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class CopilotDataJoinPath
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public List<CopilotRelationshipDefinition> Relationships { get; set; } = new();
    }
}
