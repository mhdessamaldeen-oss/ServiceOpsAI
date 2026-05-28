namespace SuperAdminCopilot.Compilation;

using System.Collections;
using System.Text.Json;

/// <summary>
/// D.4 — Helpers cluster of <see cref="SqlCompiler"/>. Pure utility methods on column / table
/// references and JSON-element → CLR coercion. No SQL composition logic here; that lives in
/// the other partials and the main file.
/// </summary>
internal sealed partial class SqlCompiler
{
    /// <summary>
    /// Normalises the configured <c>DefaultProjectionJoinKind</c> to a known value. Anything
    /// other than <c>"inner"</c> or <c>"left"</c> falls back to <c>"left"</c> (the original
    /// hardcoded default before the option was introduced), so a typo in JSON can't accidentally
    /// disable the auto-promote logic.
    /// </summary>
    private static string NormalizeProjectionKind(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Models.SpecConstants.JoinKinds.Left;
        var v = configured.Trim().ToLowerInvariant();
        return v == Models.SpecConstants.JoinKinds.Inner
            ? Models.SpecConstants.JoinKinds.Inner
            : Models.SpecConstants.JoinKinds.Left;
    }

    private static (string Table, string Column) SplitQualified(string s)
    {
        if (string.IsNullOrEmpty(s)) return ("", "");
        // The LLM may emit any of: "Column" / "Table.Column" / "Schema.Table.Column" / "[dbo].[Tickets].[Title]".
        // Strip brackets, then split by dots, take the LAST two parts as Table.Column.
        var parts = s.Replace("[", "").Replace("]", "")
                     .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => ("", parts[0]),
            _ => (parts[^2], parts[^1]),  // last two parts = Table, Column
        };
    }

    /// <summary>
    /// Strips any schema prefix (e.g. "dbo.Tickets" → "Tickets") and bracket noise from a
    /// table reference so the catalog lookup (which stores bare names) succeeds.
    /// </summary>
    private static string StripSchema(string tableRef)
    {
        if (string.IsNullOrEmpty(tableRef)) return tableRef;
        var parts = tableRef.Replace("[", "").Replace("]", "")
                            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "" : parts[^1];  // last segment = bare table name
    }

    private static void AddTable(string columnRef, HashSet<string> set)
    {
        var (t, _) = SplitQualified(columnRef);
        if (!string.IsNullOrEmpty(t)) set.Add(t);
    }

    private bool TryFormatColumn(string columnRef, out string formatted)
    {
        var (t, c) = SplitQualified(columnRef);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || !_catalog.ColumnExists(t, c))
        {
            formatted = "";
            return false;
        }
        formatted = _dialect.QuoteQualified(t, c);
        return true;
    }

    /// <summary>
    /// F1 — wraps a GROUP BY column that allows NULL with <c>ISNULL(...,'(Unassigned)')</c>
    /// so the resulting bucket has a human-readable label instead of an unlabeled NULL row.
    /// Used by both BuildSelect (for the SELECT-side projection of GROUP BY columns) and
    /// BuildGroupBy so the two expressions are lexically identical and SQL Server accepts the
    /// query. Returns the plain <c>[Table].[Column]</c> form when the column is not nullable
    /// or its type can't be safely wrapped (numeric/datetime — they aren't user-facing labels
    /// anyway). Caller must add <c>AS [Column]</c> separately on the SELECT side.
    /// </summary>
    private bool TryWrapNullableGroupKey(string columnRef, out string wrapped, out bool wasWrapped)
    {
        wasWrapped = false;
        wrapped = "";
        var (t, c) = SplitQualified(columnRef);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c) || !_catalog.ColumnExists(t, c))
            return false;
        var bare = _dialect.QuoteQualified(t, c);
        var col = _catalog.GetColumns(t).FirstOrDefault(ci =>
            string.Equals(ci.ColumnName, c, StringComparison.OrdinalIgnoreCase));
        if (col is null)
        {
            wrapped = bare;
            return true;
        }
        if (!col.IsNullable || !IsTextOrIdLike(col.DataType))
        {
            wrapped = bare;
            return true;
        }
        // Cast non-string ID-like types (uniqueidentifier / int) so the '(Unassigned)' literal is
        // type-compatible with the column expression for the COALESCE/ISNULL contract.
        var castInner = IsTextType(col.DataType) ? bare : _dialect.CastAsString(bare, 64);
        wrapped = _dialect.NullCoalesce(castInner, "'(Unassigned)'");
        wasWrapped = true;
        return true;
    }

    private static bool IsTextType(string dataType) =>
        dataType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("nchar", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("char", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("ntext", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextOrIdLike(string dataType) =>
        IsTextType(dataType) ||
        dataType.Equals("uniqueidentifier", StringComparison.OrdinalIgnoreCase) ||
        dataType.StartsWith("int", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("bigint", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("smallint", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("tinyint", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// C13 — true when the column ref resolves to a catalog column whose type is a raw
    /// millisecond-precision date/time. Used by auto-GROUP-BY inference to skip these
    /// columns: grouping by a raw datetime yields one bucket per row, not the per-period
    /// breakdown the user expects. The user (or LLM) must supply a Computed bucket alias
    /// (FORMAT(...) / CAST AS DATE / DATETRUNC) to get sensible time buckets.
    /// </summary>
    private bool IsRawDateTimeColumn(string columnRef)
    {
        var (t, c) = SplitQualified(columnRef);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(c)) return false;
        var col = _catalog.GetColumns(t).FirstOrDefault(ci =>
            string.Equals(ci.ColumnName, c, StringComparison.OrdinalIgnoreCase));
        if (col is null) return false;
        var dt = col.DataType;
        return dt.StartsWith("datetime", StringComparison.OrdinalIgnoreCase) ||
               dt.Equals("smalldatetime", StringComparison.OrdinalIgnoreCase) ||
               dt.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase) ||
               dt.Equals("time", StringComparison.OrdinalIgnoreCase);
    }

    private static object?[] ExtractValues(object? v)
    {
        if (v is null) return Array.Empty<object?>();
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(JsonElementToClr).ToArray();
            return new[] { JsonElementToClr(je) };
        }
        if (v is string s) return new object?[] { s };
        if (v is IEnumerable e and not string)
        {
            var list = new List<object?>();
            foreach (var item in e) list.Add(item);
            return list.ToArray();
        }
        return new[] { v };
    }

    private static object? JsonElementToClr(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je.GetRawText(),
    };
}
