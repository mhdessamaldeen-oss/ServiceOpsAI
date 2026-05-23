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

        var labelColumn = DetectLabelColumn(columns);
        var softDeleteColumn = DetectSoftDeleteColumn(columns);
        var naturalKey = DetectNaturalKey(columns, keys, pkSet);
        var dateRoles = DetectDateRoles(columns);
        var piiSet = DetectPiiColumns(columns);

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
        };
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

    private static readonly string[] LabelPriority =
        { "Name", "Title", "DisplayName", "Description", "Label", "Code" };

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

    private static string? DateRoleFor(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        if (lower.Contains("create") || lower.Contains("added") || lower.Contains("inserted")) return SpecConstants.DateRoles.Created;
        if (lower.Contains("update") || lower.Contains("modified") || lower.Contains("changed")) return SpecConstants.DateRoles.Modified;
        if (lower.Contains("delete") || lower.Contains("removed")) return SpecConstants.DateRoles.Deleted;
        if (lower.Contains("complete") || lower.Contains("finished") || lower.Contains("closed")) return SpecConstants.DateRoles.Completed;
        if (lower.Contains("resolved")) return SpecConstants.DateRoles.Resolved;
        if (lower.Contains("started") || lower.Contains("opened")) return SpecConstants.DateRoles.Started;
        if (lower.Contains("scheduled") || lower.Contains("planned") || lower.Contains("due")) return SpecConstants.DateRoles.Due;
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

