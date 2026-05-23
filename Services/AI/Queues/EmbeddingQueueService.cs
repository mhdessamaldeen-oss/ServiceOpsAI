using AISupportAnalysisPlatform.Models;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Singleton service that tracks embedding batch progress and supports cancellation.
    /// Registered as a singleton so the controller and background task share the same state.
    /// </summary>
    public class EmbeddingQueueService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EmbeddingQueueService> _logger;
        private readonly object _lock = new();
        private EmbeddingProgress _progress = new();
        private CancellationTokenSource? _cts;

        public EmbeddingQueueService(IServiceScopeFactory scopeFactory, ILogger<EmbeddingQueueService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// Starts a background embedding batch for the given ticket IDs.
        /// Returns false if a batch is already running.
        /// </summary>
        public bool StartBatch(List<int> ticketIds)
        {
            var normalizedTicketIds = ticketIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            lock (_lock)
            {
                if (_progress.IsRunning)
                    return false;

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _progress = new EmbeddingProgress
                {
                    TotalCount = normalizedTicketIds.Count,
                    IsRunning = true
                };
            }

            var ct = _cts!.Token;

            _ = Task.Run(async () =>
            {
                _logger.LogInformation("Embedding batch started for {Count} tickets.", normalizedTicketIds.Count);

                using var scope = _scopeFactory.CreateScope();
                var semanticService = scope.ServiceProvider.GetRequiredService<ISemanticSearchService>();

                foreach (var id in normalizedTicketIds)
                {
                    if (ct.IsCancellationRequested) break;

                    lock (_lock) { _progress.CurrentTicketId = id; }

                    try
                    {
                        await semanticService.UpsertTicketEmbeddingAsync(id);
                        lock (_lock) { _progress.CompletedCount++; }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Embedding failed for ticket {TicketId}.", id);
                        lock (_lock) 
                        { 
                            _progress.FailedCount++; 
                            _progress.LastErrorMessage = ex.Message;
                        }

                        // CRITICAL: If the provider is misconfigured or model is unsupported,
                        // stop the entire batch instead of failing 100+ times.
                        if (ex is InvalidOperationException)
                        {
                            _logger.LogError("Critical AI configuration error encountered. Stopping batch.");
                            break; 
                        }
                    }
                }

                lock (_lock)
                {
                    _progress.IsRunning = false;
                    _progress.CurrentTicketId = null;
                }

                _logger.LogInformation("Embedding batch finished. Completed: {C}, Failed: {F}",
                    _progress.CompletedCount, _progress.FailedCount);
            });

            return true;
        }

        /// <summary>
        /// Returns a snapshot of the current embedding progress.
        /// </summary>
        public EmbeddingProgress GetProgress()
        {
            lock (_lock)
            {
                return new EmbeddingProgress
                {
                    TotalCount = _progress.TotalCount,
                    CompletedCount = _progress.CompletedCount,
                    FailedCount = _progress.FailedCount,
                    CurrentTicketId = _progress.CurrentTicketId,
                    IsRunning = _progress.IsRunning,
                    LastErrorMessage = _progress.LastErrorMessage
                };
            }
        }

        /// <summary>
        /// Cancels the running embedding batch.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _progress.IsRunning = false;
            }
            _logger.LogInformation("Embedding batch stopped by user.");
        }
    }
}
