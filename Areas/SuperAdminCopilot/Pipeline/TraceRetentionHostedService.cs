namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using ServiceOpsAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

/// <summary>
/// P1 #80 — Background hosted service that prunes <c>CopilotTraceHistories</c> rows older than
/// <see cref="CopilotOptions.TraceRetentionDays"/>. Without this the trace table grows
/// unbounded; at 1000 questions/day with ~6 KB embedding per row that's 2 GB/year just for
/// vectors. The job sleeps <see cref="CopilotOptions.TracePruneIntervalHours"/> between cycles.
///
/// <para>Set <c>TraceRetentionDays = 0</c> to disable pruning (dev / forensic mode). The
/// service still starts but skips every cycle so it doesn't burn CPU.</para>
///
/// <para>Why a background service instead of a SQL Agent job: this is a portable copilot
/// module shipped as part of the host. Asking ops to set up SQL Agent jobs per environment
/// creates drift; a single in-process scheduler is consistent across local / staging / prod.</para>
/// </summary>
internal sealed class TraceRetentionHostedService : BackgroundService
{
    private readonly IServiceProvider _root;
    private readonly IOptionsMonitor<CopilotOptions> _options;
    private readonly ILogger<TraceRetentionHostedService> _logger;

    public TraceRetentionHostedService(
        IServiceProvider root,
        IOptionsMonitor<CopilotOptions> options,
        ILogger<TraceRetentionHostedService> logger)
    {
        _root = root;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stagger start so we don't compete with warmup. 60s after boot is plenty.
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var cycleSw = Stopwatch.StartNew();
                try
                {
                    await PruneOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[TraceRetention] prune cycle failed (non-fatal) after {ElapsedMs}ms.",
                        cycleSw.ElapsedMilliseconds);
                }

                // H6 backpressure: measure interval from cycle COMPLETION so a slow delete
                // doesn't cause the next cycle to fire immediately. The delay is always the
                // configured interval minus the time already spent (floored at 60s minimum).
                cycleSw.Stop();
                var intervalHours = Math.Max(1, _options.CurrentValue.TracePruneIntervalHours);
                var remaining = TimeSpan.FromHours(intervalHours) - cycleSw.Elapsed;
                if (remaining < TimeSpan.FromSeconds(60)) remaining = TimeSpan.FromSeconds(60);
                await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[TraceRetention] Service is shutting down, cancellation requested.");
        }
    }

    private async Task PruneOnceAsync(CancellationToken cancellationToken)
    {
        var retentionDays = _options.CurrentValue.TraceRetentionDays;
        if (retentionDays <= 0)
        {
            _logger.LogDebug("[TraceRetention] disabled (TraceRetentionDays = 0); skipping cycle.");
            return;
        }

        using var scope = _root.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        // ExecuteDeleteAsync (EF 7+) issues a single DELETE without materialising rows. Logged
        // affected-row count goes to ops dashboard. Bounded by retention window so we never
        // delete recent traces even if cutoff math is wrong.
        var deleteSw = Stopwatch.StartNew();
        var deleted = await ctx.Set<ServiceOpsAI.Models.AI.CopilotTraceHistory>()
            .Where(t => t.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        deleteSw.Stop();

        if (deleted > 0)
            _logger.LogInformation(
                "[TraceRetention] pruned {Count} trace row(s) older than {Cutoff:yyyy-MM-dd} " +
                "({Days}-day retention) in {ElapsedMs}ms.",
                deleted, cutoff, retentionDays, deleteSw.ElapsedMilliseconds);
        else
            _logger.LogDebug("[TraceRetention] no rows older than {Cutoff:yyyy-MM-dd}; nothing to prune.", cutoff);
    }
}
