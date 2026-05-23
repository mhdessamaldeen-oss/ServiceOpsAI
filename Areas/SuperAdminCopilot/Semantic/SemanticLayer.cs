namespace SuperAdminCopilot.Semantic;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

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

    public SemanticLayer(IOptions<CopilotOptions> options, ILogger<SemanticLayer> logger)
    {
        _config = new Lazy<SemanticLayerConfig>(() => Load(options.Value.SemanticLayerPath, logger));
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
}
