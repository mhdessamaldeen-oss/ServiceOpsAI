namespace SuperAdminCopilot.Compilation.Dialects;

/// <summary>
/// SQL-dialect abstraction. The compiler talks to this interface instead of hardcoding T-SQL
/// (or any specific dialect) syntax. Swapping the target database from SQL Server to PostgreSQL,
/// MySQL, Snowflake, etc. is implementing ONE class — no search-and-replace through the compiler.
///
/// <para><b>Design rules</b>:
/// <list type="bullet">
///   <item>Every method that emits a dialect-specific string is here. The compiler must never
///         contain a literal that depends on the dialect (no <c>"TOP"</c>, <c>"GETDATE"</c>,
///         <c>"["</c>, <c>"ISNULL"</c> anywhere outside dialect implementations).</item>
///   <item>The interface is intentionally narrow — only the constructs the compiler actually
///         emits today. Adding a new construct = adding one method here and one implementation
///         per dialect, not free-form SQL building.</item>
///   <item>All methods return strings (fragments). Composition is the compiler's job; emission
///         is the dialect's. Keeps the dialect side pure and easy to test.</item>
///   <item>Each dialect implementation is exhaustively unit-tested (one test per method); the
///         interface is the contract surface a new dialect implementer reads.</item>
/// </list></para>
///
/// <para><b>Inventory of T-SQL specifics this interface abstracts</b> (the compiler used to hardcode
/// these — every one is now behind a method):
/// identifier quoting (<c>[brackets]</c>) · TOP/LIMIT/OFFSET-FETCH · GETDATE / NOW · DATEADD ·
/// DATEDIFF · DATEFROMPARTS · YEAR/MONTH/DAY · CAST AS DATE / NVARCHAR · ISNULL ·
/// the 20+ temporal token expansions (<c>@today</c>, <c>@week_start</c>, <c>@q1_start</c>, …).</para>
/// </summary>
public interface ISqlDialect
{
    /// <summary>Dialect display name. Used in logs + diagnostic messages.</summary>
    string Name { get; }

    // ── Identifiers ────────────────────────────────────────────────────────────────────────

    /// <summary>Quote a single identifier (table or column name). T-SQL: <c>[X]</c>; Postgres: <c>"X"</c>.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Quote a qualified <c>Table.Column</c>. Convenience for the common case.</summary>
    string QuoteQualified(string table, string column);

    /// <summary>
    /// Opening identifier-quote char (T-SQL <c>[</c>, Postgres/SQLite <c>"</c>). Exposed so the
    /// alias-dedup pass can parse back already-quoted aliases without re-implementing per-dialect
    /// regex. Most callers should prefer <see cref="QuoteIdentifier"/>.
    /// </summary>
    char IdentifierQuoteOpen { get; }

    /// <summary>Closing identifier-quote char (T-SQL <c>]</c>, Postgres/SQLite <c>"</c>).</summary>
    char IdentifierQuoteClose { get; }

    // ── Result-set bounds (TOP / LIMIT / OFFSET-FETCH) ─────────────────────────────────────

    /// <summary>
    /// True when this dialect places the row-count limit BEFORE the column list
    /// (<c>SELECT TOP (N) col …</c>). False when the dialect uses a trailing clause
    /// (<c>SELECT col … LIMIT N</c>).
    /// </summary>
    bool LimitGoesBeforeColumns { get; }

    /// <summary>Emit the per-SELECT-list prefix for TOP-style dialects. Returns "" when unused.</summary>
    string TopClause(int limit);

    /// <summary>
    /// Emit the trailing OFFSET-FETCH clause for both styles. T-SQL emits
    /// <c>OFFSET N ROWS FETCH NEXT M ROWS ONLY</c>; Postgres emits <c>OFFSET N LIMIT M</c>.
    /// Returns "" when both offset and limit are absent.
    /// </summary>
    string LimitOffsetClause(int? limit, int? offset);

    // ── Date/time literals & "now" ─────────────────────────────────────────────────────────

    /// <summary>Current date+time. T-SQL: <c>GETDATE()</c>; Postgres: <c>NOW()</c>.</summary>
    string NowExpression { get; }

    /// <summary>Current date (no time). T-SQL: <c>CAST(GETDATE() AS DATE)</c>; Postgres: <c>CURRENT_DATE</c>.</summary>
    string CurrentDateExpression { get; }

    // ── Date arithmetic ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emit a date-add expression: <c>baseExpr + offset units</c>.
    /// Unit values: <c>second, minute, hour, day, week, month, quarter, year</c>.
    /// </summary>
    string DateAdd(string unit, int offset, string baseExpr);

    /// <summary>
    /// Emit a date-diff expression: integer count of <paramref name="unit"/> boundaries between
    /// <paramref name="startExpr"/> and <paramref name="endExpr"/>.
    /// </summary>
    string DateDiff(string unit, string startExpr, string endExpr);

    /// <summary>Construct a date from year/month/day expressions.</summary>
    string DateFromParts(string yearExpr, string monthExpr, string dayExpr);

    /// <summary>Extract the year, month, day, week, or quarter component of a date expression.</summary>
    string DatePart(string unit, string expr);

    /// <summary>Truncate a datetime expression down to the start of the given unit (week/month/quarter/year).</summary>
    string DateTrunc(string unit, string expr);

    /// <summary>Cast an expression to the dialect's DATE type.</summary>
    string CastAsDate(string expr);

    // ── Null handling ──────────────────────────────────────────────────────────────────────

    /// <summary>Replace NULL with a fallback. T-SQL: <c>ISNULL(a,b)</c>; Postgres: <c>COALESCE(a,b)</c>.</summary>
    string NullCoalesce(string expr, string fallback);

    // ── String / numeric casts ─────────────────────────────────────────────────────────────

    /// <summary>Cast an expression to a string of given max length. T-SQL: <c>CAST(x AS NVARCHAR(N))</c>; Postgres: <c>x::varchar(N)</c>.</summary>
    string CastAsString(string expr, int maxLength);

    /// <summary>Cast an expression to a 32-bit integer.</summary>
    string CastAsInt(string expr);

    /// <summary>Cast an expression to decimal(precision,scale).</summary>
    string CastAsDecimal(string expr, int precision, int scale);

    // ── Boolean & comparison operators that may differ ────────────────────────────────────

    /// <summary>LIKE pattern operator (T-SQL/Postgres share <c>LIKE</c>; MySQL is the same; SQLite same).</summary>
    string LikeOperator { get; }

    /// <summary>NOT LIKE pattern operator.</summary>
    string NotLikeOperator { get; }
}
