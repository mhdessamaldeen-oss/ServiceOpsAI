namespace AnalystAgent.Pipeline.Stages;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Models;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// Deterministic handler for metadata-intent questions. Recognizes a small set of patterns
/// (list tables / list columns / find FK relationships / find table by name) and emits real
/// INFORMATION_SCHEMA / sys.foreign_keys SQL that the regular executor runs. Bypasses the
/// LlmPlanner + SqlCompiler entirely — these queries are deterministic and safe.
///
/// <para><b>Policy gate (security):</b> the underlying SqlAstValidator refuses
/// INFORMATION_SCHEMA on user-generated SQL, so without a policy gate this handler is a recon
/// back door. Two AnalystOptions switches close it:
///   • <c>EnableSchemaIntrospection</c> — master kill switch; when false the handler refuses
///     every pattern.
///   • <c>RestrictMetadataToConfiguredEntities</c> — when true, only tables registered in the
///     semantic layer are revealed. Hides audit logs, migration tables, ASP.NET Identity
///     internals, etc.
/// Column-listing results are ALWAYS filtered through <see cref="ISemanticLayer.IsSensitiveColumn"/>
/// so PasswordHash / SecurityStamp / ConnectionString never appear in the answer regardless of
/// the two switches above.</para>
/// </summary>
public interface IMetadataHandler
{
    /// <summary>
    /// Try to handle the question deterministically. Returns null if no pattern matches or the
    /// metadata policy is disabled — caller falls through to the next stage.
    /// On match, returns the SQL string + executes it via the host executor and returns the rows
    /// (post-filtered against the sensitive-column allowlist).
    /// </summary>
    Task<MetadataHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default);
}

public sealed record MetadataHandlerResult(string Sql, ExecutionResult Result);

internal sealed class DeterministicMetadataHandler : IMetadataHandler
{
    private readonly IExecutor _executor;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IAnalystSchemaAccessPolicy _schemaPolicy;
    private readonly AnalystOptions _options;

    public DeterministicMetadataHandler(
        IExecutor executor,
        ISemanticLayer semanticLayer,
        IAnalystSchemaAccessPolicy schemaPolicy,
        IOptions<AnalystOptions> options)
    {
        _executor = executor;
        _semanticLayer = semanticLayer;
        _schemaPolicy = schemaPolicy;
        _options = options.Value;
    }

    public string Name => "Metadata";

