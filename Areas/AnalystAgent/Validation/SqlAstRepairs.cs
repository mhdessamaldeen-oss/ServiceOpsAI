namespace AnalystAgent.Validation;

using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>
/// STAGE-2 AST repair passes — the three highest-value/risk deterministic repairs re-expressed as
/// MUTATIONS on the ScriptDom tree (parsed via <see cref="SqlAstService"/>) instead of regex-on-text.
/// Token-gluing is impossible: the grammar re-serializes the mutated tree.
///
/// <para>These mirror the POLICY of the regex versions in <c>DirectAnalystPath</c> exactly — same
/// decision inputs (which column is a label, the grounded values, the getTable lookup), same guards,
/// same conservatism (return false / unchanged when nothing matched). ONLY the mechanism changes.
/// They are gated behind <c>AnalystOptions.EnableAstRepairs</c> (default OFF) so the regex path stays
/// byte-identical until the AST path is proven equivalent.</para>
///
/// <para>Schema-agnostic by construction: the only "vocabulary" is the SAME label/status/state NAME-SHAPE
/// the regex versions use, passed in as a predicate, plus the caller-supplied grounded values and PK —
/// no per-table or domain literals live here.</para>
/// </summary>
internal static class SqlAstRepairs
{
    // ── Pass 1: GROUP-BY grain ─────────────────────────────────────────────────────────────────
    // Prepend the entity PK to the GROUP BY when the SOLE grouping column is a label column. The AST
    // version cannot glue the rebuilt GROUP BY to the following HAVING/ORDER BY — the exact regex bug
    // that motivated this. Same guards as TryFixGroupByGrain: a real aggregate present, a SINGLE
    // label-shaped grouping column, a single-column PK that isn't already grouped.

    /// <summary>AST form of <c>DirectAnalystPath.TryFixGroupByGrain</c>. Adds the owning table's
    /// single-column primary key as the FIRST grouping specification when the sole GROUP BY column is a
    /// label column, restoring per-entity grain. Decision inputs are passed in so the policy matches the
    /// regex version: <paramref name="isLabelColumn"/> (the label NAME-SHAPE test), <paramref name="resolvePk"/>
    /// (alias/owner → single-column PK), <paramref name="hasAggregate"/>. Returns the repaired SQL via
    /// <paramref name="repaired"/> and true when it mutated; false (unchanged) otherwise.</summary>
    public static bool TryFixGroupByGrain(
        string sql,
        Func<string, bool> isLabelColumn,
        Func<ColumnReferenceExpression, (string Qualifier, string PkColumn)?> resolvePk,
        out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;

        var fragment = SqlAstService.Parse(sql, out var parseErrors);
        if (parseErrors.Count > 0 || fragment is null) return false;
        var query = SqlAstService.FindQuerySpecification(fragment);
        if (query is null) return false;

        // Real aggregate required (a label-only grouping is the bug only with an aggregate present).
        if (!HasAggregateCall(query)) return false;

        var specs = query.GroupByClause?.GroupingSpecifications;
        if (specs is not { Count: 1 }) return false;                          // SINGLE grouping column only
        if (specs[0] is not ExpressionGroupingSpecification egs) return false;
        if (egs.Expression is not ColumnReferenceExpression colRef) return false;
        var bareCol = SqlAstService.BareColumnName(colRef);
        if (string.IsNullOrEmpty(bareCol) || !isLabelColumn(bareCol)) return false;   // label-shaped only

        var pk = resolvePk(colRef);
        if (pk is not { } resolved) return false;                            // no unambiguous single-col PK owner
        var (qualifier, pkCol) = resolved;
        if (string.Equals(pkCol, bareCol, StringComparison.OrdinalIgnoreCase)) return false;  // already the key

        // Prepend "<qualifier>.<pkCol>" as the first grouping spec. Built from the grammar — no gluing.
        var pkSpec = new ExpressionGroupingSpecification
        {
            Expression = MakeColumnReference(qualifier, pkCol),
        };
        query.GroupByClause!.GroupingSpecifications.Insert(0, pkSpec);

        return SqlAstService.TryRenderIfParses(fragment, sql, out repaired)
            && !string.Equals(repaired, sql, StringComparison.Ordinal);
    }

    // ── Pass 2: PREDICATE strip (status case) ──────────────────────────────────────────────────
    // Remove a matched status-equality BooleanExpression from the WHERE/HAVING tree, letting the grammar
    // re-fold the AND/OR. The AST version handles OR / parens / subqueries the regex string strip can't.

