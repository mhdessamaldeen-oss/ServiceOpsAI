namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;

/// <summary>
/// Owns the "finalize the response + write the trace row" step that both the orchestrator's
/// preflight/fast-path stages AND the single-question executor need to call. Extracted from
/// <c>CopilotOrchestrator.PersistAsync</c> on 2026-06-01 so the executor doesn't have to
/// duplicate it.
///
/// <para>Builds a <see cref="CopilotResponse"/> with the elapsed-time stamp + trace metadata,
/// then asynchronously persists the trace row via <see cref="ITraceSink"/>. Trace failures are
/// swallowed (non-fatal — the response still returns). Stamping happens once, here, so the
/// response field and the persisted row carry byte-identical <c>TotalElapsedMs</c>.</para>
/// </summary>
public interface IResponsePersister
{
    Task<CopilotResponse> PersistAsync(
        CopilotRequest request,
        Stopwatch totalSw,
        System.Collections.Generic.List<PipelineStep> steps,
        string reply,
        string? sql = null,
        int? rowCount = null,
        System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, object?>>? rows = null,
        string? error = null,
        string? chartType = null,
        System.Collections.Generic.IReadOnlyList<Abstractions.SemanticSearchHit>? similarEntities = null,
        System.Collections.Generic.IReadOnlyList<Abstractions.CopilotWarning>? warnings = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ResponsePersister : IResponsePersister
{
    private readonly ITraceSink _traceSink;
    private readonly ILogger<ResponsePersister> _logger;

    public ResponsePersister(ITraceSink traceSink, ILogger<ResponsePersister> logger)
    {
        _traceSink = traceSink;
        _logger = logger;
    }

    public async Task<CopilotResponse> PersistAsync(
        CopilotRequest request,
        Stopwatch totalSw,
        System.Collections.Generic.List<PipelineStep> steps,
        string reply,
        string? sql = null,
        int? rowCount = null,
        System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, object?>>? rows = null,
        string? error = null,
        string? chartType = null,
        System.Collections.Generic.IReadOnlyList<Abstractions.SemanticSearchHit>? similarEntities = null,
        System.Collections.Generic.IReadOnlyList<Abstractions.CopilotWarning>? warnings = null,
        CancellationToken cancellationToken = default)
    {
        totalSw.Stop();
        var totalElapsedMs = totalSw.ElapsedMilliseconds;
        var response = new CopilotResponse(
            Reply: reply,
            Sql: sql,
            RowCount: rowCount,
            Rows: rows,
            Trace: error is null ? StageNames.StatusOk : StageNames.StatusFailed,
            Error: error,
            Steps: steps,
            SimilarEntities: similarEntities,
            SuggestedChartType: chartType,
            Warnings: warnings,
            TotalElapsedMs: totalElapsedMs);
        try
        {
            var traceId = await _traceSink.RecordAsync(
                question: request.Question ?? "",
                sql: sql,
                rowCount: rowCount,
                elapsedMs: totalElapsedMs,
                error: error,
                reply: reply,
                caseCode: request.CaseCode,
                sourceSuite: request.SourceSuite,
                sessionId: int.TryParse(request.ConversationId, out var sid) ? sid : (int?)null,
                rows: rows,
                steps: steps,
                expectedSql: request.ExpectedSql,
                cancellationToken: cancellationToken);
            response = response with { TraceId = traceId };
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "[ResponsePersister] trace persist failed (non-fatal).");
        }
        return response;
    }
}
