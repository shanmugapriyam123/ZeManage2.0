using BIManage.Core.Protection.Models;
using BIManage.Infrastructure.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Asynchronous audit entry queue with background worker
    /// Prevents UI blocking during audit logging
    /// Thread-safe with graceful shutdown
    /// </summary>
    public class AsyncAuditQueue : IDisposable
    {
        private readonly AuditRepository _repository;
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<ProtectionAuditEntry> _queue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _backgroundTask;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        // Performance metrics
        private int _totalEnqueued;
        private int _totalProcessed;
        private int _totalFailed;

        public AsyncAuditQueue(AuditRepository repository, ILogger logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _queue = new ConcurrentQueue<ProtectionAuditEntry>();
            _cancellationTokenSource = new CancellationTokenSource();
            _semaphore = new SemaphoreSlim(0);
            _disposed = false;

            // Start background worker
            _backgroundTask = Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));

            _logger.LogInfo("AsyncAuditQueue started");
        }

        /// <summary>
        /// Enqueue an audit entry for asynchronous processing
        /// Non-blocking, returns immediately
        /// </summary>
        public void Enqueue(ProtectionAuditEntry entry)
        {
            if (_disposed)
            {
                _logger?.LogWarning("Attempted to enqueue to disposed AsyncAuditQueue");
                return;
            }

            if (entry == null)
            {
                _logger?.LogWarning("Attempted to enqueue null audit entry");
                return;
            }

            _queue.Enqueue(entry);
            Interlocked.Increment(ref _totalEnqueued);
            _semaphore.Release(); // Signal worker thread

            _logger?.LogDebug($"Audit entry enqueued (queue depth: {_queue.Count})");
        }

        /// <summary>
        /// Background worker that processes queued audit entries
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            _logger?.LogInfo("Audit queue worker started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for signal or cancellation
                    await _semaphore.WaitAsync(cancellationToken);

                    // Process all available entries
                    while (_queue.TryDequeue(out var entry))
                    {
                        try
                        {
                            var auditLogId = await _repository.SaveAuditEntryAsync(entry);

                            if (!string.IsNullOrEmpty(auditLogId))
                            {
                                Interlocked.Increment(ref _totalProcessed);
                                _logger?.LogDebug($"Audit entry saved: {auditLogId} (Protection: {entry.ProtectionId}, User: {entry.UserName})");
                            }
                            else
                            {
                                Interlocked.Increment(ref _totalFailed);
                                _logger?.LogError($"Failed to save audit entry (Protection: {entry.ProtectionId})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _totalFailed);
                            _logger?.LogError($"Exception saving audit entry: {ex.Message}", ex);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Unexpected error in audit queue worker: {ex.Message}", ex);
                }
            }

            _logger?.LogInfo($"Audit queue worker stopped (Processed: {_totalProcessed}, Failed: {_totalFailed})");
        }

        /// <summary>
        /// Get current queue statistics
        /// </summary>
        public AuditQueueStats GetStats()
        {
            return new AuditQueueStats
            {
                QueueDepth = _queue.Count,
                TotalEnqueued = _totalEnqueued,
                TotalProcessed = _totalProcessed,
                TotalFailed = _totalFailed,
                SuccessRate = _totalEnqueued > 0
                    ? (double)_totalProcessed / _totalEnqueued * 100
                    : 0
            };
        }

        /// <summary>
        /// Flush all pending entries synchronously (for shutdown)
        /// </summary>
        public async Task FlushAsync(TimeSpan timeout)
        {
            var startTime = DateTime.Now;
            var remaining = _queue.Count;

            _logger?.LogInfo($"Flushing audit queue ({remaining} entries)...");

            while (_queue.Count > 0 && (DateTime.Now - startTime) < timeout)
            {
                await Task.Delay(100);
            }

            if (_queue.Count > 0)
            {
                _logger?.LogWarning($"Flush timeout: {_queue.Count} entries remaining");
            }
            else
            {
                _logger?.LogInfo("Audit queue flushed successfully");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _logger?.LogInfo("Disposing AsyncAuditQueue...");

            try
            {
                // Signal shutdown
                _cancellationTokenSource.Cancel();

                // Wait for background task with timeout
                if (!_backgroundTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    _logger?.LogWarning("Background task did not complete within timeout");
                }

                // Flush remaining entries synchronously (synchronous poll to avoid async deadlock)
                var flushStart = DateTime.Now;
                var flushRemaining = _queue.Count;
                if (flushRemaining > 0)
                {
                    _logger?.LogInfo($"Flushing audit queue ({flushRemaining} entries)...");
                    while (_queue.Count > 0 && (DateTime.Now - flushStart) < TimeSpan.FromSeconds(3))
                    {
                        Thread.Sleep(100);
                    }
                    if (_queue.Count > 0)
                        _logger?.LogWarning($"Flush timeout: {_queue.Count} entries remaining");
                    else
                        _logger?.LogInfo("Audit queue flushed successfully");
                }

                _semaphore?.Dispose();
                _cancellationTokenSource?.Dispose();
                _backgroundTask?.Dispose();

                _disposed = true;
                _logger?.LogInfo("AsyncAuditQueue disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error disposing AsyncAuditQueue: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Statistics for async audit queue performance monitoring
    /// </summary>
    public class AuditQueueStats
    {
        public int QueueDepth { get; set; }
        public int TotalEnqueued { get; set; }
        public int TotalProcessed { get; set; }
        public int TotalFailed { get; set; }
        public double SuccessRate { get; set; }

        public override string ToString()
        {
            return $"Queue: {QueueDepth} | Enqueued: {TotalEnqueued} | Processed: {TotalProcessed} | Failed: {TotalFailed} | Success: {SuccessRate:F1}%";
        }
    }
}
