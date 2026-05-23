using AISupportAnalysisPlatform.Enums;
using System.Collections.Concurrent;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Data;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// Singleton background queue that processes AI analysis requests ONE AT A TIME.
    /// Prevents local Docker models from being overwhelmed with concurrent requests.
    /// </summary>
    public class AiAnalysisQueueService : IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AiAnalysisQueueService> _logger;
        private readonly ConcurrentQueue<QueuedAnalysisItem> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _processingTask;

        // Real-time status tracking
        private readonly ConcurrentDictionary<int, TicketQueueStatus> _ticketStatuses = new();
        private BatchQueueProgress _batchProgress = new();
        private readonly object _progressLock = new();

        public AiAnalysisQueueService(IServiceScopeFactory scopeFactory, ILogger<AiAnalysisQueueService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        /// <summary>
        /// No longer requires a running local engine.
        /// </summary>
        public void Enqueue(int ticketId, string userId, bool isRefresh = true)
        {
            var item = new QueuedAnalysisItem
            {
                TicketId = ticketId,
                UserId = userId,
                IsRefresh = isRefresh,
                EnqueuedAt = DateTime.UtcNow
            };

            _ticketStatuses[ticketId] = new TicketQueueStatus
            {
                TicketId = ticketId,
                Status = AiAnalysisStatus.Queued,
                EnqueuedAt = DateTime.UtcNow
            };

            _queue.Enqueue(item);
            _signal.Release();
            _logger.LogInformation("Ticket {TicketId} enqueued for AI analysis.", ticketId);
        }

        /// <summary>
        /// Enqueue multiple tickets for sequential analysis (batch).
        /// </summary>
        public string EnqueueBatch(List<int> ticketIds, string userId)
        {
            var batchId = Guid.NewGuid().ToString("N")[..8];

            lock (_progressLock)
            {
                _batchProgress = new BatchQueueProgress
                {
                    BatchId = batchId,
                    TotalCount = ticketIds.Count,
                    CompletedCount = 0,
                    FailedCount = 0,
                    CurrentTicketId = null,
                    IsRunning = true,
                    StartedAt = DateTime.UtcNow
                };
            }

            foreach (var id in ticketIds)
            {
                Enqueue(id, userId, isRefresh: true);
            }

            _logger.LogInformation("Batch {BatchId}: {Count} tickets enqueued for sequential analysis.", batchId, ticketIds.Count);
            return batchId;
        }

        /// <summary>
        /// Get the current status of a specific ticket in the queue.
        /// </summary>
        public TicketQueueStatus? GetTicketStatus(int ticketId)
        {
            _ticketStatuses.TryGetValue(ticketId, out var status);
            return status;
        }

        /// <summary>
        /// Get the current batch progress.
        /// </summary>
        public BatchQueueProgress GetBatchProgress()
        {
            lock (_progressLock)
            {
                return new BatchQueueProgress
                {
                    BatchId = _batchProgress.BatchId,
                    TotalCount = _batchProgress.TotalCount,
                    CompletedCount = _batchProgress.CompletedCount,
                    FailedCount = _batchProgress.FailedCount,
                    CurrentTicketId = _batchProgress.CurrentTicketId,
                    IsRunning = _batchProgress.IsRunning,
                    StartedAt = _batchProgress.StartedAt
                };
            }
        }

        /// <summary>
        /// Get all ticket statuses (for UI polling).
        /// </summary>
        public Dictionary<int, TicketQueueStatus> GetAllStatuses()
        {
            return new Dictionary<int, TicketQueueStatus>(_ticketStatuses);
        }

        /// <summary>
        /// How many items are waiting in the queue.
        /// </summary>
        public int QueueLength => _queue.Count;

        /// <summary>
        /// Background loop: processes one item at a time.
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            _logger.LogInformation("AI Analysis Queue processor started.");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(ct);

                    if (!_queue.TryDequeue(out var item))
                        continue;

                    // Update status to Processing
                    _ticketStatuses[item.TicketId] = new TicketQueueStatus
                    {
                        TicketId = item.TicketId,
                        Status = AiAnalysisStatus.InProgress,
                        EnqueuedAt = item.EnqueuedAt,
                        StartedAt = DateTime.UtcNow
                    };

                    lock (_progressLock)
                    {
                        _batchProgress.CurrentTicketId = item.TicketId;
                    }

                    _logger.LogInformation("Processing ticket {TicketId} from queue. {Remaining} remaining.", item.TicketId, _queue.Count);

                    bool success = false;
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var analysisService = scope.ServiceProvider.GetRequiredService<IAiAnalysisService>();

                        TicketAiAnalysis result;
                        if (item.IsRefresh)
                            result = await analysisService.RefreshTicketAnalysisAsync(item.TicketId, item.UserId);
                        else
                            result = await analysisService.RunTicketAnalysisAsync(item.TicketId, item.UserId);

                        success = result.AnalysisStatus == AiAnalysisStatus.Success;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Queue processing failed for ticket {TicketId}: {Error}", item.TicketId, ex.Message);
                    }

                    // Update status
                    _ticketStatuses[item.TicketId] = new TicketQueueStatus
                    {
                        TicketId = item.TicketId,
                        Status = success ? AiAnalysisStatus.Success : AiAnalysisStatus.Failed,
                        EnqueuedAt = item.EnqueuedAt,
                        StartedAt = _ticketStatuses.TryGetValue(item.TicketId, out var prev) ? prev.StartedAt : null,
                        CompletedAt = DateTime.UtcNow
                    };

                    lock (_progressLock)
                    {
                        if (success)
                            _batchProgress.CompletedCount++;
                        else
                            _batchProgress.FailedCount++;

                        // Check if batch is done
                        if (_batchProgress.CompletedCount + _batchProgress.FailedCount >= _batchProgress.TotalCount)
                        {
                            _batchProgress.IsRunning = false;
                            _batchProgress.CurrentTicketId = null;
                        }
                    }

                    // Small delay between requests to let the local engine breathe
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in queue processor loop.");
                    await Task.Delay(2000, ct);
                }
            }

            _logger.LogInformation("AI Analysis Queue processor stopped.");
        }

        /// <summary>
        /// Instantly clears all pending items in the queue and terminates the current batch progress.
        /// </summary>
        public void StopBatchProcess()
        {
            _queue.Clear();

            // Adjust the signal count strictly
            while (_signal.Wait(0)) { } // Drain existing signals

            lock (_progressLock)
            {
                if (_batchProgress.IsRunning)
                {
                    _batchProgress.IsRunning = false;
                    _batchProgress.CurrentTicketId = null;
                }
            }

            // Mark all "Queued" statuses as "Stopped"
            var queuedItems = _ticketStatuses.Where(x => x.Value.Status == AiAnalysisStatus.Queued).ToList();
            foreach (var kvp in queuedItems)
            {
                kvp.Value.Status = AiAnalysisStatus.Stopped;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _signal.Dispose();
        }
    }

    // --- Data models for the queue ---

    public class QueuedAnalysisItem
    {
        public int TicketId { get; set; }
        public string UserId { get; set; } = "";
        public bool IsRefresh { get; set; }
        public DateTime EnqueuedAt { get; set; }
    }

    public class TicketQueueStatus
    {
        public int TicketId { get; set; }
        public AiAnalysisStatus Status { get; set; } = AiAnalysisStatus.Queued; 
        public DateTime EnqueuedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class BatchQueueProgress
    {
        public string BatchId { get; set; } = "";
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int? CurrentTicketId { get; set; }
        public bool IsRunning { get; set; }
        public DateTime? StartedAt { get; set; }
        public int SuccessCount => CompletedCount;
        public int ProcessedCount => CompletedCount + FailedCount;
        public double ProgressPercent => TotalCount == 0 ? 0 : Math.Round((double)ProcessedCount / TotalCount * 100, 1);
    }
}

