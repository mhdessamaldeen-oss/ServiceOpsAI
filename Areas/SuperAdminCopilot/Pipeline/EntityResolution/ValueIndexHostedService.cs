namespace SuperAdminCopilot.Pipeline.EntityResolution;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hosted service that builds the <see cref="IValueIndex"/> snapshot on application
/// startup and rebuilds it periodically thereafter (interval configured via
/// <see cref="ValueIndexOptions.RebuildIntervalHours"/>; 0 = build once at startup,
/// never rebuild). Without this service, the index stays empty and
/// <see cref="IFuzzyEntityResolver"/> returns no matches.
///
/// <para><b>Startup behaviour:</b> the first build happens once host startup completes
/// (Task.Run on a background thread). The orchestrator can serve requests immediately —
/// the resolver returns empty results until the first build finishes, gracefully. No
/// request blocks on index initialisation.</para>
/// </summary>
internal sealed class ValueIndexHostedService : BackgroundService
{
    private readonly IValueIndex _index;
    private readonly IOptionsMonitor<ValueIndexOptions> _options;
    private readonly ILogger<ValueIndexHostedService> _logger;

    public ValueIndexHostedService(
        IValueIndex index,
        IOptionsMonitor<ValueIndexOptions> options,
        ILogger<ValueIndexHostedService> logger)
    {
        _index = index;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial build — fire as soon as the host has finished startup. Wrap in try/catch
        // so a failure here never crashes the host; the index simply stays empty and
        // entity resolution returns no matches (downstream code handles that gracefully).
        try
        {
            _logger.LogInformation("[ValueIndex] running initial build");
            await _index.RebuildAsync(stoppingToken);
            var stats = _index.GetStats();
            _logger.LogInformation(
                "[ValueIndex] initial build complete — {Entries} entries across {Columns} columns",
                stats.EntryCount, stats.IndexedColumns);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ValueIndex] initial build failed — index will remain empty until next scheduled rebuild");
        }

        // Periodic rebuilds — configurable; 0 = never.
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = _options.CurrentValue.RebuildIntervalHours;
            if (intervalHours <= 0)
            {
                // No periodic refresh requested. Sleep indefinitely until shutdown.
                try { await Task.Delay(Timeout.Infinite, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try { await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                _logger.LogInformation("[ValueIndex] scheduled rebuild ({Hours}h interval)", intervalHours);
                await _index.RebuildAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ValueIndex] scheduled rebuild failed; will retry on next interval");
            }
        }
    }
}
