namespace AnalystAgent.Pipeline;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Models;

/// <summary>
/// The lean per-question driver: a deterministic schema-metadata fast path, then the
/// <see cref="IDirectAnalystPath"/> (route already happened upstream; this grounds → generates SQL →
/// validates → executes → self-corrects → explains in ~2 LLM calls). If the analyst path can't
/// answer, we **abstain honestly** rather than guess.
///
/// <para>This replaced the old ~7-stage form-filling pipeline (QuerySpec IR + hand-written SQL
/// compiler + 23 repair rules + intent guard + coverage check). That machinery existed to prop up a
/// "weak" model — but the model writes correct T-SQL directly (proven live: 8/8 of the owner's real
/// questions, incl. above-average subqueries, window functions, multi-CTE, Arabic). Correctness now
/// comes from CONTROL (validate → self-correct → abstain), not from a symbolic crutch layer.</para>
/// </summary>
internal interface ISingleQuestionExecutor
{
    Task<AnalystResponse> ExecuteAsync(
        AnalystRequest request,
        string question,
        Stopwatch totalSw,
        BroadcastingStepList steps,
        CancellationToken cancellationToken);
}

internal sealed class SingleQuestionExecutor : ISingleQuestionExecutor
{
    private readonly Stages.IMetadataHandler _metadataHandler;
    private readonly IDirectAnalystPath _directPath;
    private readonly IResponsePersister _persister;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<SingleQuestionExecutor> _logger;

    public SingleQuestionExecutor(
        Stages.IMetadataHandler metadataHandler,
        IDirectAnalystPath directPath,
        IResponsePersister persister,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<SingleQuestionExecutor> logger)
    {
        _metadataHandler = metadataHandler;
        _directPath = directPath;
        _persister = persister;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    private CopilotTextCatalog Text => _textCatalog.CurrentValue;

    public async Task<AnalystResponse> ExecuteAsync(
        AnalystRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps,
        CancellationToken cancellationToken)
    {
        // Schema-metadata fast path — "what tables exist" / "columns of X" / "how are X and Y
        // related" run as deterministic INFORMATION_SCHEMA / sys.foreign_keys queries with no LLM.
        try
        {
            var metaStart = DateTime.UtcNow;
            var meta = await _metadataHandler.TryHandleAsync(question, cancellationToken);
            if (meta is not null)
            {
                var rows = meta.Result?.Rows ?? System.Array.Empty<IReadOnlyDictionary<string, object?>>();
                var rowText = rows.Count == 0 ? "No rows." : $"Returned {rows.Count} row(s).";
                steps.Add(new PipelineStep(
                    "Schema metadata", StageNames.StatusOk,
                    ElapsedMs: (long)(DateTime.UtcNow - metaStart).TotalMilliseconds, StartedAt: metaStart,
                    Detail: meta.Sql, Kind: "metadata"));
                // Persist through the shared path (like every other answer) so the metadata fast-path
                // lands a trace row — SQL + rows + steps — on the investigation page instead of
                // vanishing entirely. HostTraceSink already serializes StructuredRows when rows exist.
                return await _persister.PersistAsync(request, totalSw, steps,
                    reply: rowText,
                    sql: meta.Sql,
                    rowCount: rows.Count,
                    rows: rows,
                    cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[copilot.silent_failure][stage=MetadataHandler] failed; continuing");
        }

        // The analyst path: ground → generate grounded SQL → validate → execute → self-correct → explain.
        var direct = await _directPath.TryAnswerAsync(request, question, totalSw, steps, cancellationToken);
        if (direct is not null) return direct;

        // Abstain honestly — the model couldn't ground/answer this against the schema, and we do NOT
        // guess. (No more silent heavy-pipeline fallback; control over coverage.) Stamp Provenance /
        // Confidence like every other path so analytics can distinguish an abstain from a null-state.
        return (await _persister.PersistAsync(request, totalSw, steps,
            reply: Text.SpecExtractorFailed,
            error: "direct-analyst could not answer",
            cancellationToken: cancellationToken))
            with { Provenance = "abstain", Confidence = 0.0 };
    }
}
