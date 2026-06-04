namespace AnalystAgent.Abstractions;

using AnalystAgent.Models;

/// <summary>
/// Internal trace contract. After every pipeline run, the orchestrator calls
/// <see cref="RecordAsync"/>. The host adapter persists the trace to the existing
/// CopilotTraceHistories store so the existing investigation-tree UI picks it up.
/// </summary>
public interface ITraceSink
{
    /// <summary>
    /// Persist a single question outcome. Returns the trace id (if the store assigns one)
    /// or null if persistence was skipped/failed. <paramref name="steps"/> is the per-stage
    /// pipeline trace that surfaces in the existing investigation-tree step panel.
    /// </summary>
    Task<int?> RecordAsync(
        string question,
        string? sql,
        int? rowCount,
        long elapsedMs,
        string? error,
        string? reply = null,
        string? caseCode = null,
        string? sourceSuite = null,
        int? sessionId = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null,
        IReadOnlyList<PipelineStep>? steps = null,
        string? expectedSql = null,
        CancellationToken cancellationToken = default);
}
