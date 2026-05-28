namespace SuperAdminCopilot.Schema;

using QuikGraph;
using QuikGraph.Algorithms.Observers;
using QuikGraph.Algorithms.ShortestPath;

public sealed record FkEdge(string SourceTable, string TargetTable, ForeignKeyInfo Fk) : IEdge<string>
{
    public string Source => SourceTable;
    public string Target => TargetTable;
}

public interface IForeignKeyGraph
{
    IReadOnlyList<FkEdge>? FindPath(string fromTable, string toTable);
    IEnumerable<string> Neighbors(string table);
}

internal sealed class ForeignKeyGraph : IForeignKeyGraph
{
    private readonly BidirectionalGraph<string, FkEdge> _graph = new(allowParallelEdges: true);

    public ForeignKeyGraph(SchemaSnapshot snapshot)
    {
        foreach (var t in snapshot.Tables) _graph.AddVertex(t.Name);
        foreach (var fk in snapshot.ForeignKeys)
        {
            if (!_graph.ContainsVertex(fk.ParentTable)) _graph.AddVertex(fk.ParentTable);
            if (!_graph.ContainsVertex(fk.ReferencedTable)) _graph.AddVertex(fk.ReferencedTable);
            _graph.AddEdge(new FkEdge(fk.ParentTable, fk.ReferencedTable, fk));
            _graph.AddEdge(new FkEdge(fk.ReferencedTable, fk.ParentTable, fk));
        }
    }

    public IReadOnlyList<FkEdge>? FindPath(string fromTable, string toTable)
    {
        if (string.Equals(fromTable, toTable, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<FkEdge>();
        if (!_graph.ContainsVertex(fromTable) || !_graph.ContainsVertex(toTable))
            return null;

        // Edge weighting: pure shortest-path picks arbitrarily when several FK chains have
        // the same hop count (e.g. Customer→Ticket can go via Account, Subscription, or
        // Meter). The wrong pick still returns rows but with the wrong semantic relationship.
        // Heuristic: prefer FKs whose column name names the target entity. So
        // <c>Bill.CustomerId</c> (target = Customers) is cheaper than
        // <c>Bill.CreatedByUserId</c> for the same Bill→User hop.
        var dijkstra = new DijkstraShortestPathAlgorithm<string, FkEdge>(_graph, WeightFor);
        var predecessors = new VertexPredecessorRecorderObserver<string, FkEdge>();
        using (predecessors.Attach(dijkstra))
            dijkstra.Compute(fromTable);
        return predecessors.TryGetPath(toTable, out var path) ? path.ToList() : null;
    }

    /// <summary>
    /// Edge weight 1.0 base. Discounts the edge when the FK column name *contains* the target
    /// table's singular form (case-insensitive): the FK semantically points at the entity we're
    /// trying to reach, so it's the canonical join. Penalises FKs whose name doesn't, since
    /// those usually carry an unrelated role (e.g. <c>CreatedByUserId</c>).
    /// </summary>
    private static double WeightFor(FkEdge edge)
    {
        if (edge?.Fk is null) return 1.0;
        var col = edge.Fk.ParentColumn ?? string.Empty;
        var target = edge.TargetTable ?? string.Empty;
        if (string.IsNullOrEmpty(col) || string.IsNullOrEmpty(target)) return 1.0;
        var singular = SingularizeForMatch(target);                              // Customers → Customer
        if (col.Contains(singular, StringComparison.OrdinalIgnoreCase)) return 0.75; // semantically named
        return 1.25;                                                                 // role-named (CreatedBy, etc.)
    }

    private static string SingularizeForMatch(string table)
    {
        if (string.IsNullOrEmpty(table)) return table;
        if (table.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && table.Length > 3)
            return table[..^3] + "y";
        if (table.EndsWith("ses", StringComparison.OrdinalIgnoreCase)
            || table.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
            || table.EndsWith("ches", StringComparison.OrdinalIgnoreCase)
            || table.EndsWith("shes", StringComparison.OrdinalIgnoreCase))
            return table[..^2];
        if (table.EndsWith("s", StringComparison.OrdinalIgnoreCase) && table.Length > 1
            && !table.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return table[..^1];
        return table;
    }

    public IEnumerable<string> Neighbors(string table)
    {
        if (!_graph.ContainsVertex(table)) return Array.Empty<string>();
        return _graph.OutEdges(table).Select(e => e.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
