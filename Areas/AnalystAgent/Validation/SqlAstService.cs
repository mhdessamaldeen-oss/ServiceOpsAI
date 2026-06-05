namespace AnalystAgent.Validation;

using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

/// <summary>
/// STAGE-2 repair foundation: a thin Parse/Render service over Microsoft ScriptDom so the
/// highest-risk deterministic SQL repairs can be expressed as AST MUTATIONS on the already-parsed
/// tree instead of regex-on-text. Token-gluing (the class of bug that motivated this — a GROUP BY
/// clause regex that ate the whitespace before HAVING) is impossible by construction: the tree is
/// re-serialized by the grammar, never by string concatenation.
///
/// <para>The service owns ONLY the parse/render plumbing — reuse the SAME <see cref="TSql170Parser"/>
/// the validator uses, and a single shared <see cref="Sql160ScriptGenerator"/> for rendering. The
/// individual repair passes live in <see cref="SqlAstRepairs"/> and take the parsed fragment so a
/// caller parses ONCE per attempt and shares the tree across passes.</para>
///
/// <para>Internal + static: no DI footprint, no allocation of intent, deterministic. This is the
/// MECHANISM swap for three repairs already gated behind <c>AnalystOptions.EnableAstRepairs</c>;
/// it does not change any repair POLICY (which column is a label, which value is grounded, which
/// table owns a column) — those decision inputs are passed in by the caller unchanged.</para>
/// </summary>
internal static class SqlAstService
{
    // One shared generator. Compact, single-line-ish output (no forced newlines before clauses) so a
    // rendered fragment stays a clean one-statement SELECT the validator + executor consume exactly as
    // they consume the regex path's output. KeywordCasing/SqlVersion fixed for deterministic rendering.
    private static readonly SqlScriptGeneratorOptions GeneratorOptions = new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        SqlVersion = SqlVersion.Sql160,
        IncludeSemicolons = false,
        AlignClauseBodies = false,
        NewLineBeforeFromClause = false,
        NewLineBeforeWhereClause = false,
        NewLineBeforeGroupByClause = false,
        NewLineBeforeOrderByClause = false,
        NewLineBeforeHavingClause = false,
        NewLineBeforeJoinClause = false,
        MultilineSelectElementsList = false,
        MultilineWherePredicatesList = false,
    };

    /// <summary>
    /// Parse <paramref name="sql"/> into a ScriptDom fragment using the SAME parser the validator runs
    /// (<see cref="TSql170Parser"/> with quoted-identifier defaults). Returns the fragment and the parser's
    /// error list. A non-empty <paramref name="errors"/> means the SQL did not parse (the caller should not
    /// attempt AST repairs on it); the fragment may still be non-null (ScriptDom returns a best-effort tree).
    /// </summary>
    public static TSqlFragment? Parse(string sql, out IList<ParseError> errors)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql ?? string.Empty);
        var fragment = parser.Parse(reader, out errors);
        errors ??= new List<ParseError>();
        return fragment;
    }

    /// <summary>
    /// Render a (possibly mutated) ScriptDom fragment back to T-SQL text via the shared
    /// <see cref="Sql160ScriptGenerator"/>. Pure serialization — the grammar re-folds clauses, so no
    /// token-gluing is possible. Trims the trailing newline the generator appends so the output is a clean
    /// single-statement string the validator/executor consume directly.
    /// </summary>
    public static string Render(TSqlFragment fragment)
    {
        var generator = new Sql160ScriptGenerator(GeneratorOptions);
        generator.GenerateScript(fragment, out var script);
        return (script ?? string.Empty).Trim();
    }

    /// <summary>
    /// Render <paramref name="fragment"/> and return it ONLY when it re-parses clean (defensive guard —
    /// a grammar-built tree always re-parses, but the guard keeps the contract honest and discards any
    /// pathological case rather than poisoning the next pass). Returns false (and the original
    /// <paramref name="original"/> unchanged in <paramref name="rendered"/>) when rendering produced
    /// unparseable text. The shared safety net the three AST passes route their result through.
    /// </summary>
    public static bool TryRenderIfParses(TSqlFragment fragment, string original, out string rendered)
    {
        rendered = original;
        var candidate = Render(fragment);
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        Parse(candidate, out var errors);
        if (errors.Count > 0) return false;
        rendered = candidate;
        return true;
    }

    /// <summary>
    /// Find the single top-level <see cref="QuerySpecification"/> the repairs operate on. The direct path
    /// only ships single-statement SELECTs, so the FIRST QuerySpecification reachable from the fragment is
    /// the target. Returns null when the fragment is not a single SELECT (e.g. a set operation at the top, a
    /// non-SELECT statement) — the caller then leaves the SQL untouched, mirroring the regex passes' "too
    /// complex → skip" posture.
    /// </summary>
    public static QuerySpecification? FindQuerySpecification(TSqlFragment? fragment)
    {
        if (fragment is null) return null;
        var finder = new QuerySpecificationFinder();
        fragment.Accept(finder);
        return finder.Result;
    }

    private sealed class QuerySpecificationFinder : TSqlFragmentVisitor
    {
        public QuerySpecification? Result { get; private set; }
        public override void ExplicitVisit(QuerySpecification node)
        {
            Result ??= node;   // first (outermost-encountered) wins; subqueries are deeper, never overwrite
            // do NOT descend — we only ever want the top query spec for these repairs.
        }
    }

    /// <summary>Bare column name (last identifier) of a column reference, or null when it isn't a
    /// simple column reference. Used by the predicate passes to key off the column NAME shape.</summary>
    public static string? BareColumnName(ScalarExpression? expr) =>
        expr is ColumnReferenceExpression c && c.MultiPartIdentifier is { Count: > 0 } mpi
            ? mpi.Identifiers[^1].Value
            : null;

    /// <summary>The string value of a <see cref="StringLiteral"/> right-hand side, or null when the
    /// expression is not a string literal (so an int comparison / column-to-column compare is never touched).</summary>
    public static string? StringLiteralValue(ScalarExpression? expr) =>
        expr is StringLiteral s ? s.Value : null;
}
