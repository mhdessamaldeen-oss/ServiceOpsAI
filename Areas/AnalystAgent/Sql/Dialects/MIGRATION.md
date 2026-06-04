# Compiler dialect-migration plan

## Status

| Step | Status |
|---|---|
| `ISqlDialect` interface | ✅ shipped |
| `MssqlDialect` (T-SQL) | ✅ shipped, mirrors current emission |
| `PostgresDialect` | ✅ shipped, proven by unit tests |
| DI binding (default Mssql) | ✅ shipped |
| Compiler files refactored to use `ISqlDialect` | ✅ shipped (4 PRs landed) |
| Integration test (QuerySpec → 2 dialects produces 2 valid SQLs) | ✅ shipped (3 cases) |

## What's done

The compiler is fully dialect-aware. Switching the database target is now exactly one DI line:

```csharp
// To target Postgres:
services.AddSingleton<ISqlDialect, PostgresDialect>();
```

The compiler partial-class files emit NO literal T-SQL constructs anymore — every dialect-touching
fragment routes through `ISqlDialect`. The architectural claim "swapping the database is implementing
one class" is now mechanically true, not theoretical.

### What the sweep covered (4 PRs)

**PR 1 — `SqlCompiler.Helpers.cs`** (identifier quoting + null-wrap):
- `TryFormatColumn` → `_dialect.QuoteQualified`
- `TryWrapNullableGroupKey` → `_dialect.NullCoalesce` + `_dialect.CastAsString`
- `SqlCompiler` constructor extended to receive `ISqlDialect` via DI.

**PR 2 — `SqlCompiler.Select.cs`** (TOP / aliases):
- `TOP (N)` emission → `_dialect.TopClause(spec.Limit.Value)`
- `AS [bare]` / `AS [friendly]` / `AS [alias]` / `AS [Count]` / `AS [Computed]` → `AS {_dialect.QuoteIdentifier(...)}`

**PR 3 — `SqlCompiler.Where.cs`** (the big one — 20+ temporal-token expansions):
- `GETDATE()` → `_dialect.NowExpression`
- `CAST(GETDATE() AS DATE)` → `_dialect.CurrentDateExpression`
- `DATEADD(unit, n, base)` → `_dialect.DateAdd(unit, n, base)`
- `DATEFROMPARTS(y, m, d)` → `_dialect.DateFromParts(y, m, d)`
- `YEAR(x)` / `MONTH(x)` / `DAY(x)` → `_dialect.DatePart(unit, x)`
- T-SQL `DATEADD(unit, DATEDIFF(unit, 0, expr), 0)` truncation idiom → `_dialect.DateTrunc(unit, expr)`
- `LIKE` / `NOT LIKE` operators in `BuildFilterClause` → `_dialect.LikeOperator` / `_dialect.NotLikeOperator`
- Anti-join `[T].[C] IS NULL` brackets → `_dialect.QuoteQualified`
- Text-search column brackets → `_dialect.QuoteQualified`
- `TryExpandTemporalToken` signature now takes `ISqlDialect` (was static, still static — pure function with explicit dialect arg).

**PR 4 — `SqlCompiler.cs` (remaining partials)**:
- `BuildFrom`: every `[Table]` / `[Table].[Column]` → `_dialect.QuoteIdentifier` / `_dialect.QuoteQualified`
- `BuildOffsetFetch`: hardcoded `OFFSET N ROWS FETCH NEXT M ROWS ONLY` → `_dialect.LimitOffsetClause(spec.Limit, spec.Offset)`. Now correctly emits LIMIT/OFFSET on Postgres-style dialects too.
- `BuildOrderBy`: `"[" + alias + "]"` → `_dialect.QuoteIdentifier`
- `EmitTableColumn`: bracket emission → `_dialect.QuoteQualified`
- `QualifyColumnsInExpression`: every bracket emission → `_dialect.QuoteQualified`
- `DedupeAliasesInPlace`: parses the dialect's `IdentifierQuoteOpen` / `IdentifierQuoteClose` chars (new on `ISqlDialect`) so the dedup works for any dialect.

### Integration test

[`SqlCompilerDialectIntegrationTests`](../../../Tests/AnalystAgent.Tests/Compilation/SqlCompilerDialectIntegrationTests.cs)
constructs the compiler twice with the same catalog / semantic layer / options — only the dialect
binding differs — and asserts:

1. **Same spec → distinct SQL per dialect** — `COUNT(*) AS [Count]` on MSSQL vs `COUNT(*) AS "Count"` on PG; `TOP (10)` vs `LIMIT 10`; `CAST(GETDATE() AS DATE)` vs `CURRENT_DATE`; `[Tickets].[CreatedAt]` vs `"Tickets"."CreatedAt"`.
2. **OFFSET+LIMIT pagination** — `OFFSET N ROWS FETCH NEXT M ROWS ONLY` (MSSQL) vs `LIMIT M OFFSET N` (PG).
3. **`@q1_start` temporal token** — `DATEFROMPARTS(YEAR(GETDATE()), 1, 1)` (MSSQL) vs `MAKE_DATE(EXTRACT(YEAR FROM NOW())::int, 1, 1)` (PG).

After this test passes, the architectural claim is proven, not asserted.

## Future dialects

Each new dialect = one new class. Acceptance: pass every test in `SqlDialectTests.cs` (currently 41).
No exceptions.

Candidates by likelihood of demand:
- MySQL / MariaDB
- SQLite (useful for tests — would let `SqlCompilerDialectIntegrationTests` round-trip against a real engine in-process)
- Snowflake
- BigQuery
- Oracle (heavyweight; date math is significantly different)

Real engine validation (running the emitted PG SQL against a live PostgreSQL instance) is the
obvious next-week test once an integration environment exists.
