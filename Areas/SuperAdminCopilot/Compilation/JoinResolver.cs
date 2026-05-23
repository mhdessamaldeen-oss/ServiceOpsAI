namespace SuperAdminCopilot.Compilation;

using SuperAdminCopilot.Schema;

internal sealed class JoinResolver
{
    private readonly IEntityCatalog _catalog;

    public JoinResolver(IEntityCatalog catalog) => _catalog = catalog;

    public IReadOnlyList<FkEdge> Resolve(string rootTable, IEnumerable<string> referencedTables)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootTable };
        var ordered = new List<FkEdge>();

        foreach (var t in referencedTables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (visited.Contains(t)) continue;
            if (!_catalog.TableExists(t)) continue;
            var path = _catalog.Graph.FindPath(rootTable, t);
            if (path is null) continue;
            foreach (var edge in path)
            {
                if (visited.Contains(edge.TargetTable)) continue;
                ordered.Add(edge);
                visited.Add(edge.TargetTable);
            }
        }
        return ordered;
    }
}
