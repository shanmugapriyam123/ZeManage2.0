using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for Revit event logging and analytics
    /// Provides comprehensive event tracking with aggregation support
    /// </summary>
    public class EventRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private bool _disposed;

        public EventRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        #region Event Logging

        /// <summary>
        /// Log a Revit event
        /// </summary>
        public async Task<bool> LogEventAsync(RevitEvent evt)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO event_log (
                            session_id, document_id, timestamp, event_type, event_category,
                            event_name, element_id, element_type, transaction_name,
                            success, error_message, metadata, duration_ms
                        ) VALUES (
                            @sessionId, @documentId, @timestamp, @eventType, @eventCategory,
                            @eventName, @elementId, @elementType, @transactionName,
                            @success, @errorMessage, @metadata, @durationMs
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", evt.SessionId);
                        command.Parameters.AddWithValue("@documentId", evt.DocumentId ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@timestamp", evt.Timestamp.ToString("o"));
                        command.Parameters.AddWithValue("@eventType", evt.EventType);
                        command.Parameters.AddWithValue("@eventCategory", evt.EventCategory);
                        command.Parameters.AddWithValue("@eventName", evt.EventName);
                        command.Parameters.AddWithValue("@elementId", evt.ElementId ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@elementType", evt.ElementType ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@transactionName", evt.TransactionName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@success", evt.Success ? 1 : 0);
                        command.Parameters.AddWithValue("@errorMessage", evt.ErrorMessage ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@metadata", evt.Metadata ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@durationMs", evt.DurationMs ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log event: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Log multiple events in batch (performance optimization)
        /// </summary>
        public async Task<int> LogEventsBatchAsync(List<RevitEvent> events)
        {
            if (events == null || events.Count == 0)
                return 0;

            int successCount = 0;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var transaction = connection.BeginTransaction())
                    {
                        var sql = @"
                            INSERT INTO event_log (
                                session_id, document_id, timestamp, event_type, event_category,
                                event_name, element_id, element_type, transaction_name,
                                success, error_message, metadata, duration_ms
                            ) VALUES (
                                @sessionId, @documentId, @timestamp, @eventType, @eventCategory,
                                @eventName, @elementId, @elementType, @transactionName,
                                @success, @errorMessage, @metadata, @durationMs
                            )";

                        using (var command = new SQLiteCommand(sql, connection))
                        {
                            foreach (var evt in events)
                            {
                                command.Parameters.Clear();
                                command.Parameters.AddWithValue("@sessionId", evt.SessionId);
                                command.Parameters.AddWithValue("@documentId", evt.DocumentId ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@timestamp", evt.Timestamp.ToString("o"));
                                command.Parameters.AddWithValue("@eventType", evt.EventType);
                                command.Parameters.AddWithValue("@eventCategory", evt.EventCategory);
                                command.Parameters.AddWithValue("@eventName", evt.EventName);
                                command.Parameters.AddWithValue("@elementId", evt.ElementId ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@elementType", evt.ElementType ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@transactionName", evt.TransactionName ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@success", evt.Success ? 1 : 0);
                                command.Parameters.AddWithValue("@errorMessage", evt.ErrorMessage ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@metadata", evt.Metadata ?? (object)DBNull.Value);
                                command.Parameters.AddWithValue("@durationMs", evt.DurationMs ?? (object)DBNull.Value);

                                await command.ExecuteNonQueryAsync();
                                successCount++;
                            }
                        }

                        transaction.Commit();
                    }
                }

                _logger?.LogInfo($"Logged {successCount} events in batch");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log event batch: {ex.Message}", ex);
            }

            return successCount;
        }

        /// <summary>
        /// Get recent events for session
        /// </summary>
        public async Task<List<RevitEvent>> GetRecentEventsAsync(string sessionId, int limit = 100)
        {
            var events = new List<RevitEvent>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, session_id, document_id, timestamp, event_type, event_category,
                               event_name, element_id, element_type, transaction_name,
                               success, error_message, metadata, duration_ms
                        FROM event_log
                        WHERE session_id = @sessionId
                        ORDER BY timestamp DESC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                events.Add(ReadEvent(reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get recent events: {ex.Message}", ex);
            }

            return events;
        }

        #endregion

        /// <summary>
        /// Get event statistics by category
        /// </summary>
        public async Task<Dictionary<string, EventStatistics>> GetEventStatisticsAsync(string sessionId)
        {
            var stats = new Dictionary<string, EventStatistics>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT
                            event_category,
                            COUNT(*) as total_events,
                            SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) as successful,
                            SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END) as failed,
                            AVG(duration_ms) as avg_duration_ms,
                            MIN(timestamp) as first_event,
                            MAX(timestamp) as last_event
                        FROM event_log
                        WHERE session_id = @sessionId
                        GROUP BY event_category";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var category = reader.GetString(0);
                                stats[category] = new EventStatistics
                                {
                                    Category = category,
                                    TotalEvents = reader.GetInt32(1),
                                    Successful = reader.GetInt32(2),
                                    Failed = reader.GetInt32(3),
                                    AvgDurationMs = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                                    FirstEvent = DateTime.Parse(reader.GetString(5)),
                                    LastEvent = DateTime.Parse(reader.GetString(6))
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get event statistics: {ex.Message}", ex);
            }

            return stats;
        }

        #region Cleanup

        /// <summary>
        /// Delete old events beyond retention period
        /// </summary>
        public async Task<int> CleanupOldEventsAsync(TimeSpan retentionPeriod)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var cutoffDate = DateTime.UtcNow.Subtract(retentionPeriod);

                    var sql = @"
                        DELETE FROM event_log
                        WHERE datetime(timestamp) < @cutoffDate";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@cutoffDate", cutoffDate.ToString("o"));

                        var deleted = await command.ExecuteNonQueryAsync();

                        if (deleted > 0)
                        {
                            _logger?.LogInfo($"Cleaned up {deleted} old events (older than {retentionPeriod.TotalDays} days)");
                        }

                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup old events: {ex.Message}", ex);
                return 0;
            }
        }

        #endregion

        #region Helper Methods

        private RevitEvent ReadEvent(SQLiteDataReader reader)
        {
            return new RevitEvent
            {
                Id = reader.GetInt32(0),
                SessionId = reader.GetString(1),
                DocumentId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3)),
                EventType = reader.GetString(4),
                EventCategory = reader.GetString(5),
                EventName = reader.GetString(6),
                ElementId = reader.IsDBNull(7) ? null : reader.GetString(7),
                ElementType = reader.IsDBNull(8) ? null : reader.GetString(8),
                TransactionName = reader.IsDBNull(9) ? null : reader.GetString(9),
                Success = reader.GetInt32(10) == 1,
                ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
                Metadata = reader.IsDBNull(12) ? null : reader.GetString(12),
                DurationMs = reader.IsDBNull(13) ? null : reader.GetInt32(13)
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // EventRepository doesn't hold unmanaged resources, but implements IDisposable for consistency
        }

        #endregion
    }

    #region Data Models

    /// <summary>
    /// Revit event model
    /// </summary>
    public class RevitEvent
    {
        public int Id { get; set; }
        public string SessionId { get; set; }
        public string DocumentId { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; }
        public string EventCategory { get; set; }
        public string EventName { get; set; }
        public string ElementId { get; set; }
        public string ElementType { get; set; }
        public string TransactionName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string Metadata { get; set; }
        public int? DurationMs { get; set; }

        public static RevitEvent Create(
            string sessionId,
            string documentId,
            string eventType,
            string eventCategory,
            string eventName)
        {
            return new RevitEvent
            {
                SessionId = sessionId,
                DocumentId = documentId,
                Timestamp = DateTime.UtcNow,
                EventType = eventType,
                EventCategory = eventCategory,
                EventName = eventName,
                Success = true
            };
        }
    }

    /// <summary>
    /// Event statistics model
    /// </summary>
    public class EventStatistics
    {
        public string Category { get; set; }
        public int TotalEvents { get; set; }
        public int Successful { get; set; }
        public int Failed { get; set; }
        public double AvgDurationMs { get; set; }
        public DateTime FirstEvent { get; set; }
        public DateTime LastEvent { get; set; }

        public double SuccessRate => TotalEvents > 0 ? (double)Successful / TotalEvents * 100 : 0;
        public double ErrorRate => TotalEvents > 0 ? (double)Failed / TotalEvents * 100 : 0;

        public override string ToString()
        {
            return $"{Category}: {TotalEvents} events ({SuccessRate:F1}% success, {ErrorRate:F1}% error)";
        }
    }

    #endregion
}
