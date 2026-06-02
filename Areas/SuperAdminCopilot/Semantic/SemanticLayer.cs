namespace SuperAdminCopilot.Semantic;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

/// <summary>
/// Loads <c>semantic-layer.json</c> at first access, builds case-insensitive lookup indexes, and
/// answers questions from the compiler / planner / orchestrator. Lazy + singleton: cost is paid
/// once per process.
/// </summary>
internal sealed class SemanticLayer : ISemanticLayer
{
    private readonly Lazy<SemanticLayerConfig> _config;
    private readonly Lazy<Dictionary<string, EntityDefinition>> _entityByNameOrSynonym;
    private readonly Lazy<Dictionary<string, EntityDefinition>> _entityByTable;
    private readonly Lazy<Dictionary<string, MetricDefinition>> _metricByNameOrSynonym;
    private readonly Lazy<Dictionary<string, DimensionDefinition>> _dimensionByNameOrSynonym;

    /// <summary>Index: column-context (lowercased) → alias (lowercased) → canonical value.</summary>
    private readonly Lazy<Dictionary<string, Dictionary<string, string>>> _valueSynonymsByColumn;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public SemanticLayer(
        IOptions<CopilotOptions> options,
        ISchemaKnowledge schemaKnowledge,
        ILogger<SemanticLayer> logger)
    {
        // Two-source load: the hand-curated `semantic-layer.json` is the canonical source
        // (rich Arabic synonyms, custom metrics, naturalKey regex). For any table NOT covered
        // there, we synthesize a baseline EntityDefinition from the auto-inferred schema
        // knowledge — so adding a new table never requires touching semantic-layer.json
        // unless the heuristic guess is wrong. Hand-curated entries always win; synthesized
        // entries fill the gap.
        _config = new Lazy<SemanticLayerConfig>(() =>
        {
            var loaded = Load(options.Value.SemanticLayerPath, logger);
            var synthesized = SynthesizeMissingEntities(loaded.Entities, schemaKnowledge, logger);
            if (synthesized.Count > 0)
            {
                loaded.Entities.AddRange(synthesized);
                logger.LogInformation(
                    "[SemanticLayer] synthesized {Count} entity definitions from SchemaKnowledge " +
                    "(tables not present in semantic-layer.json): {Names}",
                    synthesized.Count, string.Join(", ", synthesized.Select(e => e.Name)));

                // Round-3 diagnostic — log the first 6 synonyms per synthesized entity so a
                // bad merge or missing Arabic forms is obvious from the startup log alone.
                foreach (var e in synthesized)
                {
                    var totalSyn = e.Synonyms?.Count ?? 0;
                    var preview = totalSyn > 0
                        ? string.Join(" | ", e.Synonyms!.Take(20))
                        : "(none)";
                    logger.LogInformation("  • {Entity} ({Total} syn): search=[{Search}] syn=[{Syn}]",
                        e.Name, totalSyn,
                        e.SearchableColumns is { Count: > 0 } ? string.Join(", ", e.SearchableColumns) : "(none)",
                        preview);
                }
            }
            return loaded;
        });
        _entityByNameOrSynonym = new Lazy<Dictionary<string, EntityDefinition>>(() =>
        {
            var d = new Dictionary<string, EntityDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _config.Value.Entities)
            {
                if (!string.IsNullOrEmpty(e.Name)) d[e.Name] = e;
                if (!string.IsNullOrEmpty(e.Table)) d[e.Table] = e;
                foreach (var s in e.Synonyms) if (!string.IsNullOrEmpty(s)) d[s] = e;
            }
            return d;
        });
        _entityByTable = new Lazy<Dictionary<string, EntityDefinition>>(() =>
        {
            var d = new Dictionary<string, EntityDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _config.Value.Entities)
                if (!string.IsNullOrEmpty(e.Table)) d[e.Table] = e;
            return d;
        });
        _metricByNameOrSynonym = new Lazy<Dictionary<string, MetricDefinition>>(() =>
        {
            var d = new Dictionary<string, MetricDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _config.Value.Metrics)
            {
                if (!string.IsNullOrEmpty(m.Name)) d[m.Name] = m;
                foreach (var s in m.Synonyms) if (!string.IsNullOrEmpty(s)) d[s] = m;
            }
            return d;
        });
        _dimensionByNameOrSynonym = new Lazy<Dictionary<string, DimensionDefinition>>(() =>
        {
            var d = new Dictionary<string, DimensionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var dim in _config.Value.Dimensions)
            {
                if (!string.IsNullOrEmpty(dim.Name)) d[dim.Name] = dim;
                foreach (var s in dim.Synonyms) if (!string.IsNullOrEmpty(s)) d[s] = dim;
            }
            return d;
        });
        _valueSynonymsByColumn = new Lazy<Dictionary<string, Dictionary<string, string>>>(() =>
        {
            var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _config.Value.Synonyms)
            {
                if (!string.Equals(s.Type, "value", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(s.Context) || string.IsNullOrEmpty(s.Canonical)) continue;
                if (!d.TryGetValue(s.Context, out var inner))
                {
                    inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    d[s.Context] = inner;
                }
                foreach (var alias in s.Aliases)
                    if (!string.IsNullOrEmpty(alias)) inner[alias] = s.Canonical;
            }
            return d;
        });
    }

    public SemanticLayerConfig Config => _config.Value;

    public EntityDefinition? GetEntityByNameOrSynonym(string nameOrSynonym) =>
        string.IsNullOrEmpty(nameOrSynonym) ? null
            : _entityByNameOrSynonym.Value.TryGetValue(nameOrSynonym, out var e) ? e : null;

    public EntityDefinition? GetEntityForTable(string table) =>
        string.IsNullOrEmpty(table) ? null
            : _entityByTable.Value.TryGetValue(table, out var e) ? e : null;

    public MetricDefinition? GetMetric(string nameOrSynonym) =>
        string.IsNullOrEmpty(nameOrSynonym) ? null
            : _metricByNameOrSynonym.Value.TryGetValue(StripPrefix(nameOrSynonym, "metric:"), out var m) ? m : null;

    public DimensionDefinition? GetDimension(string nameOrSynonym) =>
        string.IsNullOrEmpty(nameOrSynonym) ? null
            : _dimensionByNameOrSynonym.Value.TryGetValue(StripPrefix(nameOrSynonym, "dimension:"), out var d) ? d : null;

    public (string Column, object? Value)? GetSoftDeleteFilter(string table)
    {
        var e = GetEntityForTable(table);
        if (e is null || string.IsNullOrEmpty(e.SoftDeleteColumn)) return null;
        return ($"{e.Table}.{e.SoftDeleteColumn}", e.SoftDeleteFilterValue);
    }

    public string ResolveSynonymValue(string column, string value)
    {
        if (string.IsNullOrEmpty(column) || string.IsNullOrEmpty(value)) return value;
        if (!_valueSynonymsByColumn.Value.TryGetValue(column, out var inner)) return value;
        return inner.TryGetValue(value, out var canonical) ? canonical : value;
    }

    /// <summary>Empty set returned when no sensitive columns are declared for a table.</summary>
    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsSensitiveColumn(string table, string column)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) return false;
        var map = SensitiveMap;
        return map.TryGetValue(table, out var set) && set.Contains(column);
    }

    public IReadOnlySet<string> GetSensitiveColumns(string table)
    {
        if (string.IsNullOrEmpty(table)) return EmptyStringSet;
        return SensitiveMap.TryGetValue(table, out var set) ? set : EmptyStringSet;
    }

    public string? GetDateColumn(string table, string? role)
    {
        if (string.IsNullOrEmpty(table)) return null;
        var e = GetEntityForTable(table);
        if (e is null) return null;

        // Explicit role match wins (e.g. "closed" → "ClosedAt"). Roles dictionary is
        // case-insensitive — the constructor in SemanticModels guarantees it.
        if (!string.IsNullOrEmpty(role)
            && e.DateRoles is { Count: > 0 }
            && e.DateRoles.TryGetValue(role!, out var roleCol)
            && !string.IsNullOrEmpty(roleCol))
        {
            return roleCol;
        }

        // "default" role mapping is the answer for any unmapped verb.
        if (e.DateRoles is { Count: > 0 }
            && e.DateRoles.TryGetValue("default", out var def)
            && !string.IsNullOrEmpty(def))
        {
            return def;
        }

        // No config-driven default — fall back to the historical convention so legacy
        // semantic layers (no DateRoles set) keep working unchanged.
        return "CreatedAt";
    }

    /// <summary>Build (lazy, on-demand) the sensitive-column lookup. Reads every entity's
    /// SensitiveColumns list. Stripped of empty strings and folded case-insensitive.</summary>
    private Dictionary<string, HashSet<string>> SensitiveMap
    {
        get
        {
            if (_sensitiveMap is not null) return _sensitiveMap;
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _config.Value.Entities)
            {
                if (string.IsNullOrEmpty(e.Table)) continue;
                if (e.SensitiveColumns is not { Count: > 0 }) continue;
                if (!map.TryGetValue(e.Table, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[e.Table] = set;
                }
                foreach (var col in e.SensitiveColumns)
                    if (!string.IsNullOrEmpty(col)) set.Add(col);
            }
            _sensitiveMap = map;
            return map;
        }
    }
    private Dictionary<string, HashSet<string>>? _sensitiveMap;

    public string BuildPromptSummary()
    {
        var cfg = _config.Value;
        if (cfg.Entities.Count == 0 && cfg.Metrics.Count == 0
            && cfg.Dimensions.Count == 0 && cfg.Synonyms.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("SEMANTIC LAYER (canonical vocabulary — prefer these over invented names)");

        if (cfg.Entities.Count > 0)
        {
            sb.AppendLine("Entities:");
            foreach (var e in cfg.Entities)
            {
                sb.Append("  - ").Append(e.Name).Append(" → table ").Append(e.Table);
                if (e.Synonyms.Count > 0) sb.Append("  (aliases: ").Append(string.Join(", ", e.Synonyms)).Append(')');
                sb.AppendLine();
                // Expose natural-key + label + date-role mappings inline under each entity so
                // the planner can pick the right column without inventing one. Indented two
                // levels so the LLM sees them as sub-facts of the entity, not separate rules.
                if (!string.IsNullOrEmpty(e.NaturalKeyColumn))
                {
                    sb.Append("      natural key: ").Append(e.Table).Append('.').Append(e.NaturalKeyColumn);
                    if (!string.IsNullOrEmpty(e.NaturalKeyFormat))
                        sb.Append("  (format: ").Append(e.NaturalKeyFormat).Append(')');
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(e.LabelColumn))
                    sb.Append("      label column: ").Append(e.Table).Append('.').Append(e.LabelColumn).AppendLine();
                if (e.DisplayColumns is { Count: > 0 })
                    sb.Append("      default display columns: ").AppendLine(string.Join(", ", e.DisplayColumns));
                if (e.DateRoles is { Count: > 0 })
                {
                    sb.Append("      date roles: ");
                    sb.AppendLine(string.Join(", ", e.DateRoles.Select(kv => $"{kv.Key}→{kv.Value}")));
                }
            }
        }

        if (cfg.Metrics.Count > 0)
        {
            sb.AppendLine("Metrics (use \"metric:<name>\" as an aggregation function to invoke):");
            foreach (var m in cfg.Metrics)
            {
                sb.Append("  - ").Append(m.Name).Append(" = ").Append(m.Expression);
                if (m.Filters.Count > 0)
                {
                    sb.Append(" WHERE ");
                    sb.Append(string.Join(" AND ", m.Filters.Select(f => $"{f.Column} {f.Op} {f.Value}")));
                }
                if (m.Synonyms.Count > 0) sb.Append("  (aliases: ").Append(string.Join(", ", m.Synonyms)).Append(')');
                sb.AppendLine();
            }
        }

        if (cfg.Dimensions.Count > 0)
        {
            sb.AppendLine("Dimensions (use \"dimension:<name>\" as a select/groupBy entry):");
            foreach (var d in cfg.Dimensions)
            {
                sb.Append("  - ").Append(d.Name).Append(" = ");
                sb.Append(d.Expression ?? d.Column ?? "");
                if (d.Synonyms.Count > 0) sb.Append("  (aliases: ").Append(string.Join(", ", d.Synonyms)).Append(')');
                sb.AppendLine();
            }
        }

        if (cfg.Synonyms.Count > 0)
        {
            sb.AppendLine("Value synonyms (when the user uses these words in a filter, use the canonical):");
            foreach (var s in cfg.Synonyms.Where(s => s.Type == "value"))
            {
                sb.Append("  - ").Append(string.Join(", ", s.Aliases))
                  .Append(" → ").Append(s.Canonical);
                if (!string.IsNullOrEmpty(s.Context)) sb.Append(" (filter on ").Append(s.Context).Append(')');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string StripPrefix(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s[prefix.Length..] : s;

    private static SemanticLayerConfig Load(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogWarning("[SuperAdminCopilot] Semantic-layer config not found at {Path}; running with empty layer.", path);
            return new SemanticLayerConfig();
        }
        try
        {
            using var stream = File.OpenRead(path);
            var cfg = JsonSerializer.Deserialize<SemanticLayerConfig>(stream, JsonOpts) ?? new SemanticLayerConfig();
            logger.LogInformation(
                "[SuperAdminCopilot] Semantic layer loaded: {Entities} entities, {Metrics} metrics, {Dimensions} dimensions, {Synonyms} synonym groups.",
                cfg.Entities.Count, cfg.Metrics.Count, cfg.Dimensions.Count, cfg.Synonyms.Count);
            return cfg;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SuperAdminCopilot] Failed to load semantic-layer config from {Path}; running with empty layer.", path);
            return new SemanticLayerConfig();
        }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    // ── Synthesis: auto-fill EntityDefinitions from SchemaKnowledge ───────────────────
    //
    // For every table the schema-inference pipeline has produced (but which has no entry
    // in semantic-layer.json), build a baseline EntityDefinition. Hand-curated entries in
    // semantic-layer.json always win — we only fill the GAP. This is the no-config path
    // for new tables: add a table, run the inference, and the planner can answer basic
    // questions about it without any hand-edit.

    private static List<EntityDefinition> SynthesizeMissingEntities(
        List<EntityDefinition> manualEntities,
        ISchemaKnowledge schema,
        ILogger logger)
    {
        if (!schema.IsAvailable) return new List<EntityDefinition>();

        // Tables already covered by manual entries — match on `Table` (the DB table name)
        // case-insensitively. Empty Table values are skipped.
        var coveredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manualEntities)
        {
            if (!string.IsNullOrEmpty(e.Table)) coveredTables.Add(e.Table);
        }

        var result = new List<EntityDefinition>();
        foreach (var t in schema.AllTables)
        {
            if (string.IsNullOrEmpty(t.Name)) continue;
            if (coveredTables.Contains(t.Name)) continue;

            // Skip Identity-framework / framework-internal tables and bridge tables — they're
            // not user-facing entities. Bridge tables can be added explicitly to overrides if
            // anyone wants to query them by name.
            if (IsFrameworkTable(t.Name)) continue;
            if (t.Flags.IsBridge) continue;

            result.Add(SynthesizeFrom(t));
        }
        return result;
    }

    // Identity tables, EF history, and the copilot's own infrastructure tables are skipped.
    // The planner shouldn't surface them when the user asks "show me X".
    private static readonly string[] FrameworkTablePrefixes =
        { "AspNet", "__EF", "Copilot", "TicketAiAnalysis", "TicketSemanticEmbedding", "RetrievalBenchmark", "ModelPricing", "GeminiApi", "GroqApi", "EmbeddingProgress" };

    private static bool IsFrameworkTable(string name)
    {
        foreach (var pfx in FrameworkTablePrefixes)
            if (name.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static EntityDefinition SynthesizeFrom(InferredTable t)
    {
        var dateRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? defaultDateColumn = null;
        foreach (var c in t.Columns)
        {
            if (string.IsNullOrEmpty(c.DateRole)) continue;
            // First match per role wins (covers ScheduledStart/ScheduledEnd → both "scheduled":
            // first one becomes the canonical answer for the role).
            if (!dateRoles.ContainsKey(c.DateRole)) dateRoles[c.DateRole] = c.Name;
            // Default = first chronological column, prefer `created` if present.
            if (defaultDateColumn is null
                || string.Equals(c.DateRole, SuperAdminCopilot.Models.SpecConstants.DateRoles.Created, StringComparison.OrdinalIgnoreCase))
            {
                defaultDateColumn = c.Name;
            }
        }
        if (defaultDateColumn is not null && !dateRoles.ContainsKey("default"))
            dateRoles["default"] = defaultDateColumn;

        // SearchableColumns: pick PROSE columns (Description / Title / Notes / Summary /
        // Comment / Content / Message / Body / Detail), bilingual variants included. Round-1
        // heuristic was "first 4 text columns" which mistakenly picked enum-string columns
        // (Status / Priority / OrderType) that appear earlier in the table — making
        // "WorkOrders about transformer" LIKE-search status columns and always return zero.
        var searchable = PickSearchableColumns(t);

        // SoftDeleteFilterValue: when the soft-delete column is "IsActive" the keep-row value
        // is TRUE (active rows); for "IsDeleted" / "IsArchived" it's FALSE (not-deleted rows);
        // for "DeletedAt" it's NULL. Conventional heuristic; users can override.
        object? softDeleteFilterValue = null;
        var sdc = t.Roles.SoftDeleteColumn;
        if (!string.IsNullOrEmpty(sdc))
        {
            softDeleteFilterValue = sdc.Equals("IsActive", StringComparison.OrdinalIgnoreCase) ? (object)true
                : sdc.EndsWith("At", StringComparison.OrdinalIgnoreCase) ? null
                : (object)false;
        }

        // DisplayColumns — analyst's default projection set. Synthesised heuristically so the
        // EnsureDisplayColumnsPhase has something to expand for auto-discovered entities
        // (WorkOrders, Payments, ServiceAccounts, etc.). Order: Id → NaturalKey → LabelColumn →
        // first dated column (CreatedAt / IssuedAt / StartedAt). Skipped when those slots are
        // unfilled. Manual semantic-layer.json entries continue to override.
        var displayCols = new List<string>();
        if (t.Columns.Any(c => string.Equals(c.Name, "Id", System.StringComparison.OrdinalIgnoreCase)))
            displayCols.Add("Id");
        if (!string.IsNullOrEmpty(t.Roles.NaturalKey) && !displayCols.Contains(t.Roles.NaturalKey, StringComparer.OrdinalIgnoreCase))
            displayCols.Add(t.Roles.NaturalKey);
        if (!string.IsNullOrEmpty(t.Roles.LabelColumn) && !displayCols.Contains(t.Roles.LabelColumn, StringComparer.OrdinalIgnoreCase))
            displayCols.Add(t.Roles.LabelColumn);
        // Add common bilingual title columns if present.
        foreach (var titleVariant in new[] { "TitleEn", "Title", "NameEn", "Name" })
        {
            if (displayCols.Contains(titleVariant, StringComparer.OrdinalIgnoreCase)) continue;
            if (t.Columns.Any(c => string.Equals(c.Name, titleVariant, System.StringComparison.OrdinalIgnoreCase)))
            {
                displayCols.Add(titleVariant);
                break;
            }
        }
        // Add a Status / State column if present — analysts always want lifecycle state.
        foreach (var stateVariant in new[] { "Status", "State", "Severity" })
        {
            if (displayCols.Contains(stateVariant, StringComparer.OrdinalIgnoreCase)) continue;
            if (t.Columns.Any(c => string.Equals(c.Name, stateVariant, System.StringComparison.OrdinalIgnoreCase)))
            {
                displayCols.Add(stateVariant);
                break;
            }
        }
        // Add the first date column (created/issued/started) so timeline ordering is visible.
        if (!string.IsNullOrEmpty(defaultDateColumn) && !displayCols.Contains(defaultDateColumn, StringComparer.OrdinalIgnoreCase))
            displayCols.Add(defaultDateColumn);

        return new EntityDefinition
        {
            Name = t.Name,
            Table = t.Name,
            Synonyms = new List<string>(t.Synonyms),
            SoftDeleteColumn = t.Roles.SoftDeleteColumn,
            SoftDeleteFilterValue = softDeleteFilterValue,
            IsLookup = t.Flags.IsLookup,
            SearchableColumns = searchable,
            NaturalKeyColumn = t.Roles.NaturalKey,
            LabelColumn = t.Roles.LabelColumn,
            DisplayColumns = displayCols,
            DateRoles = dateRoles,
            Description = $"Auto-synthesized from schema-inferred.json. " +
                          "Add a manual entry in semantic-layer.json (Arabic synonyms, custom metrics) to override.",
        };
    }

    // Cross-engine text-type recognition (SQL Server + PostgreSQL + MySQL + SQLite) so searchable-
    // column detection works on ANY target database, not just T-SQL. SQL Server's own type names
    // (nvarchar/varchar/nchar/char/text/ntext) still match exactly — behavior is unchanged there;
    // the extra names only add coverage on other engines (no SQL Server type name collides with them).
    // internal for the cross-engine unit test.
    internal static bool IsTextType(string sqlType)
    {
        if (string.IsNullOrEmpty(sqlType)) return false;
        var t = sqlType.ToLowerInvariant();
        return t.StartsWith("nvarchar") || t.StartsWith("varchar")
            || t.StartsWith("nchar") || t.StartsWith("char")
            || t.StartsWith("text") || t.StartsWith("ntext")
            || t.StartsWith("character")      // PostgreSQL: "character varying", "character"
            || t.StartsWith("citext")         // PostgreSQL case-insensitive text
            || t.StartsWith("clob")           // SQLite / Oracle
            || t.StartsWith("longtext") || t.StartsWith("mediumtext") || t.StartsWith("tinytext"); // MySQL
    }

    // Names (substring, case-insensitive) that signal a column holds user-facing prose worth
    // searching. Order matters loosely — earlier entries are preferred when multiple match.
    // Bilingual suffixes (En/Ar) handled automatically by substring matching.
    private static readonly string[] ProseColumnHints =
    {
        "description",       // bills.Description, outages.Description, etc.
        "title",             // workorders.TitleEn, maintenanceSchedules.TitleEn
        "notes",             // most tables — Notes / Note
        "summary",           // tickets.ResolutionSummary, calllogs.Summary
        "comment",           // ticketcomments.Content via Comment
        "content",           // ticketcomments.Content
        "message",           // outagenotifications.MessageEn / MessageAr
        "body",
        "detail",
        "address",           // servicepoints.AddressLineEn / AddressLineAr
        "name",              // customers.FullNameEn, technicians.FullNameEn — broad search target
        "rootcause",         // tickets.RootCause
        "verification",      // tickets.VerificationNotes
        "assessment",        // tickets.TechnicalAssessment
        "specification",     // assets.Specification
    };

    // Names (substring, case-insensitive) that DISQUALIFY a column from search even if it's
    // a string type. Enum stored as strings, surrogate identifiers, etc. — these never carry
    // free-text content users would search by.
    private static readonly string[] NonSearchableHints =
    {
        "status", "priority", "severity", "type", "kind", "category", "channel",
        "direction", "outcome", "specialty", "stamp", "hash", "isocode", "code",
        "unit", "method", "currency",
        "phone", "email",                  // PII; planner shouldn't surface
        "url", "uri", "endpoint",
        "id",                              // catches *Id FK columns plus a few stragglers
    };

    /// <summary>
    /// Pick up to 4 columns suitable for cross-column LIKE text-search. Priority:
    ///   1. Columns whose name matches a <see cref="ProseColumnHints"/> entry (Description, Title*, Notes, …).
    ///   2. Any remaining text column NOT in <see cref="NonSearchableHints"/> (Status / Priority / Email / …).
    ///   3. Fallback (only if 1 + 2 produce nothing): first 4 text columns regardless.
    /// PII columns are always excluded.
    /// </summary>
    private static List<string> PickSearchableColumns(InferredTable t)
    {
        // Text-typed, non-PII, not the natural key (which we already query separately), not a
        // date / FK / audit column.
        bool eligible(InferredColumn c) =>
            IsTextType(c.Type)
            && !c.IsPii
            && (c.Role is null || c.Role == SuperAdminCopilot.Models.SpecConstants.ColumnRoles.Label)
            && string.IsNullOrEmpty(c.DateRole);

        bool nameMatches(string columnName, string[] hints)
        {
            foreach (var h in hints)
                if (columnName.Contains(h, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        var allEligible = t.Columns.Where(eligible).ToList();

        // 1. Prose-named columns first.
        var prose = allEligible
            .Where(c => nameMatches(c.Name, ProseColumnHints))
            .Where(c => !nameMatches(c.Name, NonSearchableHints))
            .OrderBy(c => Array.FindIndex(ProseColumnHints,
                h => c.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))  // preserve hint order
            .Select(c => c.Name)
            .ToList();

        // 2. Any other text column that's clearly not enum-shaped.
        if (prose.Count < 4)
        {
            var extras = allEligible
                .Where(c => !nameMatches(c.Name, ProseColumnHints))
                .Where(c => !nameMatches(c.Name, NonSearchableHints))
                .Select(c => c.Name);
            foreach (var x in extras)
            {
                if (prose.Count >= 4) break;
                if (!prose.Contains(x, StringComparer.OrdinalIgnoreCase)) prose.Add(x);
            }
        }

        // 3. Last-resort fallback — entity has no obvious text content; fall back to the
        // original "first N text columns" so the search at least RUNS. Better than refusing.
        if (prose.Count == 0)
        {
            return allEligible.Select(c => c.Name).Take(4).ToList();
        }
        return prose.Take(4).ToList();
    }
}
