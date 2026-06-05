namespace AnalystAgent.Validation;

using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;
using AnalystAgent.Schema;

internal sealed class SqlAstValidator : IValidator
{
    private readonly IEntityCatalog _catalog;
    private readonly IAnalystSchemaAccessPolicy _schemaPolicy;

    public SqlAstValidator(IEntityCatalog catalog, IAnalystSchemaAccessPolicy schemaPolicy)
    {
        _catalog = catalog;
        _schemaPolicy = schemaPolicy;
    }

    // Pre-parse rejection: the LLM sometimes emits template placeholders inside generated
    // SQL — e.g. `WHERE RegionId = '<RegionId for Damascus>'` or `WHERE Status = '{ "query": ... }'`.
    // These slip past T-SQL parsing because they're well-formed quoted strings, then explode at
    // execution time with "Conversion failed when converting nvarchar value '<...>' to data type int".
    // Catching them here gives a clean rejection with an actionable message and skips the doomed
    // parse + execute cycle. Patterns chosen by SHAPE (not content) so this never enumerates
    // domain vocabulary:
    //   - <word> or <words with spaces>  ← universal placeholder syntax
    //   - JSON-fragment leakage: { "key": "..." } appearing inside a literal
    // Real domain values never look like either.
    private static readonly Regex AngleBracketPlaceholder = new(
        @"<[A-Za-z][A-Za-z0-9_\s]{0,80}>",
        RegexOptions.Compiled);

    private static readonly Regex JsonFragmentLeak = new(
        @"\{\s*""[A-Za-z_][A-Za-z0-9_]*""\s*:",
        RegexOptions.Compiled);

    /// <summary>
    /// CHEAP, SYNTAX-ONLY parse check — runs <see cref="TSql170Parser"/> and reports whether the text
    /// has ZERO parse errors. This is NOT the full allowlist/identifier validation (no catalog, no policy,
    /// no GROUP-BY semantics): it answers only "is this syntactically a parseable T-SQL fragment?".
    ///
    /// <para>Used by the deterministic repair chain to PARSE-CHECK each rewrite at its source: a string
    /// mutation that produces unparseable SQL is discarded (kept as a no-op) instead of poisoning the next
    /// repair pass before the real validator runs. A valid SELECT always parses, so this never false-rejects
    /// a behaviour-preserving strip; it only catches a rewrite that broke the syntax (e.g. a clause glued to
    /// the next keyword). Empty/whitespace parses to no fragment with no errors → true (the executor handles
    /// the empty case, mirroring <see cref="Validate"/>'s posture).</para>
    /// </summary>
    public static bool ParsesAsSingleSelect(string? sql)
    {
        if (sql is null) return false;
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var parseErrors);
        return parseErrors is null || parseErrors.Count == 0;
    }

    public ValidationResult Validate(CompiledSql compiled)
    {
        var errors = new List<string>();

        // Reject template/placeholder leakage BEFORE parsing — these would parse cleanly as
        // string literals and fail only at execution. Surfacing them here gives a clean
        // diagnostic and avoids burning a DB round-trip on doomed SQL. The patterns are
        // shape-based, not vocabulary-based — won't false-fire on legitimate domain text.
        if (AngleBracketPlaceholder.IsMatch(compiled.Sql))
        {
            errors.Add("SQL contains a template placeholder of the form <…>. The LLM produced an unfilled template instead of a real value; entity resolution upstream is missing or failed.");
            return new ValidationResult(false, errors);
        }
        if (JsonFragmentLeak.IsMatch(compiled.Sql))
        {
            errors.Add("SQL contains a JSON fragment as a literal value. The LLM leaked its own structured-output envelope into the generated SQL; treat as a malformed generation.");
            return new ValidationResult(false, errors);
        }
        // Parameter values too — the compiler may have parameterized a JSON-string filter value.
        // The above checks only see SQL text; without this, JSON-as-parameter slips past validation
        // and SQL Server fails with "Conversion failed when converting nvarchar value '{...}' to int".
        foreach (var kv in compiled.Parameters)
        {
            if (kv.Value is string sv && sv.Length > 2 && (sv[0] == '{' || sv[0] == '[')
                && JsonFragmentLeak.IsMatch(sv))
            {
                errors.Add($"Parameter {kv.Key} carries a JSON envelope value. The LLM leaked structured output into a filter value; treat as a malformed generation.");
                return new ValidationResult(false, errors);
            }
        }

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

        // GROUP BY semantic check — catch the "SELECT col not in GROUP BY and not aggregated" violation BEFORE it reaches SQL Server. Triggered by LlmDirectSqlEmitter output that bypasses the compiler's GROUP BY safety.
        var gbVisitor = new GroupBySanityVisitor();
        fragment.Accept(gbVisitor);
        if (gbVisitor.Violations.Count > 0)
        {
            errors.AddRange(gbVisitor.Violations);
        }

        return errors.Count == 0
            ? new ValidationResult(true, Array.Empty<string>())
            : new ValidationResult(false, errors);
    }

    // GROUP BY column-reference sanity check using the parsed AST. False-positive-averse: only flags SelectScalarExpression with a bare ColumnReferenceExpression (the common LLM-emitted form) that doesn't appear in GROUP BY and isn't wrapped in an aggregate. Complex expressions (CASE, CAST, arithmetic) are not flagged — SQL Server will reject them itself if invalid.
    private sealed class GroupBySanityVisitor : TSqlFragmentVisitor
    {
        public List<string> Violations { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            // Only enforce SELECT/GROUP-BY consistency when GROUP BY has at least one real grouping key.
            // ScriptDom may parse a stub GroupByClause even for SQL without an actual GROUP BY clause —
            // checking for non-empty GroupingSpecifications avoids false-flagging plain SELECTs.
            var groupingSpecs = node.GroupByClause?.GroupingSpecifications;
            if (groupingSpecs is not null && groupingSpecs.Count > 0)
            {
                var groupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in groupingSpecs)
                {
                    if (spec is ExpressionGroupingSpecification eg
                        && eg.Expression is ColumnReferenceExpression cre)
                    {
                        groupKeys.Add(ColumnText(cre));
                    }
                }
                // Only check when we extracted at least one column-reference grouping key — otherwise the GROUP BY is by computed expressions and we can't reliably compare SELECT columns to it.
                if (groupKeys.Count > 0)
                {
                    foreach (var elem in node.SelectElements)
                    {
                        if (elem is not SelectScalarExpression sse) continue;
                        if (sse.Expression is not ColumnReferenceExpression colRef) continue;
                        var key = ColumnText(colRef);
                        if (!groupKeys.Contains(key))
                        {
                            Violations.Add($"column '{key}' is in the SELECT list but not in GROUP BY and not wrapped in an aggregate function — SQL Server will reject this query.");
                        }
                    }
                }
            }
            base.ExplicitVisit(node);
        }

        private static string ColumnText(ColumnReferenceExpression c)
        {
            if (c.MultiPartIdentifier is null) return "";
            return string.Join(".", c.MultiPartIdentifier.Identifiers.Select(i => i.Value));
        }
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