    /// <summary>AST form of the status case of <c>DirectAnalystPath.TryStripUnrequestedStatusFilter</c>.
    /// Removes every <c>&lt;col-ending-in-Status/State&gt; = / != / &lt;&gt; 'literal'</c> comparison whose
    /// literal the caller's <paramref name="shouldStrip"/> predicate says is UNREQUESTED (neither in the
    /// question nor grounded). The boolean tree is rebuilt without those nodes, so a complex
    /// <c>WHERE a AND (b OR c) AND Status='X'</c> collapses to a valid <c>WHERE a AND (b OR c)</c> with the
    /// AND re-folded by the grammar. <paramref name="isStatusColumn"/> is the column NAME-SHAPE test (same as
    /// the regex: ends in Status/State). Returns true (and the repaired SQL) when at least one predicate was
    /// removed; false (unchanged) otherwise.</summary>
    public static bool TryStripUnrequestedStatusFilter(
        string sql,
        Func<string, bool> isStatusColumn,
        Func<string, bool> shouldStrip,
        out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;

        var fragment = SqlAstService.Parse(sql, out var parseErrors);
        if (parseErrors.Count > 0 || fragment is null) return false;
        var query = SqlAstService.FindQuerySpecification(fragment);
        if (query is null) return false;

        bool IsStripTarget(BooleanExpression e)
        {
            if (e is not BooleanComparisonExpression cmp) return false;
            if (cmp.ComparisonType is not (BooleanComparisonType.Equals
                or BooleanComparisonType.NotEqualToBrackets
                or BooleanComparisonType.NotEqualToExclamation)) return false;
            // The column can be on either side; the literal on the other.
            var col = SqlAstService.BareColumnName(cmp.FirstExpression) ?? SqlAstService.BareColumnName(cmp.SecondExpression);
            var literal = SqlAstService.StringLiteralValue(cmp.SecondExpression) ?? SqlAstService.StringLiteralValue(cmp.FirstExpression);
            if (col is null || literal is null) return false;
            return isStatusColumn(col) && shouldStrip(literal);
        }

        var changed = false;
        if (query.WhereClause is { SearchCondition: { } whereCond })
        {
            var rebuilt = RemoveMatching(whereCond, IsStripTarget, ref changed);
            query.WhereClause.SearchCondition = rebuilt!;   // null ⇒ whole WHERE emptied; handled below
            if (rebuilt is null) query.WhereClause = null;  // drop the now-empty WHERE entirely (no dangling clause)
        }
        if (query.HavingClause is { SearchCondition: { } havingCond })
        {
            var rebuilt = RemoveMatching(havingCond, IsStripTarget, ref changed);
            if (rebuilt is null) query.HavingClause = null;
            else query.HavingClause.SearchCondition = rebuilt;
        }

        if (!changed) return false;
        return SqlAstService.TryRenderIfParses(fragment, sql, out repaired)
            && !string.Equals(repaired, sql, StringComparison.Ordinal);
    }

    // ── Pass 3: VALUE injection (multi-value → single IN predicate) ─────────────────────────────
    // When 2+ values target one (table,column), add a SINGLE InPredicate to the WHERE. The regex version
    // already groups to IN; the AST version makes the insertion robust (no clause-boundary string surgery).