    /// <summary>
    /// Matches all the natural ways to ask about a table's columns:
    ///   "columns in/of/for [the] X"            — original phrasing
    ///   "columns are in [the] X [table]"        — "what columns are in Tickets?"
    ///   "columns does [the] X [table] have"     — "what columns does Tickets have?"
    ///   "X has what columns" / "X column list"  — keyword-driven
    /// All forms capture the table name into &lt;table&gt;.
    /// </summary>
    private static readonly Regex ColumnsInTable = new(
        @"(?:" +
        @"(?:column|columns|fields)\s+(?:are\s+)?(?:available\s+)?(?:in|of|for|on)\s+(?:the\s+)?[`'""]?(?<table>\w+)[`'""]?" +
        @"|" +
        @"(?:column|columns|fields)\s+(?:does|do)\s+(?:the\s+)?[`'""]?(?<table>\w+)[`'""]?\s+(?:table\s+)?(?:have|contain)" +
        @"|" +
        @"(?:list|show|describe)\s+(?:the\s+)?(?:column|columns|fields|schema)\s+(?:of|for|in)?\s*(?:the\s+)?[`'""]?(?<table>\w+)[`'""]?" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataTypeOf = new(
        @"(?:data\s+type|type)\s+of\s+(?:the\s+)?[`'""]?(?<table>\w+)[`'""]?\.?[`'""]?(?<col>\w+)?[`'""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Relationship = new(
        @"(?:relat(?:ed|ionship|ion)|link|connect)\b.*\b(?<a>\w+)\b.*\b(?:and|to|with)\b.*\b(?<b>\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"How are X and Y related?" / "X and Y connection" — relation word at the END.</summary>
    private static readonly Regex RelationshipReverse = new(
        @"\b(?<a>\w+)\b\s+(?:and|with|to)\s+\b(?<b>\w+)\b\s+(?:relat(?:ed|ionship|ion)|connect(?:ed|ion)?|link(?:ed)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhichTable = new(
        @"(?:which\s+table|what\s+table|table\s+(?:that\s+)?holds?|table\s+for)\b.*\b(?<keyword>\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SearchTablesByName = new(
        @"(?:" +
        @"(?:list|find|show|every)\s+(?:every\s+)?tables?\s+.*\b(?:contains?|with|named|matching|like)\s+[`'""]?(?<keyword>\w+)[`'""]?" +
        @"|" +
        @"(?:list|find|show)\s+(?:every\s+|all\s+)?tables?\s+that\s+(?:has|have|contain|contains)\s+[`'""]?(?<keyword>\w+)[`'""]?\s+(?:in\s+)?(?:its\s+|their\s+)?name" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListTables = new(
        @"\b(?:what\s+tables|tables?\s+(?:do|are|exist|do\s+we\s+have)|list\s+(?:all\s+)?tables|all\s+tables)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<MetadataHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default)
    {
        // Policy gate: when introspection is disabled, fall through silently. The next stage
        // (LLM planner / OOS regex) will either refuse or ignore the question. Returning null
        // here matches how ConversationalHandler / KnowledgeMatchHandler fall through.
        if (!_options.EnableSchemaIntrospection) return null;

        // Order matters: most specific patterns first.
        if (DataTypeOf.Match(question) is { Success: true } dt && !string.IsNullOrEmpty(dt.Groups["col"].Value))
        {
            var table = ResolveMetadataTable(dt.Groups["table"].Value);
            var column = dt.Groups["col"].Value;

            // Defence in depth: refuse before running the SQL when the requested column is
            // declared sensitive in the semantic layer. Returning an empty result (not an error)
            // hides the fact that the column exists — same surface as "column not found".
            if (!_schemaPolicy.IsColumnAllowed(table, column) || _schemaPolicy.IsColumnSensitive(table, column))
            {
                return new MetadataHandlerResult(string.Empty,
                    new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero));
            }
            if (!IsTableAllowed(table))
            {
                return new MetadataHandlerResult(string.Empty,
                    new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero));
            }

            var sql = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH " +
                      "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table AND COLUMN_NAME = @column;";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>
            {
                ["@table"] = table,
                ["@column"] = column,
            });
            return new MetadataHandlerResult(sql, await _executor.ExecuteAsync(compiled, cancellationToken));
        }

        // Try both Relationship patterns: "How are X related to Y" (forward) AND
        // "How are X and Y related" (reverse — relation word at the end).
        var relMatch = LooksLikeDataRowList(question) ? Match.Empty : Relationship.Match(question);
        if (!relMatch.Success && !LooksLikeDataRowList(question)) relMatch = RelationshipReverse.Match(question);
        if (relMatch.Success)
        {
            var a = relMatch.Groups["a"].Value;
            var b = relMatch.Groups["b"].Value;
            // Restricted mode: refuse when either side isn't a configured entity, so the user
            // can't probe foreign-key graphs into audit/identity/migration tables.
            if (!IsTableAllowed(a) || !IsTableAllowed(b))
            {
                return new MetadataHandlerResult(string.Empty,
                    new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero));
            }

            var sql =
                "SELECT fk.name AS fk_name, " +
                "       OBJECT_NAME(fk.parent_object_id) AS parent_table, " +
                "       OBJECT_NAME(fk.referenced_object_id) AS referenced_table " +
                "FROM sys.foreign_keys fk " +
                "WHERE (OBJECT_NAME(fk.parent_object_id) = @a AND OBJECT_NAME(fk.referenced_object_id) = @b) " +
                "   OR (OBJECT_NAME(fk.parent_object_id) = @b AND OBJECT_NAME(fk.referenced_object_id) = @a);";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>
            {
                ["@a"] = a,
                ["@b"] = b,
            });
            return new MetadataHandlerResult(sql, await _executor.ExecuteAsync(compiled, cancellationToken));
        }

        if (!LooksLikeOneColumnProjection(question) && ColumnsInTable.Match(question) is { Success: true } cols)
        {
            var table = ResolveMetadataTable(cols.Groups["table"].Value);
            if (!IsTableAllowed(table))
            {
                return new MetadataHandlerResult(string.Empty,
                    new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero));
            }

