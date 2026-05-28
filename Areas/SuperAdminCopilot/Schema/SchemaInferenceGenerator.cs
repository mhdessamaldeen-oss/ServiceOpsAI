namespace SuperAdminCopilot.Schema;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;

/// <summary>
/// Layer 2 of schema knowledge: emits a single auditable JSON file describing every table the
/// copilot can see — primary keys, columns (with types, nullability, per-column roles), foreign
/// keys outbound and inbound, plus inferred flags (bridge / lookup / person) and roles
/// (label / soft-delete / natural-key). Heuristics-only; the file is reviewable and overridable.
/// </summary>
public interface ISchemaInferenceGenerator
{
    SchemaInferenceResult Generate(IProgress<SchemaInferenceProgress>? progress = null);
    Task WriteAsync(string path, IProgress<SchemaInferenceProgress>? progress = null, CancellationToken cancellationToken = default);
}

// Output models (SchemaInferenceResult, InferredTable, etc.) live in SchemaInferenceModels.cs.

internal sealed class SchemaInferenceGenerator : ISchemaInferenceGenerator
{
    private readonly IEntityCatalog _catalog;
    private readonly IOptionsMonitor<FkRoleOptions> _fkRoleOptions;
    private readonly ILogger<SchemaInferenceGenerator> _logger;

    public SchemaInferenceGenerator(
        IEntityCatalog catalog,
        IOptionsMonitor<FkRoleOptions> fkRoleOptions,
        ILogger<SchemaInferenceGenerator> logger)
    {
        _catalog = catalog;
        _fkRoleOptions = fkRoleOptions;
        _logger = logger;
    }

    public SchemaInferenceResult Generate(IProgress<SchemaInferenceProgress>? progress = null)
    {
        var snapshot = _catalog.Snapshot;
        var total = snapshot.Tables.Count;
        var tables = new List<InferredTable>(total);
        progress?.Report(new SchemaInferenceProgress(total, 0, null));

        for (int i = 0; i < total; i++)
        {
            var table = snapshot.Tables[i];
            progress?.Report(new SchemaInferenceProgress(total, i, table.Name));
            var inferred = InferTable(table, snapshot);
            // Populate sample values for lookup tables so the LLM can use real status / priority /
            // category names as filter values rather than guessing. _catalog.GetSampleValues
            // queries the DB once per table and caches.
            if (inferred.Flags.IsLookup)
            {
                try
                {
                    var samples = _catalog.GetSampleValues(table.Name);
                    if (samples is { Count: > 0 })
                        inferred.SampleValues = samples.Take(10).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SchemaInferenceGenerator] sample-value fetch failed for {Table}.", table.Name);
                }
            }
            tables.Add(inferred);
        }
        progress?.Report(new SchemaInferenceProgress(total, total, null));