    /// <summary>AST form of the multi-value case of <c>DirectAnalystPath.TryInjectGroundedValueFilters</c>.
    /// For each (table,column) group the caller supplies with 2+ distinct values, builds ONE
    /// <c>&lt;qualifier&gt;.&lt;col&gt; IN ('a','b',…)</c> predicate and ANDs it into the WHERE (creating the
    /// WHERE when absent). <paramref name="resolveQualifier"/> maps a target table to the alias/name actually
    /// used in the FROM/JOIN (so an aliased table is qualified correctly), returning null when the table isn't
    /// in the query (skip). Single-value groups are intentionally left to the regex/equality path — this pass
    /// is the multi-value robustness upgrade only. Returns true (and the repaired SQL) when at least one IN was
    /// added; false otherwise.</summary>
    public static bool TryInjectGroundedValueFilters(
        string sql,
        IReadOnlyList<(string Table, string Column, IReadOnlyList<string> Values)> multiValueGroups,
        Func<string, string?> resolveQualifier,
        out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql) || multiValueGroups is null || multiValueGroups.Count == 0) return false;

        var fragment = SqlAstService.Parse(sql, out var parseErrors);
        if (parseErrors.Count > 0 || fragment is null) return false;
        var query = SqlAstService.FindQuerySpecification(fragment);
        if (query is null) return false;

        var changed = false;
        foreach (var (table, column, values) in multiValueGroups)
        {
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column)) continue;
            var distinct = (values ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count < 2) continue;                         // multi-value only (single = regex/equality path)
            var qualifier = resolveQualifier(table);
            if (string.IsNullOrEmpty(qualifier)) continue;            // table not in query → skip

            var inPredicate = new InPredicate { Expression = MakeColumnReference(qualifier!, column) };
            foreach (var v in distinct) inPredicate.Values.Add(new StringLiteral { Value = v });

            AndIntoWhere(query, inPredicate);
            changed = true;
        }

        if (!changed) return false;
        return SqlAstService.TryRenderIfParses(fragment, sql, out repaired)
            && !string.Equals(repaired, sql, StringComparison.Ordinal);
    }

    // ── Shared tree helpers ────────────────────────────────────────────────────────────────────

    /// <summary>True when the SELECT list contains at least one aggregate FUNCTION CALL (COUNT/SUM/AVG/…).
    /// Mirrors the regex aggregate-presence guard — the grain fix is only valid with an aggregate present.</summary>
    private static bool HasAggregateCall(QuerySpecification query)
    {
        var v = new AggregateCallVisitor();
        foreach (var e in query.SelectElements) e.Accept(v);
        query.HavingClause?.Accept(v);
        return v.Found;
    }

    private sealed class AggregateCallVisitor : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }
        public override void Visit(FunctionCall node)
        {
            // A FunctionCall with a non-empty UniqueRowFilter (DISTINCT) OR a known aggregate name.
            var name = node.FunctionName?.Value;
            if (!string.IsNullOrEmpty(name) && AggregateNames.Contains(name)) Found = true;
        }
    }

    // The ANSI aggregate set the grain guard keys off — the SAME set the regex used (SUM|COUNT|AVG|MIN|MAX).
    // Not domain vocabulary; SQL grammar. Kept here (not a config knob) because it is the grammar's fixed
    // aggregate list, identical to the regex literal it replaces; the operator-tunable aggregate list for the
    // ungrounded-projection guard lives in AnalystOptions.AggregateSqlFunctions and is unrelated to this pass.
    private static readonly HashSet<string> AggregateNames = new(StringComparer.OrdinalIgnoreCase)
    { "SUM", "COUNT", "AVG", "MIN", "MAX" };

    /// <summary>Rebuilds a boolean expression tree WITHOUT the nodes matching <paramref name="predicate"/>,
    /// letting the AND/OR re-fold. Returns the rebuilt expression (possibly a sub-expression when a sibling
    /// was removed), or null when the WHOLE expression was removed (the caller then drops the clause). Sets
    /// <paramref name="changed"/> true when any node was removed. Handles AND/OR (BooleanBinaryExpression) and
    /// parentheses (BooleanParenthesisExpression); a parenthesis whose inner expression fully collapses is
    /// itself removed.</summary>
    private static BooleanExpression? RemoveMatching(
        BooleanExpression expr, Func<BooleanExpression, bool> predicate, ref bool changed)
    {
        switch (expr)
        {
            case BooleanBinaryExpression bin:
            {
                var left = RemoveMatching(bin.FirstExpression, predicate, ref changed);
                var right = RemoveMatching(bin.SecondExpression, predicate, ref changed);
                if (left is null) return right;            // left collapsed → just the right survives
                if (right is null) return left;            // right collapsed → just the left survives
                bin.FirstExpression = left;
                bin.SecondExpression = right;
                return bin;
            }
            case BooleanParenthesisExpression paren:
            {
                var inner = RemoveMatching(paren.Expression, predicate, ref changed);
                if (inner is null) return null;            // (…) emptied → drop the parenthesis too
                paren.Expression = inner;
                return paren;
            }
            default:
                if (predicate(expr)) { changed = true; return null; }   // leaf predicate matched → remove it
                return expr;
        }
    }

    /// <summary>AND a new predicate into the query's WHERE clause, creating the WHERE when absent. The
    /// grammar serializes the conjunction — no clause-boundary string surgery.</summary>
    private static void AndIntoWhere(QuerySpecification query, BooleanExpression predicate)
    {
        if (query.WhereClause?.SearchCondition is { } existing)
        {
            query.WhereClause.SearchCondition = new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = existing,
                SecondExpression = predicate,
            };
        }
        else
        {
            query.WhereClause = new WhereClause { SearchCondition = predicate };
        }
    }

    /// <summary>Builds a qualified <c>&lt;qualifier&gt;.&lt;column&gt;</c> column reference node. Qualifier
    /// empty ⇒ a bare column reference. Identifiers carry no quote type, so the generator quotes per its
    /// options (matching how the validator/executor see any other identifier).</summary>
    private static ColumnReferenceExpression MakeColumnReference(string qualifier, string column)
    {
        var mpi = new MultiPartIdentifier();
        if (!string.IsNullOrEmpty(qualifier)) mpi.Identifiers.Add(new Identifier { Value = qualifier });
        mpi.Identifiers.Add(new Identifier { Value = column });
        return new ColumnReferenceExpression { MultiPartIdentifier = mpi };
    }
}