            var sql = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION " +
                      "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table " +
                      "ORDER BY ORDINAL_POSITION;";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>
            {
                ["@table"] = table,
            });
            var raw = await _executor.ExecuteAsync(compiled, cancellationToken);
            // ALWAYS strip sensitive columns — the policy is independent of the restriction
            // switch. PasswordHash / SecurityStamp / ConcurrencyStamp must never appear here.
            return new MetadataHandlerResult(sql, StripSensitiveColumnRows(raw, table));
        }

        if (WhichTable.Match(question) is { Success: true } wt)
        {
            var sql = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                      "WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME LIKE @pattern " +
                      "ORDER BY TABLE_NAME;";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>
            {
                ["@pattern"] = "%" + wt.Groups["keyword"].Value + "%",
            });
            var raw = await _executor.ExecuteAsync(compiled, cancellationToken);
            return new MetadataHandlerResult(sql, RestrictTablesToAllowed(raw));
        }

        if (SearchTablesByName.Match(question) is { Success: true } st)
        {
            var sql = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                      "WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME LIKE @pattern " +
                      "ORDER BY TABLE_NAME;";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>
            {
                ["@pattern"] = "%" + st.Groups["keyword"].Value + "%",
            });
            var raw = await _executor.ExecuteAsync(compiled, cancellationToken);
            return new MetadataHandlerResult(sql, RestrictTablesToAllowed(raw));
        }

        if (ListTables.IsMatch(question))
        {
            var sql = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                      "WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;";
            var compiled = new CompiledSql(sql, new Dictionary<string, object?>());
            var raw = await _executor.ExecuteAsync(compiled, cancellationToken);
            return new MetadataHandlerResult(sql, RestrictTablesToAllowed(raw));
        }

        return null; // no metadata pattern matched — caller falls back to generic refusal
    }

    /// <summary>
    /// Allowlist check used before running any per-table query. Returns true when the table is
    /// registered in the semantic layer OR when the restriction switch is off. Treats unknown
    /// table names as denied in restricted mode so the user can't probe internal tables by
    /// guessing names ("AspNetUserLogins", "__EFMigrationsHistory", etc.).
    /// </summary>
    private bool IsTableAllowed(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) return false;
        return _schemaPolicy.IsTableAllowed(table);
    }

    private static bool LooksLikeDataRowList(string question) =>
        Regex.IsMatch(question ?? "", @"^\s*(?:show|list|display|give\s+me|find|get)\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeOneColumnProjection(string question) =>
        Regex.IsMatch(question ?? "", @"^\s*(?:show|list|display|give\s+me|find|get)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(question ?? "", @"\bone\s+column\b", RegexOptions.IgnoreCase);

    private string ResolveMetadataTable(string requested)
    {
        var entity = _semanticLayer.GetEntityByNameOrSynonym(requested);
        return entity is not null ? entity.Table : requested;
    }

    /// <summary>
    /// Drop rows naming a sensitive column on <paramref name="table"/>. Matches the row's
    /// <c>COLUMN_NAME</c> field case-insensitively against the entity's SensitiveColumns set.
    /// </summary>
    private ExecutionResult StripSensitiveColumnRows(ExecutionResult raw, string table)
    {
        if (raw.Rows.Count == 0) return raw;
        var kept = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            if (row.TryGetValue("COLUMN_NAME", out var nameObj)
                && nameObj is string name
                && (!_schemaPolicy.IsColumnAllowed(table, name) || _schemaPolicy.IsColumnSensitive(table, name)))
            {
                continue; // sensitive — skip
            }
            kept.Add(row);
        }
        return raw with { Rows = kept, RowCount = kept.Count };
    }

    /// <summary>
    /// Filter a TABLE_NAME-bearing row set to only the tables registered in the semantic layer.
    /// No-op when <see cref="AnalystOptions.RestrictMetadataToConfiguredEntities"/> is off.
    /// </summary>
    private ExecutionResult RestrictTablesToAllowed(ExecutionResult raw)
    {
        if (raw.Rows.Count == 0) return raw;

        var kept = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            if (row.TryGetValue("TABLE_NAME", out var nameObj)
                && nameObj is string name
                && _schemaPolicy.IsTableAllowed(name))
            {
                kept.Add(row);
            }
        }
        return raw with { Rows = kept, RowCount = kept.Count };
    }
}
