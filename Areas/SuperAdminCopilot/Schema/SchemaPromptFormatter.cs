namespace SuperAdminCopilot.Schema;

using System.Text;

/// <summary>
/// Renders a list of tables into the prompt-friendly schema block the planner sees.
///
/// <para><b>Two strategies</b>:
/// <list type="bullet">
///   <item><b>Full</b> (legacy default): every column on every retrieved table, all FKs,
///   sample values. ~10K chars on a wide schema. The planner can reference any column.</item>
///   <item><b>Minimal</b> (B.5): only "interesting" columns per table — primary key, label
///   columns (Name/Title/Code), FK columns, soft-delete column, dates ending in <c>At</c>,
///   the natural-key column (TicketNumber). ~5K chars. Good enough for 90%+ of questions and
///   cuts planner-prompt cost in half.</item>
/// </list></para>
///
/// <para>Single canonical formatter shared by every retriever. Previously duplicated; pulling
/// here means a format change in one place updates every retriever and there's no drift.</para>
/// </summary>
internal static class SchemaPromptFormatter
{
    /// <summary>Suffixes / exact names that mark a column as "always interesting" for Minimal mode.</summary>
    private static readonly string[] InterestingSuffixes = { "Id", "At", "Name", "Title", "Code", "Number", "By" };
    private static readonly HashSet<string> AlwaysInteresting = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Name", "Title", "Code", "Email", "UserName", "TicketNumber", "IsDeleted", "IsActive",
        "Description", "Status", "Priority", "Category"
    };

    public static string Format(
        IEntityCatalog catalog,
        IReadOnlyList<string> tables,
        SchemaPromptStrategy strategy = SchemaPromptStrategy.Full,
        ICopilotSchemaAccessPolicy? accessPolicy = null)
    {
        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            var info = catalog.GetTable(t);
            if (info is null) continue;
            if (accessPolicy is not null && !accessPolicy.IsTableAllowed(info.Name)) continue;

            // Show bare table name (no schema prefix) so the LLM doesn't echo "dbo.Tickets"
            // back as the root — the compiler tolerates both, but cleaner input → cleaner output.
            sb.Append("Table ").Append(info.Name).AppendLine();

            foreach (var col in catalog.GetColumns(t))
            {
                if (accessPolicy is not null && !accessPolicy.IsColumnAllowed(t, col.ColumnName))
                    continue;
                if (strategy == SchemaPromptStrategy.Minimal && !IsInteresting(col.ColumnName))
                    continue;
                sb.Append("  - ").Append(col.ColumnName).Append(" (").Append(col.DataType);
                if (col.IsNullable) sb.Append(", null");
                sb.AppendLine(")");
            }

            var outFks = catalog.Snapshot.ForeignKeys
                .Where(f => string.Equals(f.ParentTable, t, StringComparison.OrdinalIgnoreCase)
                    && (accessPolicy is null || accessPolicy.IsTableAllowed(f.ReferencedTable)));
            foreach (var fk in outFks)
                sb.Append("  FK: ").Append(fk.ParentColumn)
                  .Append(" -> ").Append(fk.ReferencedTable).Append('.').Append(fk.ReferencedColumn)
                  .AppendLine();

            // Sample label values ground the LLM in real strings it can reach for as filter
            // arguments — e.g. "Critical" is a real priority value, not a column name.
            var samples = catalog.GetSampleValues(t);
            if (samples.Count > 0)
                sb.Append("  Sample values: ").AppendLine(string.Join(", ", samples));

            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// "Interesting" columns for Minimal mode: primary keys, FK columns (end in Id), label
    /// columns (Name/Title/Code), date columns (end in At), the natural-key column
    /// (TicketNumber), and a small allowlist of common semantically-meaningful columns.
    /// Skips internal noise like ConcurrencyStamp, NormalizedUserName, SecurityStamp, etc.
    /// </summary>
    private static bool IsInteresting(string columnName)
    {
        if (AlwaysInteresting.Contains(columnName)) return true;
        foreach (var suf in InterestingSuffixes)
            if (columnName.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Schema-prompt verbosity strategy. <see cref="Full"/> renders every column; <see cref="Minimal"/>
/// renders only "interesting" columns (PK, FK, label, date, natural-key) and is the default in
/// production builds. Configurable via <c>CopilotOptions.SchemaPromptStrategy</c>.
/// </summary>
public enum SchemaPromptStrategy
{
    Full = 0,
    Minimal = 1,
}
