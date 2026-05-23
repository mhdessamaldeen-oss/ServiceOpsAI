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

        var dijkstra = new DijkstraShortestPathAlgorithm<string, FkEdge>(_graph, _ => 1.0);
        var predecessors = new VertexPredecessorRecorderObserver<string, FkEdge>();
        using (predecessors.Attach(dijkstra))
            dijkstra.Compute(fromTable);
        return predecessors.TryGetPath(toTable, out var path) ? path.ToList() : null;
    }

    public IEnumerable<string> Neighbors(string table)
    {
        if (!_graph.ContainsVertex(table)) return Array.Empty<string>();
        return _graph.OutEdges(table).Select(e => e.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