        var result = new SchemaInferenceResult
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            SchemaHash = ComputeSchemaHash(snapshot),
            Tables = tables,
        };

        _logger.LogInformation(
            "[SuperAdminCopilot] Schema inference: {Count} tables (bridge={Bridge}, lookup={Lookup}, person={Person})",
            result.Tables.Count,
            result.Tables.Count(t => t.Flags.IsBridge),
            result.Tables.Count(t => t.Flags.IsLookup),
            result.Tables.Count(t => t.Flags.IsPerson));

        return result;
    }

    public async Task WriteAsync(string path, IProgress<SchemaInferenceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = Generate(progress);
        var json = JsonSerializer.Serialize(result, SerializerOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        _logger.LogInformation("[SuperAdminCopilot] Schema inference written to {Path}", path);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Per-table inference ────────────────────────────────────────────────────────────

    private InferredTable InferTable(TableInfo table, SchemaSnapshot snapshot)
    {
        var columns = snapshot.ColumnsOf(table.Name).ToList();
        var fksOut = snapshot.ForeignKeys
            .Where(fk => string.Equals(fk.ParentTable, table.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var fksIn = snapshot.ForeignKeys
            .Where(fk => string.Equals(fk.ReferencedTable, table.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var keys = snapshot.KeyConstraints
            .Where(k => string.Equals(k.TableName, table.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pkColumns = keys
            .Where(k => k.ConstraintType.Equals("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.OrdinalPosition)
            .Select(k => k.ColumnName)
            .ToList();

        var pkSet = new HashSet<string>(pkColumns, StringComparer.OrdinalIgnoreCase);
        var fkColumnRefs = fksOut.ToDictionary(
            fk => fk.ParentColumn,
            fk => $"{fk.ReferencedTable}.{fk.ReferencedColumn}",
            StringComparer.OrdinalIgnoreCase);

        var softDeleteColumn = DetectSoftDeleteColumn(columns);
        var naturalKey = DetectNaturalKey(columns, keys, pkSet);
        var dateRoles = DetectDateRoles(columns);
        var piiSet = DetectPiiColumns(columns);
        // LabelColumn: prefer a display-friendly column (Name/Title/FullName/…); fall back
        // to the NaturalKey when nothing display-friendly exists. This catches transactional
        // entities like Payments / ServiceAccounts / ServicePoints / CallLogs whose only
        // user-visible identifier IS the natural key (PaymentReference, AccountNumber, …).
        var labelColumn = DetectLabelColumn(columns) ?? naturalKey;

        var enriched = columns.Select(c => BuildColumn(c, pkSet, fkColumnRefs, labelColumn, softDeleteColumn, naturalKey, dateRoles, piiSet)).ToList();

        return new InferredTable
        {
            Name = table.Name,
            Schema = table.Schema,
            PrimaryKey = pkColumns,
            Flags = new InferredFlags
            {
                IsBridge = IsBridgeTable(columns, fksOut, pkSet),
                IsLookup = IsLookupTable(columns, fksOut, fksIn, labelColumn),
                IsPerson = IsPersonTable(columns, fksIn),
            },
            Roles = new InferredRoles
            {
                LabelColumn = labelColumn,
                SoftDeleteColumn = softDeleteColumn,
                NaturalKey = naturalKey,
            },
            Columns = enriched,
            ForeignKeysOut = fksOut.Select(fk => new ForeignKeyRef(fk.ParentColumn, fk.ReferencedTable, fk.ReferencedColumn)).ToList(),
            ReferencedBy = fksIn.Select(fk => new ReferencedByRef(fk.ParentTable, fk.ParentColumn)).ToList(),
            // English synonyms: singular + plural of the table name, plus a small
            // utility-domain dictionary. Arabic comes from overrides.
            Synonyms = GenerateTableSynonyms(table.Name),
            Source = SpecConstants.InferenceSources.Heuristic,
        };
    }

    private InferredColumn BuildColumn(
        ColumnInfo c,
        HashSet<string> pkSet,
        Dictionary<string, string> fkColumnRefs,
        string? labelColumn,
        string? softDeleteColumn,
        string? naturalKey,
        Dictionary<string, string> dateRoles,
        HashSet<string> piiSet)
    {
        var role = pkSet.Contains(c.ColumnName) ? SpecConstants.ColumnRoles.PrimaryKey
            : fkColumnRefs.ContainsKey(c.ColumnName) ? SpecConstants.ColumnRoles.ForeignKey
            : string.Equals(c.ColumnName, naturalKey, StringComparison.OrdinalIgnoreCase) ? SpecConstants.ColumnRoles.NaturalKey
            : string.Equals(c.ColumnName, labelColumn, StringComparison.OrdinalIgnoreCase) ? SpecConstants.ColumnRoles.Label
            : string.Equals(c.ColumnName, softDeleteColumn, StringComparison.OrdinalIgnoreCase) ? SpecConstants.ColumnRoles.SoftDelete
            : dateRoles.ContainsKey(c.ColumnName) ? SpecConstants.ColumnRoles.Date
            : IsSystemColumn(c.ColumnName) ? SpecConstants.ColumnRoles.Audit
            : null;

        return new InferredColumn
        {
            Name = c.ColumnName,
            Type = FormatType(c.DataType, c.MaxLength),
            Nullable = c.IsNullable,
            Role = role,
            DateRole = dateRoles.TryGetValue(c.ColumnName, out var dr) ? dr : null,
            FkRole = role == SpecConstants.ColumnRoles.ForeignKey ? InferFkRole(c.ColumnName) : null,
            References = fkColumnRefs.TryGetValue(c.ColumnName, out var fr) ? fr : null,
            IsPii = piiSet.Contains(c.ColumnName),
            // Column synonyms: camelCase → space-separated form so the planner matches user phrasing
            // "account number" against `AccountNumber`. Skips PKs, FKs, audit cols, and bilingual
            // duplicates (NameAr is covered by overrides, not auto). Returns null when nothing useful.
            Synonyms = GenerateColumnSynonyms(c.ColumnName, role),
        };
    }

    // ── Synonym generation (English-only; Arabic comes from overrides) ───────────────

    /// <summary>Small domain dictionary mapping a canonical entity to alternate English forms.
    /// Keys are the entity SINGULAR (Customer, not Customers). Order in the value list
    /// is significant — most-common-first improves planner ranking.</summary>
    private static readonly Dictionary<string, string[]> TableSynonymDictionary = new(StringComparer.OrdinalIgnoreCase)
    {
        // People / parties
        ["Customer"]            = new[] { "client", "subscriber", "account holder", "consumer" },
        ["ApplicationUser"]     = new[] { "user", "agent", "staff", "employee" },
        ["Technician"]          = new[] { "field engineer", "engineer", "field worker", "fitter", "lineman" },
        // Money
        ["Bill"]                = new[] { "invoice", "charge", "statement" },
        ["Payment"]             = new[] { "transaction", "settlement", "remittance" },
        ["Tariff"]              = new[] { "price plan", "rate plan", "pricing" },
        ["TariffTier"]          = new[] { "price bracket", "rate bracket", "block", "slab" },
        ["Subsidy"]             = new[] { "discount", "rebate", "credit", "relief" },
        ["Currency"]            = new[] { "money", "fx" },
        ["PaymentMethod"]       = new[] { "payment channel", "payment mode" },
        // Service / contract
        ["ServiceAccount"]      = new[] { "contract", "subscription", "account" },
        ["ServicePoint"]        = new[] { "meter location", "premises", "site", "address" },
        ["CustomerSegment"]     = new[] { "segment", "customer class", "tariff class", "category" },
        ["ServiceType"]         = new[] { "service", "utility", "service kind" },
        // Operations
        ["Outage"]              = new[] { "blackout", "disconnection", "service disruption", "interruption" },
        ["WorkOrder"]           = new[] { "field job", "dispatch", "job ticket", "task" },
        ["Asset"]               = new[] { "equipment", "infrastructure", "facility", "asset" },
        ["MaintenanceSchedule"] = new[] { "planned maintenance", "maintenance window", "planned outage", "service window" },
        ["MeterReading"]        = new[] { "reading", "consumption record", "meter snapshot" },
        // Customer voice
        ["CallLog"]             = new[] { "contact", "call record", "interaction" },
        ["OutageNotification"]  = new[] { "alert", "sms notification", "outage alert" },
        ["SlaPolicy"]           = new[] { "sla", "service level", "response policy" },
        ["CsatResponse"]        = new[] { "csat", "satisfaction survey", "feedback", "rating" },
        // Tickets
        ["Ticket"]              = new[] { "issue", "case", "complaint", "report", "problem" },
        ["TicketComment"]       = new[] { "comment", "note", "remark" },
        // Geo
        ["Country"]             = new[] { "nation" },
        ["Region"]              = new[] { "district", "governorate", "area", "zone" },
        ["Department"]          = new[] { "team", "unit", "division" },
    };

    /// <summary>
    /// Produce an English synonym set for an entity. Strategy:
    ///   1. Canonical lowercase form of the table name → singular and plural (best-effort).
    ///   2. Splits CamelCase into a space-separated form (ServiceAccount → "service account").
    ///   3. Looks up the singular form in the domain dictionary and adds those entries.
    /// Returns the deduplicated lowercase list. Arabic synonyms are NOT generated here —
    /// they belong in schema-overrides.json where humans can add them after schema changes.
    /// </summary>
    private static List<string> GenerateTableSynonyms(string tableName)
    {
        if (string.IsNullOrEmpty(tableName)) return new List<string>();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var singular = Singularize(tableName);                   // Tickets → Ticket; Bills → Bill
        var plural = Pluralize(singular);                        // Ticket → Tickets

        // 1. canonical lower
        result.Add(singular.ToLowerInvariant());
        result.Add(plural.ToLowerInvariant());

        // 2. camelCase split (ServiceAccount → "service account") for both singular and plural
        var singularSplit = SplitCamelCase(singular).ToLowerInvariant();
        if (singularSplit != singular.ToLowerInvariant()) result.Add(singularSplit);
        var pluralSplit = SplitCamelCase(plural).ToLowerInvariant();
        if (pluralSplit != plural.ToLowerInvariant()) result.Add(pluralSplit);

        // 3. domain dictionary
        if (TableSynonymDictionary.TryGetValue(singular, out var domain))
        {
            foreach (var d in domain)
            {
                result.Add(d.ToLowerInvariant());
                // also add a plural form of multi-word domain entries when it's a single word
                if (!d.Contains(' ')) result.Add(Pluralize(d).ToLowerInvariant());
            }
        }

        // Skip listing the original singular twice in the output ordering — preserve
        // singular → plural → splits → domain.
        return result.ToList();
    }

    /// <summary>
    /// Per-column English synonyms. Today this is just a camelCase split with stop-word
    /// removal, surfaced only for columns the planner is likely to reference in a question
    /// (Label, NaturalKey, Date, or non-system text columns). PKs / FKs / audit cols return
    /// null so the synonym list stays meaningful.
    /// </summary>
    private static List<string>? GenerateColumnSynonyms(string columnName, string? role)
    {
        if (string.IsNullOrEmpty(columnName)) return null;
        if (role == SpecConstants.ColumnRoles.PrimaryKey
            || role == SpecConstants.ColumnRoles.ForeignKey
            || role == SpecConstants.ColumnRoles.Audit
            || role == SpecConstants.ColumnRoles.SoftDelete)
        {
            return null;
        }

        var split = SplitCamelCase(columnName).ToLowerInvariant();
        // The column name already exists as the canonical reference — only emit a synonym
        // when the split form differs (which is the user-friendlier phrasing).
        if (split == columnName.ToLowerInvariant()) return null;

        var result = new List<string> { split };
        // Common abbreviations: "number" → "no" / "#"
        if (split.EndsWith(" number"))
        {
            var stem = split[..^7];
            result.Add(stem + " no");
            result.Add(stem + " #");
        }
        return result;
    }

    /// <summary>Split CamelCase / PascalCase into space-separated lowercase words.
    /// "ServiceAccount" → "service account"; "WorkOrderId" → "work order id".</summary>
    private static string SplitCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (i > 0 && char.IsUpper(ch) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                sb.Append(' ');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // Irregular plurals + nouns that are unchanged in the plural. Checked before the regular
    // rules so "lineman" → "linemen" (not "linemans"), "fx" / "money" / "premises" stay as-is.
    private static readonly Dictionary<string, string> IrregularPlurals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lineman"]  = "linemen",
        ["man"]      = "men",
        ["woman"]    = "women",
        ["child"]    = "children",
        ["foot"]     = "feet",
        ["tooth"]    = "teeth",
        ["person"]   = "people",
        ["mouse"]    = "mice",
        ["datum"]    = "data",
    };

    // Words that are the SAME singular and plural (or where applying English plural rules
    // would produce something silly). Returning the input unchanged is correct.
    private static readonly HashSet<string> UncountableOrInvariant = new(StringComparer.OrdinalIgnoreCase)
    {
        "money", "fx", "premises", "series", "species", "fish", "sheep",
        "equipment", "infrastructure", "feedback", "information", "evidence",
        "software", "hardware", "data",
    };

    /// <summary>Best-effort pluralization. Handles irregulars + invariants first, then common
    /// rules (s, es, ies).</summary>
    private static string Pluralize(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        if (UncountableOrInvariant.Contains(word)) return word;
        if (IrregularPlurals.TryGetValue(word, out var irreg)) return irreg;
        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase)) return word;     // already plural
        if (word.EndsWith("y", StringComparison.OrdinalIgnoreCase) && word.Length > 1
            && !"aeiou".Contains(char.ToLowerInvariant(word[^2])))
            return word[..^1] + "ies";                                                // Currency → Currencies
        if (word.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("x",  StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("z",  StringComparison.OrdinalIgnoreCase))
            return word + "es";
        return word + "s";
    }

    /// <summary>Inverse of Pluralize — drops s/es/ies. Used to canonicalise table names.</summary>
    private static string Singularize(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        if (word.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && word.Length > 3)
            return word[..^3] + "y";                                                  // Currencies → Currency
        if (word.EndsWith("ses", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("ches", StringComparison.OrdinalIgnoreCase)
            || word.EndsWith("shes", StringComparison.OrdinalIgnoreCase))
            return word[..^2];                                                        // Boxes → Box
        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) && word.Length > 1
            && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return word[..^1];                                                        // Tickets → Ticket
        return word;
    }

    /// <summary>
    /// Infer the verb-role of a foreign-key column from its NAME, entity-agnostic.
    /// Walks the configured <see cref="FkRoleOptions"/> in order, returning the first role
    /// whose patterns match. Returns null when no pattern matches — the LLM is then free to
    /// pick by context.
    /// </summary>
    /// <remarks>
    /// <para>Order in the config matters: more-specific entries first (e.g. <c>AssigneeId</c>
    /// before <c>Owner</c>).</para>
    /// <para>Naming-convention agnostic: matches PascalCase (<c>CreatedByUserId</c>),
    /// camelCase (<c>createdByUserId</c>), snake_case (<c>created_by_user_id</c>) by
    /// normalising both the column name and the pattern (strip underscores + lower-case)
    /// before substring matching.</para>
    /// </remarks>
    private string? InferFkRole(string columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return null;
        var patterns = _fkRoleOptions.CurrentValue?.Patterns;
        if (patterns is not { Count: > 0 }) return null;
        var haystack = NormaliseForMatch(columnName);
        foreach (var role in patterns)
        {
            if (role.Patterns is null) continue;
            foreach (var p in role.Patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var needle = NormaliseForMatch(p);
                if (haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return role.Role;
            }
        }
        return null;
    }

    /// <summary>Normalise a column name or pattern for naming-convention-agnostic matching.
    /// Strips underscores so <c>created_by</c> matches <c>CreatedBy</c>; lower-cases so casing
    /// differences don't matter.</summary>
    private static string NormaliseForMatch(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '_' || c == '-' || c == ' ') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string FormatType(string dataType, int? maxLength)
    {
        if (!maxLength.HasValue) return dataType;
        return maxLength.Value switch
        {
            -1 => $"{dataType}(max)",
            > 0 => $"{dataType}({maxLength.Value})",
            _ => dataType,
        };
    }

    // ── Heuristic rules ───────────────────────────────────────────────────────────────

    // Ordered most-display-friendly first. Bilingual `*En` variants come AFTER plain
    // `Name`/`Title` so legacy tables that have BOTH (e.g. Departments has Name + NameEn)
    // keep their existing label choice for compat. `Description` falls late since it's
    // long-form prose, not a label. `Code` is last because the NaturalKey detector
    // already captures codes — using one as a label too would be a duplicate.
    private static readonly string[] LabelPriority =
    {
        // Generic English display names (with bilingual fallback)
        "Name", "NameEn",
        "Title", "TitleEn",
        "FullName", "FullNameEn",
        "DisplayName", "DisplayNameEn",
        "Label", "LabelEn",
        // Domain-specific names (Phase 06)
        "ProgramName", "ProgramNameEn",
        // Person FirstName as last-resort person label
        "FirstName",
        // Long-form prose (avoid unless nothing else)
        "Description",
        // Codes (NaturalKey covers the same role)
        "Code",
    };

    private static string? DetectLabelColumn(List<ColumnInfo> cols)
    {
        foreach (var candidate in LabelPriority)
        {
            var hit = cols.FirstOrDefault(c =>
                string.Equals(c.ColumnName, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.ColumnName;
        }
        return null;
    }

    private static readonly string[] SoftDeletePriority =
        { "IsDeleted", "DeletedAt", "IsArchived", "IsActive" };

    private static string? DetectSoftDeleteColumn(List<ColumnInfo> cols)
    {
        foreach (var candidate in SoftDeletePriority)
        {
            var hit = cols.FirstOrDefault(c =>
                string.Equals(c.ColumnName, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.ColumnName;
        }
        return null;
    }

    /// <summary>Single-column UNIQUE constraint on a non-PK string column = natural key
    /// (e.g. Tickets.TicketNumber).</summary>
    private static string? DetectNaturalKey(
        List<ColumnInfo> cols, List<KeyConstraintInfo> keys, HashSet<string> pkSet)
    {
        var uniqueGroups = keys
            .Where(k => k.ConstraintType.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
            .GroupBy(k => k.ConstraintName)
            .Where(g => g.Count() == 1);

        foreach (var g in uniqueGroups)
        {
            var col = g.First().ColumnName;
            if (pkSet.Contains(col)) continue;
            var info = cols.FirstOrDefault(c =>
                string.Equals(c.ColumnName, col, StringComparison.OrdinalIgnoreCase));
            if (info is null) continue;
            if (!IsStringType(info.DataType)) continue;
            return info.ColumnName;
        }
        return null;
    }

    private static readonly string[] PiiHints =
    {
        "Email", "Phone", "Mobile", "Password", "PasswordHash", "PasswordSalt",
        "Ssn", "NationalId", "PassportNumber", "CreditCard", "BirthDate", "DateOfBirth",
        "Address",
    };

    // Fix Bug #1: bit columns are flags, not PII payload. "EmailConfirmed" / "PhoneNumberConfirmed"
    // are flags, never personal data.
    private static HashSet<string> DetectPiiColumns(List<ColumnInfo> cols)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            if (c.DataType.Equals("bit", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var hint in PiiHints)
            {
                if (c.ColumnName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(c.ColumnName);
                    break;
                }
            }
        }
        return result;
    }

    private static readonly string[] DateTypes =
        { "datetime", "datetime2", "date", "datetimeoffset", "smalldatetime" };

    private static Dictionary<string, string> DetectDateRoles(List<ColumnInfo> cols)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            if (!DateTypes.Contains(c.DataType, StringComparer.OrdinalIgnoreCase)) continue;
            var role = DateRoleFor(c.ColumnName);
            if (role is not null) result[c.ColumnName] = role;
        }
        return result;
    }

    // Order matters: more-specific phrases first so e.g. "DeactivatedAt" matches
    // "deactivat" before the generic "activat". Each rule's substring is checked against
    // a lower-cased column name. The list is utility-domain enriched (Phase 06) — every
    // new `*At` column from the new Billing / Field-Ops / Customer Voice tables maps to
    // a role here without any per-table config.
    private static string? DateRoleFor(string columnName)
    {
        var lower = columnName.ToLowerInvariant();

        // — Lifecycle reversals (must precede their forward counterparts) —
        if (lower.Contains("deactivat") || lower.Contains("terminat") || lower.Contains("decommiss"))
            return lower.Contains("decommiss") ? SpecConstants.DateRoles.Decommissioned : SpecConstants.DateRoles.Deactivated;
        if (lower.Contains("churned")) return SpecConstants.DateRoles.Churned;

        // — Core lifecycle —
        if (lower.Contains("create") || lower.Contains("added") || lower.Contains("inserted")) return SpecConstants.DateRoles.Created;
        if (lower.Contains("update") || lower.Contains("modified") || lower.Contains("changed") || lower.Contains("edited")) return SpecConstants.DateRoles.Modified;
        if (lower.Contains("delete") || lower.Contains("removed")) return SpecConstants.DateRoles.Deleted;
        if (lower.Contains("complete") || lower.Contains("finished") || lower.Contains("closed")) return SpecConstants.DateRoles.Completed;
        if (lower.Contains("resolved")) return SpecConstants.DateRoles.Resolved;
        if (lower.Contains("approv")) return SpecConstants.DateRoles.Approved;
        if (lower.Contains("escalat")) return SpecConstants.DateRoles.Escalated;
        if (lower.Contains("respond")) return SpecConstants.DateRoles.Responded;

        // — Billing / contract —
        if (lower.Contains("paid") || lower.Contains("payment") || lower.Contains("settled")) return SpecConstants.DateRoles.Paid;
        if (lower.Contains("issued") || lower.Contains("invoiced")) return SpecConstants.DateRoles.Issued;
        if (lower.Contains("effective")) return SpecConstants.DateRoles.Effective;
        if (lower.Contains("activated") || lower.Contains("activatedat") || lower.Contains("signup") || lower.Contains("signed")) return lower.Contains("signup") || lower.Contains("signed") ? SpecConstants.DateRoles.Signup : SpecConstants.DateRoles.Activated;

        // — Field ops —
        if (lower.Contains("dispatch")) return SpecConstants.DateRoles.Dispatched;
        if (lower.Contains("arrived") || lower.Contains("onsite")) return SpecConstants.DateRoles.Arrived;
        if (lower.Contains("commiss")) return SpecConstants.DateRoles.Commissioned;
        if (lower.Contains("installed") || lower.Contains("install")) return SpecConstants.DateRoles.Installed;
        if (lower.Contains("hired")) return SpecConstants.DateRoles.Hired;

        // — Notifications —
        if (lower.Contains("sent")) return SpecConstants.DateRoles.Sent;
        if (lower.Contains("delivered")) return SpecConstants.DateRoles.Delivered;
        if (lower.EndsWith("readat") || lower == "read" || lower.Contains("readat")) return SpecConstants.DateRoles.Read;

        // — Generic schedule / start / due (must come last; broad matches) —
        if (lower.Contains("scheduled") || lower.Contains("planned")) return SpecConstants.DateRoles.Scheduled;
        if (lower.Contains("started") || lower.Contains("opened") || lower.Contains("begin")) return SpecConstants.DateRoles.Started;
        if (lower.Contains("due")) return SpecConstants.DateRoles.Due;

        return null;
    }

    // Fix Bug #2: bridge must have ONLY system / FK columns. A content / title / message column
    // means it's a domain table (e.g. TicketComments has Comment text → not a bridge).
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedAt", "CreatedOn", "CreatedAtUtc", "CreatedBy", "CreatedByUserId",
        "UpdatedAt", "UpdatedOn", "UpdatedBy", "UpdatedByUserId",
        "ModifiedAt", "ModifiedOn", "ModifiedBy",
        "DeletedAt", "DeletedBy", "DeletedOn",
        "RowVersion", "ConcurrencyStamp", "Timestamp",
    };

    private static bool IsSystemColumn(string columnName) => SystemColumns.Contains(columnName);

    private static bool IsBridgeTable(List<ColumnInfo> cols, List<ForeignKeyInfo> fksOut, HashSet<string> pkSet)
    {
        if (fksOut.Count != 2) return false;
        var fkCols = new HashSet<string>(fksOut.Select(fk => fk.ParentColumn), StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            if (fkCols.Contains(c.ColumnName)) continue;
            if (pkSet.Contains(c.ColumnName)) continue;
            if (IsSystemColumn(c.ColumnName)) continue;
            // Any other (content) column disqualifies the bridge classification.
            return false;
        }
        return true;
    }

    private static bool IsLookupTable(
        List<ColumnInfo> cols, List<ForeignKeyInfo> fksOut, List<ForeignKeyInfo> fksIn, string? labelColumn)
    {
        if (fksOut.Count > 0) return false;
        if (cols.Count > 6) return false;
        if (fksIn.Count == 0) return false;
        var hasId = cols.Any(c => string.Equals(c.ColumnName, "Id", StringComparison.OrdinalIgnoreCase));
        return hasId && labelColumn is not null;
    }

    private static readonly string[] PersonColumnHints =
        { "UserName", "Email", "FirstName", "LastName", "FullName" };

    private static bool IsPersonTable(List<ColumnInfo> cols, List<ForeignKeyInfo> fksIn)
    {
        var hasPersonColumns = cols.Any(c =>
            PersonColumnHints.Any(h => c.ColumnName.Contains(h, StringComparison.OrdinalIgnoreCase)));
        return hasPersonColumns && fksIn.Count >= 3;
    }

    private static bool IsStringType(string dataType) =>
        dataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("varchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("nchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("char", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("ntext", StringComparison.OrdinalIgnoreCase);

    private static string ComputeSchemaHash(SchemaSnapshot snapshot)
    {
        var sb = new StringBuilder(snapshot.Columns.Count * 80);
        foreach (var c in snapshot.Columns
            .OrderBy(c => c.TableSchema, StringComparer.Ordinal)
            .ThenBy(c => c.TableName, StringComparer.Ordinal)
            .ThenBy(c => c.OrdinalPosition))
        {
            sb.Append(c.TableSchema).Append('.').Append(c.TableName).Append('.')
              .Append(c.ColumnName).Append('|').Append(c.DataType).Append('|')
              .Append(c.IsNullable ? '1' : '0').Append(';');
        }
        foreach (var fk in snapshot.ForeignKeys
            .OrderBy(f => f.ParentSchema, StringComparer.Ordinal)
            .ThenBy(f => f.ParentTable, StringComparer.Ordinal)
            .ThenBy(f => f.ConstraintName, StringComparer.Ordinal))
        {
            sb.Append("FK:").Append(fk.ParentTable).Append('.').Append(fk.ParentColumn)
              .Append("->").Append(fk.ReferencedTable).Append('.').Append(fk.ReferencedColumn).Append(';');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}

