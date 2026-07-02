using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for offline operation queue and synchronization
    /// Handles queueing, retry logic, and sync status tracking
    /// </summary>
    public class OfflineQueueRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private bool _disposed;

        public OfflineQueueRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        #region Queue Operations

        /// <summary>
        /// Enqueue an offline operation
        /// </summary>
        /// <summary>Maximum number of pending operations allowed in the queue. Oldest items are pruned when exceeded.</summary>
        private const int MaxQueueSize = 500;

        public async Task<bool> EnqueueAsync(OfflineOperation operation)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Enforce queue depth cap — prune oldest pending items if at capacity
                    using (var countCmd = new SQLiteCommand("SELECT COUNT(*) FROM offline_queue WHERE sync_status = 'pending'", connection))
                    {
                        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                        if (count >= MaxQueueSize)
                        {
                            using (var pruneCmd = new SQLiteCommand(
                                "DELETE FROM offline_queue WHERE rowid IN (SELECT rowid FROM offline_queue WHERE sync_status = 'pending' ORDER BY created_at ASC LIMIT @pruneCount)", connection))
                            {
                                pruneCmd.Parameters.AddWithValue("@pruneCount", count - MaxQueueSize + 10); // prune 10 extra to avoid hitting cap every time
                                var pruned = await pruneCmd.ExecuteNonQueryAsync();
                                _logger?.LogWarning($"Offline queue at capacity ({count}/{MaxQueueSize}) — pruned {pruned} oldest operations");
                            }
                        }
                    }

                    var sql = @"
                        INSERT INTO offline_queue (
                            queue_id, refer_id, operation_type, operation_data,
                            created_at, max_retries, sync_status, priority
                        ) VALUES (
                            @queueId, @refer_id, @operationType, @operationData,
                            @createdAt, @maxRetries, @syncStatus, @priority
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@queueId", operation.QueueId);
                        command.Parameters.AddWithValue("@refer_id", operation.SessionId);
                        command.Parameters.AddWithValue("@operationType", operation.OperationType);
                        command.Parameters.AddWithValue("@operationData", operation.OperationData);
                        command.Parameters.AddWithValue("@createdAt", operation.CreatedAt.ToString("o"));
                        command.Parameters.AddWithValue("@maxRetries", operation.MaxRetries);
                        command.Parameters.AddWithValue("@syncStatus", operation.SyncStatus);
                        command.Parameters.AddWithValue("@priority", operation.Priority);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Offline operation enqueued: {operation.OperationType} (ID: {operation.QueueId})");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to enqueue operation: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get next pending operations for sync (ordered by priority and creation time)
        /// </summary>
        public async Task<List<OfflineOperation>> GetPendingOperationsAsync(int limit = 50)
        {
            var operations = new List<OfflineOperation>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, queue_id, refer_id, operation_type, operation_data,
                               created_at, retry_count, max_retries, last_retry_at,
                               last_error, sync_status, synced_at, priority
                        FROM offline_queue
                        WHERE sync_status = 'pending'
                          AND (max_retries = 0 OR retry_count < max_retries)
                          AND (
                              retry_count = 0
                              OR last_retry_at IS NULL
                              OR datetime(last_retry_at, '+' || CAST(MIN(retry_count * retry_count * 30, 3600) AS TEXT) || ' seconds') <= datetime('now')
                          )
                        ORDER BY priority ASC, created_at ASC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                operations.Add(ReadOperation(reader));
                            }
                        }
                    }
                }

                _logger?.LogDebug($"Retrieved {operations.Count} pending operations");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get pending operations: {ex.Message}", ex);
            }

            return operations;
        }

        /// <summary>
        /// Mark operation as in-progress
        /// </summary>
        public async Task<bool> MarkInProgressAsync(string queueId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE offline_queue
                        SET sync_status = 'in_progress',
                            last_retry_at = @retryTime
                        WHERE queue_id = @queueId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@retryTime", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@queueId", queueId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark operation in-progress: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Mark operation as completed
        /// </summary>
        public async Task<bool> MarkCompletedAsync(string queueId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE offline_queue
                        SET sync_status = 'completed',
                            synced_at = @syncedAt
                        WHERE queue_id = @queueId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@syncedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@queueId", queueId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Operation completed: {queueId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark operation completed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Mark operation as failed with retry increment
        /// </summary>
        public async Task<bool> MarkFailedAsync(string queueId, string errorMessage)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE offline_queue
                        SET sync_status = CASE
                                WHEN max_retries > 0 AND retry_count + 1 >= max_retries THEN 'failed'
                                ELSE 'pending'
                            END,
                            retry_count = retry_count + 1,
                            last_retry_at = @retryTime,
                            last_error = @errorMessage
                        WHERE queue_id = @queueId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@retryTime", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@errorMessage", errorMessage);
                        command.Parameters.AddWithValue("@queueId", queueId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogWarning($"Operation failed: {queueId} - {errorMessage}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark operation failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get queue summary statistics
        /// </summary>
        public async Task<QueueSummary> GetQueueSummaryAsync()
        {
            var summary = new QueueSummary();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT
                            sync_status,
                            COUNT(*) as item_count,
                            MIN(created_at) as oldest_item,
                            MAX(created_at) as newest_item,
                            SUM(CASE WHEN max_retries > 0 AND retry_count >= max_retries THEN 1 ELSE 0 END) as permanently_failed
                        FROM offline_queue
                        GROUP BY sync_status";

                    using (var command = new SQLiteCommand(sql, connection))
                    using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var status = reader.GetString(0);
                            var count = reader.GetInt32(1);

                            switch (status)
                            {
                                case "pending":
                                    summary.PendingCount = count;
                                    break;
                                case "in_progress":
                                    summary.InProgressCount = count;
                                    break;
                                case "completed":
                                    summary.CompletedCount = count;
                                    break;
                                case "failed":
                                    summary.FailedCount = count;
                                    summary.PermanentlyFailedCount = reader.GetInt32(4);
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get queue summary: {ex.Message}", ex);
            }

            return summary;
        }

        /// <summary>
        /// Update the status of a queue item
        /// Required for offline sync service to track operation state
        /// </summary>
        public async Task UpdateStatusAsync(long id, string status)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            UPDATE offline_queue
                            SET sync_status = @status,
                                last_retry_at = @lastRetry
                            WHERE id = @id";

                        command.Parameters.AddWithValue("@id", id);
                        command.Parameters.AddWithValue("@status", status);
                        command.Parameters.AddWithValue("@lastRetry", DateTime.UtcNow.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogDebug($"Updated queue item {id} status to '{status}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update status for queue item {id}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Increment the retry count for a queue item
        /// Used when sync attempt fails but should be retried
        /// </summary>
        public async Task IncrementRetryAsync(long id)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            UPDATE offline_queue
                            SET retry_count = retry_count + 1,
                                last_retry_at = @lastRetry
                            WHERE id = @id";

                        command.Parameters.AddWithValue("@id", id);
                        command.Parameters.AddWithValue("@lastRetry", DateTime.UtcNow.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogDebug($"Incremented retry count for queue item {id}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to increment retry for queue item {id}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Get all queue items (for diagnostics and testing)
        /// </summary>
        public async Task<List<OfflineOperation>> GetAllItemsAsync()
        {
            var items = new List<OfflineOperation>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            SELECT id, queue_id, refer_id, operation_type, operation_data,
                                   created_at, retry_count, max_retries, last_retry_at,
                                   last_error, sync_status, synced_at, priority
                            FROM offline_queue
                            ORDER BY created_at ASC";

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                items.Add(ReadOperation(reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get all queue items: {ex.Message}", ex);
            }

            return items;
        }

        /// <summary>
        /// Get pending queue items (alias for GetPendingOperationsAsync for testing compatibility)
        /// </summary>
        public async Task<List<OfflineOperation>> GetPendingItemsAsync(int limit = 1000)
        {
            return await GetPendingOperationsAsync(limit);
        }

        /// <summary>
        /// Simplified enqueue method for testing (takes individual parameters instead of OfflineOperation object)
        /// </summary>
        public async Task<long> EnqueueAsync(string operationType, string payload, string endpoint, string httpMethod)
        {
            var operation = new OfflineOperation
            {
                QueueId = Guid.NewGuid().ToString(),
                SessionId = "test-session", // Default session for simple enqueue
                OperationType = operationType,
                OperationData = payload,
                CreatedAt = DateTime.UtcNow,
                RetryCount = 0,
                MaxRetries = OfflineOperation.DefaultMaxRetries,
                SyncStatus = "pending",
                Priority = 5
            };

            await EnqueueAsync(operation);

            // Return the ID so tests can reference it
            // Need to query to get the auto-incremented ID
            using (var connection = new SQLiteConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT id FROM offline_queue
                        WHERE queue_id = @queueId";

                    command.Parameters.AddWithValue("@queueId", operation.QueueId);

                    var result = await command.ExecuteScalarAsync();
                    return result != null ? Convert.ToInt64(result) : 0;
                }
            }
        }

        #endregion

        #region Sync Logging

        /// <summary>
        /// Start a sync operation
        /// </summary>
        public async Task<SyncLog> StartSyncAsync(string sessionId, string syncDirection, int itemsQueued)
        {
            try
            {
                var syncLog = new SyncLog
                {
                    SyncId = Guid.NewGuid().ToString(),
                    SessionId = sessionId,
                    StartedAt = DateTime.UtcNow,
                    SyncDirection = syncDirection,
                    ItemsQueued = itemsQueued,
                    Success = false
                };

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO sync_log (
                            sync_id, refer_id, started_at, sync_direction, items_queued, success
                        ) VALUES (
                            @syncId, @sessionId, @startedAt, @syncDirection, @itemsQueued, 0
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@syncId", syncLog.SyncId);
                        command.Parameters.AddWithValue("@sessionId", syncLog.SessionId);
                        command.Parameters.AddWithValue("@startedAt", syncLog.StartedAt.ToString("o"));
                        command.Parameters.AddWithValue("@syncDirection", syncLog.SyncDirection);
                        command.Parameters.AddWithValue("@itemsQueued", syncLog.ItemsQueued);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Sync started: {syncLog.SyncId} ({syncDirection}, {itemsQueued} items)");
                return syncLog;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to start sync: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Complete a sync operation
        /// </summary>
        public async Task<bool> CompleteSyncAsync(
            string syncId,
            int itemsSynced,
            int itemsFailed,
            bool success,
            string errorMessage = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE sync_log
                        SET completed_at = @completedAt,
                            items_synced = @itemsSynced,
                            items_failed = @itemsFailed,
                            success = @success,
                            error_message = @errorMessage
                        WHERE sync_id = @syncId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@completedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@itemsSynced", itemsSynced);
                        command.Parameters.AddWithValue("@itemsFailed", itemsFailed);
                        command.Parameters.AddWithValue("@success", success ? 1 : 0);
                        command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@syncId", syncId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Sync completed: {syncId} (Synced: {itemsSynced}, Failed: {itemsFailed})");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to complete sync: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region Retry & Cleanup

        /// <summary>
        /// Reset all failed operations back to pending for retry
        /// </summary>
        public async Task<int> ResetFailedToRetryAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE offline_queue
                        SET sync_status = 'pending',
                            retry_count = 0,
                            last_error = NULL,
                            last_retry_at = NULL
                        WHERE sync_status = 'failed'";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var reset = await command.ExecuteNonQueryAsync();

                        if (reset > 0)
                        {
                            _logger?.LogInfo($"Reset {reset} failed operations back to pending for retry");
                        }

                        return reset;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to reset failed operations: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Delete all completed operations
        /// </summary>
        public async Task<int> DeleteCompletedAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "DELETE FROM offline_queue WHERE sync_status = 'completed'";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var deleted = await command.ExecuteNonQueryAsync();

                        if (deleted > 0)
                        {
                            _logger?.LogInfo($"Deleted {deleted} completed operations");
                        }

                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete completed operations: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Delete all operations from the queue
        /// </summary>
        public async Task<int> ClearAllAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "DELETE FROM offline_queue";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var deleted = await command.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Cleared all {deleted} operations from offline queue");
                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to clear offline queue: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Delete completed operations older than retention period
        /// </summary>
        /// <summary>
        /// Removes stale pending/failed operations older than the specified age.
        /// Prevents indefinite retry of operations for crashed/ended sessions.
        /// </summary>
        public async Task<int> CleanupStaleOperationsAsync(TimeSpan maxAge)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("o");
                    var sql = @"
                        DELETE FROM offline_queue
                        WHERE sync_status IN ('pending', 'failed')
                        AND datetime(created_at) < datetime(@cutoff)";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@cutoff", cutoff);
                        var deleted = await cmd.ExecuteNonQueryAsync();
                        if (deleted > 0)
                            _logger?.LogInfo($"Cleaned up {deleted} stale offline operation(s) older than {maxAge.TotalHours}h");
                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to cleanup stale operations: {ex.Message}");
                return 0;
            }
        }

        public async Task<int> CleanupCompletedOperationsAsync(TimeSpan retentionPeriod)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var cutoffDate = DateTime.UtcNow.Subtract(retentionPeriod);

                    var sql = @"
                        DELETE FROM offline_queue
                        WHERE sync_status = 'completed'
                          AND datetime(synced_at) < @cutoffDate";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@cutoffDate", cutoffDate.ToString("o"));

                        var deleted = await command.ExecuteNonQueryAsync();

                        if (deleted > 0)
                        {
                            _logger?.LogInfo($"Cleaned up {deleted} completed operations (older than {retentionPeriod.TotalDays} days)");
                        }

                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup completed operations: {ex.Message}", ex);
                return 0;
            }
        }

        #endregion

        #region Helper Methods

        private OfflineOperation ReadOperation(SQLiteDataReader reader)
        {
            return new OfflineOperation
            {
                Id = reader.GetInt32(0),
                QueueId = reader.GetString(1),
                SessionId = reader.GetString(2),
                OperationType = reader.GetString(3),
                OperationData = reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                RetryCount = reader.GetInt32(6),
                MaxRetries = reader.GetInt32(7),
                LastRetryAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
                LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
                SyncStatus = reader.GetString(10),
                SyncedAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                Priority = reader.GetInt32(12)
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // OfflineQueueRepository doesn't hold unmanaged resources, but implements IDisposable for consistency
        }

        #endregion
    }

    #region Data Models

    /// <summary>
    /// Offline operation model
    /// </summary>
    public class OfflineOperation
    {
        public int Id { get; set; }
        public string QueueId { get; set; }
        public string SessionId { get; set; }
        public string OperationType { get; set; }
        public string OperationData { get; set; } // JSON blob
        public DateTime CreatedAt { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; }
        public DateTime? LastRetryAt { get; set; }
        public string LastError { get; set; }
        public string SyncStatus { get; set; } // pending, in_progress, completed, failed
        public DateTime? SyncedAt { get; set; }
        public int Priority { get; set; } // 1=highest, 10=lowest

        public bool CanRetry => (MaxRetries == 0 || RetryCount < MaxRetries) && SyncStatus != "completed";
        public TimeSpan Age => DateTime.UtcNow - CreatedAt;

        /// <summary>Default max retries for offline queue operations (~17h with exponential backoff, capped at 1h intervals).</summary>
        public const int DefaultMaxRetries = 25;

        /// <summary>Higher retry limit for critical operations like session sync (~43h with exponential backoff).</summary>
        public const int CriticalMaxRetries = 50;

        public static OfflineOperation Create(string sessionId, string operationType, string operationData, int priority = 5, int? maxRetries = null)
        {
            return new OfflineOperation
            {
                QueueId = Guid.NewGuid().ToString(),
                SessionId = sessionId,
                OperationType = operationType,
                OperationData = operationData,
                CreatedAt = DateTime.UtcNow,
                RetryCount = 0,
                MaxRetries = maxRetries ?? DefaultMaxRetries,
                SyncStatus = "pending",
                Priority = priority
            };
        }
    }

    /// <summary>
    /// Sync log model
    /// </summary>
    public class SyncLog
    {
        public int Id { get; set; }
        public string SyncId { get; set; }
        public string SessionId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string SyncDirection { get; set; } // upload, download, bidirectional
        public int ItemsQueued { get; set; }
        public int ItemsSynced { get; set; }
        public int ItemsFailed { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
        public double SuccessRate => ItemsQueued > 0 ? (double)ItemsSynced / ItemsQueued * 100 : 0;
    }

    /// <summary>
    /// Queue summary statistics
    /// </summary>
    public class QueueSummary
    {
        public int PendingCount { get; set; }
        public int InProgressCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public int PermanentlyFailedCount { get; set; }

        public int TotalCount => PendingCount + InProgressCount + CompletedCount + FailedCount;
        public bool IsHealthy => PermanentlyFailedCount == 0 && InProgressCount < 10;

        public override string ToString()
        {
            return $"Queue: Pending={PendingCount}, InProgress={InProgressCount}, Completed={CompletedCount}, Failed={FailedCount}";
        }
    }

    #endregion
}
