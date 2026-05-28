namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using SuperAdminCopilot.Models;

/// <summary>
/// When the spec has a Limit (TOP N) but no OrderBy, the result set is nondeterministic — SQL
/// Server returns whichever rows the optimiser picked. Inject a stable order using:
/// <list type="number">
///   <item>The root entity's "default" date column (semantic-layer), DESC for "newest"-style
///         intent OR when there's no other signal.</item>
///   <item>Otherwise the root primary key (typically <c>Id</c>) DESC.</item>
/// </list>
///
/// <para>Conservative: if neither candidate column exists, leave the spec alone — the user can
/// still get a result, just nondeterministic. Doesn't override an existing OrderBy.</para>
/// </summary>
internal sealed class EnsureOrderByForTopNPhase : ISpecRepairPhase
{
    public string Name => "EnsureOrderByForTopN";
    public string Covers => "TOP N without ORDER BY → inject a stable default order (date DESC or Id DESC)";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        if (!(spec.Limit is > 0)) return;
        if (spec.OrderBy.Count > 0) return;
        if (string.IsNullOrEmpty(spec.Root) || !ctx.Catalog.TableExists(spec.Root)) return;

        // Prefer the default date column.
        var dateCol = ctx.SemanticLayer.GetDateColumn(spec.Root, role: null);
        if (!string.IsNullOrEmpty(dateCol) && ctx.Catalog.ColumnExists(spec.Root, dateCol!))
        {
            spec.OrderBy.Add(new OrderBySpec { Column = $"{spec.Root}.{dateCol}", Direction = "desc" });
            ctx.Diagnostics.Add(new(Name, $"injected ORDER BY {spec.Root}.{dateCol} DESC for deterministic TOP {spec.Limit}"));
            return;
        }

        // Fall back to primary key.
        if (ctx.Catalog.ColumnExists(spec.Root, "Id"))
        {
            spec.OrderBy.Add(new OrderBySpec { Column = $"{spec.Root}.Id", Direction = "desc" });
            ctx.Diagnostics.Add(new(Name, $"injected ORDER BY {spec.Root}.Id DESC for deterministic TOP {spec.Limit}"));
        }
    }
}
