namespace SuperAdminCopilot.Pipeline.EntityResolution;

/// <summary>
/// In-memory index of (table, column, value) tuples drawn from the database's lookup
/// columns — district names, status labels, role names, service-type names, etc. The
/// <see cref="IFuzzyEntityResolver"/> queries this index when matching user phrases.
///
/// <para><b>Size bound:</b> only LOOKUP columns are indexed (small cardinality enums and
/// dimensions). Free-text columns (Ticket.Title, Customer.Description) are NOT indexed —
/// they're handled by semantic search elsewhere in the pipeline. Typical index size for
/// an enterprise schema: ~1000-5000 entries, well under 1 MB.</para>
///
/// <para><b>Refresh strategy:</b> the index is rebuilt by a background job that scans the
/// configured lookup columns (defined in <see cref="ValueIndexOptions"/>) once on startup
/// and then on a configurable schedule. The pipeline reads a snapshot — readers never see
/// a partial rebuild.</para>
///
/// <para><b>Staged, dormant.</b> Not registered in DI in this commit.</para>
/// </summary>
public interface IValueIndex
{
    /// <summary>Look up candidate matches for a surface form. Returns matches above
    /// <paramref name="minSimilarity"/>, sorted by descending similarity.</summary>
    IReadOnlyList<ValueIndexHit> Lookup(string surface, int topK, double minSimilarity);

    /// <summary>Rebuild the index from the underlying data source. Atomic — the previous
    /// snapshot stays serving readers until the new one is fully built, then a single
    /// reference swap promotes it.</summary>
    Task RebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>Snapshot statistics for the trace sink / admin dashboard.</summary>
    ValueIndexStats GetStats();
}

public sealed record ValueIndexHit(
    string Value,
    string Table,
    string Column,
    double Similarity);

public sealed record ValueIndexStats(
    int EntryCount,
    int IndexedColumns,
    DateTimeOffset LastRebuiltUtc);

/// <summary>Configuration bound to the <c>Ai:EntityResolution</c> section of
/// appsettings.json. Lists which (table, column) pairs to scan for lookup values.</summary>
public class ValueIndexOptions
{
    public const string SectionName = "Ai:EntityResolution";

    /// <summary>Whitelist of lookup columns to index. Empty = use the auto-discovery
    /// rule (any column referenced by a FK from another table) baked into the rebuild job.
    /// Explicit listing is recommended for production: it's faster, predictable, and
    /// prevents accidentally indexing sensitive columns.</summary>
    public List<LookupColumn> LookupColumns { get; set; } = new();

    /// <summary>Minimum similarity (0.0-1.0) for a fuzzy match to count. Default 0.75 —
    /// catches typos and small spelling variations without false-matching dissimilar values.</summary>
    public double MinSimilarity { get; set; } = 0.75;

    /// <summary>Max matches per surface phrase passed downstream. Default 3.</summary>
    public int TopK { get; set; } = 3;

    /// <summary>How often to rebuild the index in hours. 0 = never rebuild after startup.
    /// Default 24 (daily).</summary>
    public int RebuildIntervalHours { get; set; } = 24;
}

public sealed class LookupColumn
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
}
