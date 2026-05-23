namespace SuperAdminCopilot.Validation;

using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

internal sealed class SqlAstValidator : IValidator
{
    private readonly IEntityCatalog _catalog;
    private readonly ICopilotSchemaAccessPolicy _schemaPolicy;

    public SqlAstValidator(IEntityCatalog catalog, ICopilotSchemaAccessPolicy schemaPolicy)
    {
        _catalog = catalog;
        _schemaPolicy = schemaPolicy;
    }

    public ValidationResult Validate(CompiledSql compiled)
    {
        var errors = new List<string>();

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment? fragment;
        using (var reader = new StringReader(compiled.Sql))
        {
            fragment = parser.Parse(reader, out var parseErrors);
            if (parseErrors is { Count: > 0 })
            {
                foreach (var e in parseErrors) errors.Add($"parse error at line {e.Line}: {e.Message}");
                return new ValidationResult(false, errors);
            }
        }

        // Reject multi-statement batches outright. The compiler should never produce them,
        // and accepting them is a clear injection vector ("...; DROP TABLE ...").
        if (fragment is TSqlScript script)
        {
            var topLevelStatements = script.Batches.SelectMany(b => b.Statements).Count();
            if (topLevelStatements > 1)
            {
                errors.Add($"multi-statement batches are not allowed (found {topLevelStatements}).");
                return new ValidationResult(false, errors);
            }
        }

        var allowlistVisitor = new StatementAllowlistVisitor();
        fragment!.Accept(allowlistVisitor);
        if (allowlistVisitor.Violations.Count > 0)
        {
            errors.AddRange(allowlistVisitor.Violations);
            return new ValidationResult(false, errors);
        }

        var idVisitor = new IdentifierVisitor();
        fragment.Accept(idVisitor);
        foreach (var t in idVisitor.Tables)
        {
            // Skip CTE-declared aliases — they appear as NamedTableReference in the parse tree
            // but aren't real tables. Without this, every CTE-bearing SQL (verified or LLM-emitted)
            // fails validation as "unknown table referenced: 'DailyCounts'" — the whole S5 suite
            // returns 0/8 even when the verified-query store has a 1.00 cosine match.
            if (idVisitor.CteNames.Contains(t)) continue;
            // Block system / metadata schemas (#50). Even SELECT-only access to these can leak
            // server topology, role memberships, and stored definitions. The metadata handler
            // queries INFORMATION_SCHEMA via its own read path; the planner must NEVER reference
            // these from a generated query.
            if (IsSystemTableRef(t))
            {
                errors.Add($"system / metadata table reference '{t}' is not allowed.");
                continue;
            }
            if (!_catalog.TableExists(t)) errors.Add($"unknown table referenced: '{t}'");
            else if (!_schemaPolicy.IsTableAllowed(t)) errors.Add($"table '{t}' is blocked by Copilot schema policy.");
            else
            {
                foreach (var column in _catalog.GetColumns(t))
                {
                    if (_schemaPolicy.IsColumnAllowed(t, column.ColumnName)) continue;
                    if (SqlReferencesColumn(compiled.Sql, t, column.ColumnName))
                        errors.Add($"column '{t}.{column.ColumnName}' is blocked by Copilot schema policy.");
                }
            }
        }

        return errors.Count == 0
            ? new ValidationResult(true, Array.Empty<string>())
            : new ValidationResult(false, errors);
    }

    private static bool SqlReferencesColumn(string sql, string table, string column)
    {
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(column)) return false;
        var escapedTable = Regex.Escape(table);
        var escapedColumn = Regex.Escape(column);
        return Regex.IsMatch(sql, $@"\[{escapedTable}\]\s*\.\s*\[{escapedColumn}\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(sql, $@"(?<![A-Za-z0-9_])\[{escapedColumn}\](?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True for any reference to a SQL Server system / metadata catalog (sys.*, INFORMATION_SCHEMA.*,
    /// sysobjects, syscolumns, mssqlsystemresource, etc.). Match is case-insensitive on bare identifier or
    /// schema-qualified prefix. The planner has its own metadata path; raw SQL must never reach these.</summary>
    private static bool IsSystemTableRef(string tableName)
    {
        if (string.IsNullOrEmpty(tableName)) return false;
        var lower = tableName.ToLowerInvariant();
        // Schema-qualified system refs ("sys.tables", "information_schema.columns") — captured
        // by IdentifierVisitor when it sees those schema prefixes.
        if (lower.StartsWith("sys.", StringComparison.Ordinal)
            || lower.StartsWith("information_schema.", StringComparison.Ordinal))
            return true;
        // Legacy unprefixed system names that any sane DB will reject anyway, but cheap to block.
        return lower is "sysobjects" or "syscolumns" or "sysindexes" or "syscomments"
            or "sysconstraints" or "sysdatabases" or "sysschemas" or "syslogins"
            or "sysusers" or "sysprocesses";
    }

    private sealed class StatementAllowlistVisitor : TSqlFragmentVisitor
    {
        public List<string> Violations { get; } = new();

        // ── Mutating statements ─────────────────────────────────────────────────
        public override void Visit(InsertStatement node)        => Violations.Add("INSERT is not allowed.");
        public override void Visit(UpdateStatement node)        => Violations.Add("UPDATE is not allowed.");
        public override void Visit(DeleteStatement node)        => Violations.Add("DELETE is not allowed.");
        public override void Visit(MergeStatement node)         => Violations.Add("MERGE is not allowed.");
        public override void Visit(TruncateTableStatement node) => Violations.Add("TRUNCATE is not allowed.");
        public override void Visit(DropObjectsStatement node)   => Violations.Add("DROP is not allowed.");
        public override void Visit(AlterTableStatement node)    => Violations.Add("ALTER is not allowed.");
        public override void Visit(CreateTableStatement node)   => Violations.Add("CREATE TABLE is not allowed.");
        public override void Visit(ExecuteStatement node)       => Violations.Add("EXEC is not allowed.");

        // ── Bulk / extract paths a clever planner could produce ──────────────────
        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
                Violations.Add("SELECT INTO is not allowed (creates a new table).");
        }
        public override void Visit(BulkInsertStatement node)    => Violations.Add("BULK INSERT is not allowed.");
        public override void Visit(OpenRowsetTableReference node) => Violations.Add("OPENROWSET is not allowed.");
        public override void Visit(OpenQueryTableReference node)  => Violations.Add("OPENQUERY is not allowed.");
    }

    private sealed class IdentifierVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is { Length: > 0 } name)
                CteNames.Add(name);
        }

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value is not { } table) return;
            // If the reference is schema-qualified, prepend "schema." so IsSystemTableRef can
            // detect "sys.tables" or "INFORMATION_SCHEMA.columns". Bare table names go in as-is
            // and get checked against the catalog.
            var schema = node.SchemaObject.SchemaIdentifier?.Value;
            if (!string.IsNullOrEmpty(schema)
                && (string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)))
            {
                Tables.Add(schema + "." + table);
            }
            else
            {
                Tables.Add(table);
            }
        }
    }
}
