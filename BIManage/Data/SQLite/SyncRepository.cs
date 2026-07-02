using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for model sync tracking
    /// </summary>
    public class SyncRepository
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;

        public SyncRepository(string databasePath, ILogger? logger = null)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        /// <summary>
        /// Start tracking a sync operation
        /// Returns sync_guid for later updates
        /// </summary>
        public async Task<string> StartSyncAsync(
            string sessionId,
            string modelGuid,
            string modelPath,
            string modelName,
            string syncedBy,
            bool isLocalSaved = false,
            bool isRelinquished = false)
        {
            try
            {
                var syncGuid = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow;

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO model_sync (
                            sync_guid, session_id, model_guid, model_path, model_name,
                            synced_by, sync_started_at, is_local_saved, is_relinquished,
                            is_succeeded, created_at, modified_at
                        ) VALUES (
                            @syncGuid, @sessionId, @modelGuid, @modelPath, @modelName,
                            @syncedBy, @syncStartedAt, @isLocalSaved, @isRelinquished,
                            0, @createdAt, @modifiedAt
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@syncGuid", syncGuid);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@modelName", modelName);
                        command.Parameters.AddWithValue("@syncedBy", syncedBy);
                        command.Parameters.AddWithValue("@syncStartedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@isLocalSaved", isLocalSaved ? 1 : 0);
                        command.Parameters.AddWithValue("@isRelinquished", isRelinquished ? 1 : 0);
                        command.Parameters.AddWithValue("@createdAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedAt", now.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Sync started: {modelName} by {syncedBy}");
                return syncGuid;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to start sync tracking: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Complete a sync operation (success or failure)
        /// </summary>
        public async Task<bool> CompleteSyncAsync(
            string syncGuid,
            bool isSucceeded,
            string errorMessage = null)
        {
            try
            {
                var now = DateTime.UtcNow;

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // First, get the start time to calculate duration
                    var getStartSql = "SELECT sync_started_at FROM model_sync WHERE sync_guid = @syncGuid";
                    DateTime syncStartedAt;

                    using (var command = new SQLiteCommand(getStartSql, connection))
                    {
                        command.Parameters.AddWithValue("@syncGuid", syncGuid);
                        var startTimeStr = await command.ExecuteScalarAsync() as string;

                        if (string.IsNullOrEmpty(startTimeStr))
                        {
                            _logger?.LogWarning($"Sync record not found: {syncGuid}");
                            return false;
                        }

                        syncStartedAt = DateTime.Parse(startTimeStr, null, DateTimeStyles.RoundtripKind);
                    }

                    // Calculate duration
                    var duration = (now - syncStartedAt).TotalSeconds;

                    // Update sync record
                    var updateSql = @"
                        UPDATE model_sync
                        SET sync_ended_at = @syncEndedAt,
                            sync_duration_seconds = @syncDuration,
                            is_succeeded = @isSucceeded,
                            error_message = @errorMessage,
                            modified_at = @modifiedAt
                        WHERE sync_guid = @syncGuid";

                    using (var command = new SQLiteCommand(updateSql, connection))
                    {
                        command.Parameters.AddWithValue("@syncEndedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@syncDuration", duration);
                        command.Parameters.AddWithValue("@isSucceeded", isSucceeded ? 1 : 0);
                        command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@modifiedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@syncGuid", syncGuid);

                        await command.ExecuteNonQueryAsync();
                    }

                    _logger?.LogInfo($"Sync completed: {syncGuid} (Success: {isSucceeded}, Duration: {duration:F2}s)");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to complete sync tracking: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Marks sync operations as failed if they started but never completed within the timeout.
        /// Called periodically to detect syncs that failed without firing DocumentSynchronizedWithCentral
        /// (network errors, lock conflicts, Revit crashes during sync).
        /// </summary>
        public async Task<int> MarkStaleSyncsAsFailedAsync(int timeoutMinutes = 5)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE model_sync
                        SET sync_ended_at = datetime('now'),
                            sync_duration_seconds = (julianday('now') - julianday(sync_started_at)) * 86400,
                            is_succeeded = 0,
                            error_message = 'Sync started but never completed (timeout after ' || @timeout || ' minutes)',
                            modified_at = datetime('now')
                        WHERE sync_ended_at IS NULL
                          AND datetime(sync_started_at) < datetime('now', '-' || @timeout || ' minutes')";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@timeout", timeoutMinutes.ToString());
                        var affected = await command.ExecuteNonQueryAsync();
                        if (affected > 0)
                            _logger?.LogWarning($"Marked {affected} stale sync(s) as failed (started > {timeoutMinutes}m ago, never completed)");
                        return affected;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to mark stale syncs: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get a single sync record by sync GUID
        /// </summary>
        public async Task<ModelSync?> GetSyncByGuidAsync(string syncGuid)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT * FROM model_sync WHERE sync_guid = @syncGuid";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@syncGuid", syncGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return MapFromReader((SQLiteDataReader)reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get sync by GUID: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Get sync history for a specific model
        /// </summary>
        /// <summary>
        /// Upsert a sync record originating from a SignalR event (i.e. a sync performed by
        /// ANOTHER user that we observed in real time). Uses the SignalR sessionId as the
        /// sync_guid so the same event can update on completion without duplicating rows.
        /// </summary>
        public async Task UpsertRemoteSyncAsync(
            string sessionId,
            string modelGuid,
            string? modelName,
            string? syncedBy,
            string? computerName,
            DateTime startedAtUtc,
            DateTime? endedAtUtc,
            bool? isSucceeded)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(modelGuid))
                return;

            try
            {
                var now = DateTime.UtcNow;
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                // INSERT ... ON CONFLICT(sync_guid) DO UPDATE — works because sync_guid has UNIQUE.
                // synced_by is NOT NULL: when no name was provided we fall back to @sessionId
                // (an identifiable per-user value) rather than the literal "Unknown user" —
                // that string used to leak into the History grid for remote rows whose name
                // hadn't resolved yet. The UPDATE branch uses NULLIF(...) so an empty
                // @syncedBy preserves whatever name was already stored from SyncStarting —
                // critical because SyncCompleted often arrives without a username and would
                // otherwise overwrite the real name.
                var sql = @"
                    INSERT INTO model_sync (
                        sync_guid, session_id, model_guid, model_path, model_name,
                        synced_by, sync_started_at, sync_ended_at, sync_duration_seconds,
                        is_local_saved, is_relinquished, is_succeeded,
                        created_at, modified_at
                    ) VALUES (
                        @syncGuid, @sessionId, @modelGuid, NULL, @modelName,
                        COALESCE(NULLIF(@syncedBy, ''), @sessionId), @started, @ended, @duration,
                        0, 0, COALESCE(@isSucceeded, 0),
                        @now, @now
                    )
                    ON CONFLICT(sync_guid) DO UPDATE SET
                        sync_ended_at = COALESCE(@ended, sync_ended_at),
                        sync_duration_seconds = COALESCE(@duration, sync_duration_seconds),
                        is_succeeded = COALESCE(@isSucceeded, is_succeeded),
                        synced_by = COALESCE(NULLIF(@syncedBy, ''), synced_by),
                        model_name = COALESCE(NULLIF(@modelName, ''), model_name),
                        modified_at = @now";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@syncGuid", sessionId);
                command.Parameters.AddWithValue("@sessionId", sessionId);
                command.Parameters.AddWithValue("@modelGuid", modelGuid);
                // model_name is NOT NULL in the schema. Send "" instead of DBNull when the
                // SignalR event omits a model name; the ON CONFLICT branch above uses
                // COALESCE(NULLIF(@modelName, ''), model_name) so the existing row's name is
                // preserved on update, while a fresh insert at least passes the NOT NULL gate.
                command.Parameters.AddWithValue("@modelName", (object?)modelName ?? string.Empty);
                // Bind empty string when no syncedBy is supplied; the SQL above handles both
                // the INSERT (placeholder) and UPDATE (preserve existing) cases via NULLIF.
                command.Parameters.AddWithValue("@syncedBy",
                    string.IsNullOrWhiteSpace(syncedBy) ? string.Empty : syncedBy);
                command.Parameters.AddWithValue("@started", startedAtUtc.ToString("o"));
                command.Parameters.AddWithValue("@ended", endedAtUtc.HasValue ? (object)endedAtUtc.Value.ToString("o") : DBNull.Value);
                double? duration = endedAtUtc.HasValue ? (double?)(endedAtUtc.Value - startedAtUtc).TotalSeconds : null;
                command.Parameters.AddWithValue("@duration", duration.HasValue ? (object)duration.Value : DBNull.Value);
                command.Parameters.AddWithValue("@isSucceeded", isSucceeded.HasValue ? (object)(isSucceeded.Value ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("@now", now.ToString("o"));

                await command.ExecuteNonQueryAsync();
                _logger?.LogDebug($"Upserted remote sync: {sessionId} ({syncedBy ?? "?"} on {computerName ?? "?"})");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to upsert remote sync {sessionId}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Inserts a fresh row in model_sync for an incoming remote SyncStarting event.
        /// Always creates a new row with a generated sync_guid — never UPSERTs — so each
        /// remote sync is recorded as its own history entry. The dialog's
        /// LoadHistoryAsync / LoadActiveRemoteSyncers read from this same table and now
        /// see distinct rows per sync (previously all syncs in a session collapsed into
        /// one row because the key was sync_guid=sessionId).
        /// </summary>
        public async Task<string?> InsertRemoteSyncStartAsync(
            string sessionId,
            string modelGuid,
            string? modelName,
            string? syncedBy,
            string? computerName,
            DateTime startedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(modelGuid))
                return null;

            var syncGuid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                var sql = @"
                    INSERT INTO model_sync (
                        sync_guid, session_id, model_guid, model_path, model_name,
                        synced_by, sync_started_at, sync_ended_at, sync_duration_seconds,
                        is_local_saved, is_relinquished, is_succeeded,
                        created_at, modified_at
                    ) VALUES (
                        @syncGuid, @sessionId, @modelGuid, NULL, @modelName,
                        COALESCE(NULLIF(@syncedBy, ''), @sessionId),
                        @started, NULL, NULL,
                        0, 0, 0,
                        @now, @now
                    )";
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@syncGuid", syncGuid);
                command.Parameters.AddWithValue("@sessionId", sessionId);
                command.Parameters.AddWithValue("@modelGuid", modelGuid);
                command.Parameters.AddWithValue("@modelName", (object?)modelName ?? string.Empty);
                command.Parameters.AddWithValue("@syncedBy",
                    string.IsNullOrWhiteSpace(syncedBy) ? string.Empty : syncedBy);
                command.Parameters.AddWithValue("@started", startedAtUtc.ToString("o"));
                command.Parameters.AddWithValue("@now", now.ToString("o"));
                await command.ExecuteNonQueryAsync();
                _logger?.LogDebug($"Inserted remote sync start: {syncGuid} session={sessionId} by={syncedBy ?? "?"}");
                return syncGuid;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert remote sync start for session {sessionId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Marks the most recent open (sync_ended_at IS NULL) row for this session+model
        /// as completed. Computes sync_duration_seconds from the existing sync_started_at.
        /// If no open row exists (event race / app restart / start was never persisted),
        /// inserts a synthetic closed row with sync_started_at = endedAtUtc - 1s so the
        /// completion isn't lost.
        /// </summary>
        public async Task CompleteRemoteSyncAsync(
            string sessionId,
            string modelGuid,
            bool succeeded,
            string? syncedBy,
            DateTime endedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(modelGuid))
                return;

            var now = DateTime.UtcNow;

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var updateSql = @"
                    UPDATE model_sync SET
                        sync_ended_at = @ended,
                        is_succeeded = @succeeded,
                        synced_by = COALESCE(NULLIF(@syncedBy, ''), synced_by),
                        sync_duration_seconds = (julianday(@ended) - julianday(sync_started_at)) * 86400.0,
                        modified_at = @now
                    WHERE id = (
                        SELECT id FROM model_sync
                        WHERE session_id = @sessionId
                          AND model_guid = @modelGuid
                          AND sync_ended_at IS NULL
                        ORDER BY sync_started_at DESC
                        LIMIT 1
                    )";
                int rowsAffected;
                using (var updateCmd = new SQLiteCommand(updateSql, connection))
                {
                    updateCmd.Parameters.AddWithValue("@ended", endedAtUtc.ToString("o"));
                    updateCmd.Parameters.AddWithValue("@succeeded", succeeded ? 1 : 0);
                    updateCmd.Parameters.AddWithValue("@syncedBy",
                        string.IsNullOrWhiteSpace(syncedBy) ? string.Empty : syncedBy);
                    updateCmd.Parameters.AddWithValue("@now", now.ToString("o"));
                    updateCmd.Parameters.AddWithValue("@sessionId", sessionId);
                    updateCmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                    rowsAffected = await updateCmd.ExecuteNonQueryAsync();
                }

                if (rowsAffected > 0)
                {
                    _logger?.LogDebug($"Closed remote sync row for session={sessionId} succeeded={succeeded}");
                    return;
                }

                // No open row — insert a synthetic completed row so the event isn't lost.
                var syncGuid = Guid.NewGuid().ToString("D");
                var syntheticStart = endedAtUtc.AddSeconds(-1);
                var insertSql = @"
                    INSERT INTO model_sync (
                        sync_guid, session_id, model_guid, model_path, model_name,
                        synced_by, sync_started_at, sync_ended_at, sync_duration_seconds,
                        is_local_saved, is_relinquished, is_succeeded,
                        created_at, modified_at
                    ) VALUES (
                        @syncGuid, @sessionId, @modelGuid, NULL, '',
                        COALESCE(NULLIF(@syncedBy, ''), @sessionId),
                        @started, @ended, 1.0,
                        0, 0, @succeeded,
                        @now, @now
                    )";
                using var insertCmd = new SQLiteCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("@syncGuid", syncGuid);
                insertCmd.Parameters.AddWithValue("@sessionId", sessionId);
                insertCmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                insertCmd.Parameters.AddWithValue("@syncedBy",
                    string.IsNullOrWhiteSpace(syncedBy) ? string.Empty : syncedBy);
                insertCmd.Parameters.AddWithValue("@started", syntheticStart.ToString("o"));
                insertCmd.Parameters.AddWithValue("@ended", endedAtUtc.ToString("o"));
                insertCmd.Parameters.AddWithValue("@succeeded", succeeded ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@now", now.ToString("o"));
                await insertCmd.ExecuteNonQueryAsync();
                _logger?.LogDebug($"Inserted synthetic closed sync row for session={sessionId} (no open row found)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to complete remote sync for session {sessionId}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Delete completed sync history rows for a model. Active (in-flight) syncs —
        /// rows with sync_ended_at IS NULL — are preserved so the UI's "Currently
        /// syncing" panel still reflects reality. Used by the Sync Queue dialog's
        /// "Clear History" button so the cleared list does not reappear when the
        /// dialog is reopened.
        /// </summary>
        public async Task<int> DeleteModelSyncHistoryAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return 0;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                using var command = new SQLiteCommand(
                    "DELETE FROM model_sync WHERE model_guid = @modelGuid AND sync_ended_at IS NOT NULL",
                    connection);
                command.Parameters.AddWithValue("@modelGuid", modelGuid);
                var deleted = await command.ExecuteNonQueryAsync();
                _logger?.LogInfo($"Deleted {deleted} completed sync history row(s) for model {modelGuid}");
                return deleted;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete sync history for model {modelGuid}: {ex.Message}", ex);
                return 0;
            }
        }

        public async Task<List<ModelSync>> GetModelSyncHistoryAsync(string modelGuid, int limit = 100)
        {
            var syncs = new List<ModelSync>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM model_sync
                        WHERE model_guid = @modelGuid
                        ORDER BY sync_started_at DESC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                syncs.Add(MapFromReader((SQLiteDataReader)reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get sync history: {ex.Message}", ex);
            }

            return syncs;
        }

        private ModelSync MapFromReader(SQLiteDataReader reader)
        {
            return new ModelSync
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                SyncGuid = reader.GetString(reader.GetOrdinal("sync_guid")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                ModelGuid = reader.GetString(reader.GetOrdinal("model_guid")),
                ModelPath = reader.IsDBNull(reader.GetOrdinal("model_path")) ? null : reader.GetString(reader.GetOrdinal("model_path")),
                ModelName = reader.GetString(reader.GetOrdinal("model_name")),
                SyncedBy = reader.GetString(reader.GetOrdinal("synced_by")),
                SyncStartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("sync_started_at"))),
                SyncEndedAt = reader.IsDBNull(reader.GetOrdinal("sync_ended_at")) ? null : (DateTime?)DateTime.Parse(reader.GetString(reader.GetOrdinal("sync_ended_at"))),
                SyncDurationSeconds = reader.IsDBNull(reader.GetOrdinal("sync_duration_seconds")) ? null : (double?)reader.GetDouble(reader.GetOrdinal("sync_duration_seconds")),
                IsLocalSaved = reader.GetInt32(reader.GetOrdinal("is_local_saved")) == 1,
                IsSucceeded = reader.GetInt32(reader.GetOrdinal("is_succeeded")) == 1,
                IsRelinquished = reader.GetInt32(reader.GetOrdinal("is_relinquished")) == 1,
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
                CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                ModifiedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("modified_at")))
            };
        }

        #region Sync Queue Management

        /// <summary>
        /// Check if a model is currently being synced by another user
        /// Returns the active sync entry if found, null otherwise
        /// </summary>
        public async Task<SyncQueueEntry?> GetActiveSyncForModelAsync(string modelGuid)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM sync_queue
                        WHERE model_guid = @modelGuid
                          AND status = 'syncing'
                          AND started_at > datetime('now', '-10 minutes')
                        ORDER BY started_at DESC
                        LIMIT 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return MapSyncQueueEntry((SQLiteDataReader)reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check active sync: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Check if current user can sync (no other users syncing the same model)
        /// </summary>
        public async Task<(bool CanSync, string? BlockedByUser, DateTime? BlockedSince)> CanUserSyncAsync(
            string modelGuid,
            string currentSessionId)
        {
            var activeSync = await GetActiveSyncForModelAsync(modelGuid);

            if (activeSync == null)
            {
                return (true, null, null);
            }

            // If the active sync is from the same session, allow it (same user)
            if (activeSync.SessionId == currentSessionId)
            {
                return (true, null, null);
            }

            return (false, activeSync.Username, activeSync.StartedAt);
        }

        /// <summary>
        /// Add user to sync queue (either as 'syncing' or 'waiting')
        /// </summary>
        public async Task<string> AddToSyncQueueAsync(
            string modelGuid,
            string modelName,
            string sessionId,
            string username,
            string status = "waiting")
        {
            try
            {
                var queueId = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow;

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO sync_queue (
                            queue_id, model_guid, model_name, session_id, username,
                            status, requested_at, started_at, created_at
                        ) VALUES (
                            @queueId, @modelGuid, @modelName, @sessionId, @username,
                            @status, @requestedAt, @startedAt, @createdAt
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@queueId", queueId);
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@modelName", modelName);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@username", username);
                        command.Parameters.AddWithValue("@status", status);
                        command.Parameters.AddWithValue("@requestedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@startedAt", status == "syncing" ? now.ToString("o") : (object)DBNull.Value);
                        command.Parameters.AddWithValue("@createdAt", now.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Added to sync queue: {username} for {modelName} (status: {status})");
                return queueId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to add to sync queue: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Update sync queue entry status (e.g., waiting -> syncing -> completed)
        /// </summary>
        public async Task<bool> UpdateSyncQueueStatusAsync(
            string queueId,
            string newStatus,
            string? errorMessage = null)
        {
            try
            {
                var now = DateTime.UtcNow;

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE sync_queue
                        SET status = @status,
                            started_at = CASE WHEN @status = 'syncing' AND started_at IS NULL THEN @now ELSE started_at END,
                            completed_at = CASE WHEN @status IN ('completed', 'failed', 'cancelled') THEN @now ELSE completed_at END,
                            error_message = @errorMessage
                        WHERE queue_id = @queueId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@status", newStatus);
                        command.Parameters.AddWithValue("@now", now.ToString("o"));
                        command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@queueId", queueId);

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update sync queue status: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get waiting users for a model (queue position)
        /// </summary>
        public async Task<List<SyncQueueEntry>> GetWaitingUsersAsync(string modelGuid)
        {
            var entries = new List<SyncQueueEntry>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM sync_queue
                        WHERE model_guid = @modelGuid
                          AND status = 'waiting'
                        ORDER BY requested_at ASC";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                entries.Add(MapSyncQueueEntry((SQLiteDataReader)reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get waiting users: {ex.Message}", ex);
            }

            return entries;
        }

        /// <summary>
        /// Get next user in queue to start syncing
        /// </summary>
        public async Task<SyncQueueEntry?> GetNextInQueueAsync(string modelGuid)
        {
            var waitingUsers = await GetWaitingUsersAsync(modelGuid);
            return waitingUsers.Count > 0 ? waitingUsers[0] : null;
        }

        /// <summary>
        /// Returns ALL currently-syncing entries for a model (status='syncing' within the
        /// last 10 minutes). Unlike GetActiveSyncForModelAsync which returns just one,
        /// this returns every active syncer so the Sync Activity Monitor can show all
        /// concurrent users — important when 4+ users are syncing simultaneously across
        /// different sessions.
        /// </summary>
        public async Task<List<SyncQueueEntry>> GetActiveSyncersForModelAsync(string modelGuid)
        {
            var entries = new List<SyncQueueEntry>();
            if (string.IsNullOrEmpty(modelGuid)) return entries;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                var sql = @"
                    SELECT * FROM sync_queue
                    WHERE model_guid = @modelGuid
                      AND status = 'syncing'
                      AND started_at > datetime('now', '-10 minutes')
                    ORDER BY started_at ASC";
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@modelGuid", modelGuid);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    entries.Add(MapSyncQueueEntry((SQLiteDataReader)reader));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active syncers for {modelGuid}: {ex.Message}", ex);
            }
            return entries;
        }

        /// <summary>
        /// Returns currently in-flight remote syncs persisted to model_sync (sync_ended_at IS NULL),
        /// scoped to the given model and started within the last 10 minutes (stale-row cutoff).
        /// Used by the SyncQueue dialog on open/reopen so remote users who are still syncing
        /// remain visible even when the dialog was closed during their SignalR start event.
        /// </summary>
        public async Task<List<ModelSyncRow>> GetActiveRemoteSyncsFromModelSyncAsync(string modelGuid)
        {
            var rows = new List<ModelSyncRow>();
            if (string.IsNullOrEmpty(modelGuid)) return rows;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                var sql = @"
                    SELECT sync_guid, session_id, synced_by, sync_started_at
                    FROM model_sync
                    WHERE model_guid = @modelGuid
                      AND sync_ended_at IS NULL
                      AND sync_started_at > datetime('now', '-10 minutes')
                    ORDER BY sync_started_at ASC";
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@modelGuid", modelGuid);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    DateTime started = DateTime.UtcNow;
                    if (!reader.IsDBNull(3) && DateTime.TryParse(reader.GetString(3), out var s))
                        started = s;
                    rows.Add(new ModelSyncRow
                    {
                        SyncGuid = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        SessionId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        SyncedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
                        SyncStartedAt = started
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetActiveRemoteSyncsFromModelSyncAsync failed: {ex.Message}");
            }
            return rows;
        }

        /// <summary>
        /// Clean up stale queue entries (expired syncs, old waiting entries)
        /// </summary>
        public async Task<int> CleanupStaleQueueEntriesAsync(int timeoutMinutes = 15)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Mark stale syncing entries as failed
                    var sql = @"
                        UPDATE sync_queue
                        SET status = 'failed',
                            error_message = 'Sync timeout - no completion received',
                            completed_at = datetime('now')
                        WHERE status = 'syncing'
                          AND started_at < datetime('now', @timeout)";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@timeout", $"-{timeoutMinutes} minutes");
                        var staleCount = await command.ExecuteNonQueryAsync();

                        if (staleCount > 0)
                        {
                            _logger?.LogWarning($"Cleaned up {staleCount} stale sync queue entries");
                        }

                        return staleCount;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup stale queue entries: {ex.Message}", ex);
                return 0;
            }
        }

        private SyncQueueEntry MapSyncQueueEntry(SQLiteDataReader reader)
        {
            return new SyncQueueEntry
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                QueueId = reader.GetString(reader.GetOrdinal("queue_id")),
                ModelGuid = reader.GetString(reader.GetOrdinal("model_guid")),
                ModelName = reader.GetString(reader.GetOrdinal("model_name")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                Username = reader.GetString(reader.GetOrdinal("username")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                RequestedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("requested_at"))),
                StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at")) ? null : (DateTime?)DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
                CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : (DateTime?)DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at"))),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
                CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at")))
            };
        }

        #endregion

        #region Background Sync Settings

        /// <summary>
        /// Ensures a default row exists in background_sync_settings.
        /// Called on startup to pre-load defaults if the table is empty.
        /// </summary>
        public async Task<BackgroundSyncSettings> EnsureDefaultSettingsAsync()
        {
            var existing = await GetBackgroundSyncSettingsAsync();
            if (existing != null)
                return existing;

            _logger?.LogInfo("No background sync settings found, seeding defaults...");
            var defaults = new BackgroundSyncSettings();
            await SaveBackgroundSyncSettingsAsync(defaults);
            return defaults;
        }

        /// <summary>
        /// Get background sync settings (universal, single row)
        /// </summary>
        public async Task<BackgroundSyncSettings?> GetBackgroundSyncSettingsAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT * FROM background_sync_settings LIMIT 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return MapBackgroundSyncSettings((SQLiteDataReader)reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get background sync settings: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Save background sync settings (universal, single row with fixed id=1)
        /// </summary>
        public async Task<bool> SaveBackgroundSyncSettingsAsync(BackgroundSyncSettings settings)
        {
            try
            {
                var now = DateTime.UtcNow;

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO background_sync_settings (
                            id, is_enabled,
                            sync_all_the_time,
                            sync_interval_minutes, relinquish_interval_minutes,
                            enable_idle_sync, idle_timeout_minutes, enable_relinquish,
                            sync_on_save, sync_even_if_no_changes,
                            enable_schedule, schedule_start_time, schedule_end_time,
                            compact_model_once_a_day, compact_at_night_only,
                            exit_revit_on_idle, exit_revit_after_minutes,
                            open_views_on_sync_mode, prevent_sync_when_views_opened_over,
                            created_at, modified_at
                        ) VALUES (
                            1, @isEnabled,
                            @syncAllTheTime,
                            @syncInterval, @relinquishInterval,
                            @enableIdleSync, @idleTimeout, @enableRelinquish,
                            @syncOnSave, @syncEvenIfNoChanges,
                            @enableSchedule, @scheduleStartTime, @scheduleEndTime,
                            @compactModelOnceADay, @compactAtNightOnly,
                            @exitRevitOnIdle, @exitRevitAfterMinutes,
                            @openViewsOnSyncMode, @preventSyncWhenViewsOpenedOver,
                            @createdAt, @modifiedAt
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            is_enabled = @isEnabled,
                            sync_all_the_time = @syncAllTheTime,
                            sync_interval_minutes = @syncInterval,
                            relinquish_interval_minutes = @relinquishInterval,
                            enable_idle_sync = @enableIdleSync,
                            idle_timeout_minutes = @idleTimeout,
                            enable_relinquish = @enableRelinquish,
                            sync_on_save = @syncOnSave,
                            sync_even_if_no_changes = @syncEvenIfNoChanges,
                            enable_schedule = @enableSchedule,
                            schedule_start_time = @scheduleStartTime,
                            schedule_end_time = @scheduleEndTime,
                            compact_model_once_a_day = @compactModelOnceADay,
                            compact_at_night_only = @compactAtNightOnly,
                            exit_revit_on_idle = @exitRevitOnIdle,
                            exit_revit_after_minutes = @exitRevitAfterMinutes,
                            open_views_on_sync_mode = @openViewsOnSyncMode,
                            prevent_sync_when_views_opened_over = @preventSyncWhenViewsOpenedOver,
                            modified_at = @modifiedAt";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@isEnabled", settings.IsEnabled ? 1 : 0);
                        command.Parameters.AddWithValue("@syncAllTheTime", settings.SyncAllTheTime ? 1 : 0);
                        command.Parameters.AddWithValue("@syncInterval", settings.SyncIntervalMinutes);
                        command.Parameters.AddWithValue("@relinquishInterval", settings.RelinquishIntervalMinutes);
                        command.Parameters.AddWithValue("@enableIdleSync", settings.EnableIdleSync ? 1 : 0);
                        command.Parameters.AddWithValue("@idleTimeout", settings.IdleTimeoutMinutes);
                        command.Parameters.AddWithValue("@enableRelinquish", settings.EnableRelinquish ? 1 : 0);
                        command.Parameters.AddWithValue("@syncOnSave", settings.SyncOnSave ? 1 : 0);
                        command.Parameters.AddWithValue("@syncEvenIfNoChanges", settings.SyncEvenIfNoChanges ? 1 : 0);
                        command.Parameters.AddWithValue("@enableSchedule", settings.EnableSchedule ? 1 : 0);
                        command.Parameters.AddWithValue("@scheduleStartTime", settings.ScheduleStartTime ?? "21:00");
                        command.Parameters.AddWithValue("@scheduleEndTime", settings.ScheduleEndTime ?? "09:00");
                        command.Parameters.AddWithValue("@compactModelOnceADay", settings.CompactModelOnceADay ? 1 : 0);
                        command.Parameters.AddWithValue("@compactAtNightOnly", settings.CompactAtNightOnly ? 1 : 0);
                        command.Parameters.AddWithValue("@exitRevitOnIdle", settings.ExitRevitOnIdle ? 1 : 0);
                        command.Parameters.AddWithValue("@exitRevitAfterMinutes", settings.ExitRevitAfterMinutes);
                        // Clamp to valid range: open-views-mode 0..2, prevent-over minimum 2
                        command.Parameters.AddWithValue("@openViewsOnSyncMode",
                            Math.Max(0, Math.Min(2, settings.OpenViewsOnSyncMode)));
                        command.Parameters.AddWithValue("@preventSyncWhenViewsOpenedOver",
                            Math.Max(2, settings.PreventSyncWhenViewsOpenedOver));
                        command.Parameters.AddWithValue("@createdAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedAt", now.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo("Saved background sync settings");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save background sync settings: {ex.Message}", ex);
                return false;
            }
        }

        private BackgroundSyncSettings MapBackgroundSyncSettings(SQLiteDataReader reader)
        {
            int GetBool(string col) { var o = reader.GetOrdinal(col); return reader.IsDBNull(o) ? 0 : reader.GetInt32(o); }
            int GetInt(string col, int def) { var o = reader.GetOrdinal(col); return reader.IsDBNull(o) ? def : reader.GetInt32(o); }
            string GetStr(string col, string def) { var o = reader.GetOrdinal(col); return reader.IsDBNull(o) ? def : reader.GetString(o); }

            return new BackgroundSyncSettings
            {
                Id                       = reader.GetInt32(reader.GetOrdinal("id")),
                IsEnabled                = GetBool("is_enabled") == 1,
                SyncAllTheTime           = GetBool("sync_all_the_time") == 1,
                SyncIntervalMinutes      = GetInt("sync_interval_minutes", 60),
                RelinquishIntervalMinutes = GetInt("relinquish_interval_minutes", 60),
                EnableIdleSync           = GetBool("enable_idle_sync") == 1,
                IdleTimeoutMinutes       = GetInt("idle_timeout_minutes", 15),
                EnableRelinquish         = GetBool("enable_relinquish") == 1,
                SyncOnSave               = GetBool("sync_on_save") == 1,
                SyncEvenIfNoChanges      = GetBool("sync_even_if_no_changes") == 1,
                EnableSchedule           = GetBool("enable_schedule") == 1,
                ScheduleStartTime        = GetStr("schedule_start_time", "21:00"),
                ScheduleEndTime          = GetStr("schedule_end_time", "09:00"),
                CompactModelOnceADay     = GetBool("compact_model_once_a_day") == 1,
                CompactAtNightOnly       = GetBool("compact_at_night_only") == 1,
                ExitRevitOnIdle          = GetBool("exit_revit_on_idle") == 1,
                ExitRevitAfterMinutes    = GetInt("exit_revit_after_minutes", 1440),
                OpenViewsOnSyncMode      = GetInt("open_views_on_sync_mode", 0),
                PreventSyncWhenViewsOpenedOver = GetInt("prevent_sync_when_views_opened_over", 10),
                CreatedAt                = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                ModifiedAt               = DateTime.Parse(reader.GetString(reader.GetOrdinal("modified_at")))
            };
        }

        #endregion
    }

    /// <summary>
    /// Lightweight row returned by GetActiveRemoteSyncsFromModelSyncAsync — just the
    /// fields the SyncQueue dialog needs to render an in-flight remote syncer entry.
    /// </summary>
    public class ModelSyncRow
    {
        public string SyncGuid { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string? SyncedBy { get; set; }
        public DateTime SyncStartedAt { get; set; }
    }

    /// <summary>
    /// Model sync record
    /// </summary>
    public class ModelSync
    {
        public int Id { get; set; }
        public string SyncGuid { get; set; }
        public string SessionId { get; set; }
        public string ModelGuid { get; set; }
        public string ModelPath { get; set; }
        public string ModelName { get; set; }
        public string SyncedBy { get; set; }
        public DateTime SyncStartedAt { get; set; }
        public DateTime? SyncEndedAt { get; set; }
        public double? SyncDurationSeconds { get; set; }
        public bool IsLocalSaved { get; set; }
        public bool IsSucceeded { get; set; }
        public bool IsRelinquished { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }

    /// <summary>
    /// Sync queue entry - tracks users waiting to sync or currently syncing
    /// </summary>
    public class SyncQueueEntry
    {
        public int Id { get; set; }
        public string QueueId { get; set; }
        public string ModelGuid { get; set; }
        public string ModelName { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        /// <summary>
        /// Status: waiting, syncing, completed, failed, cancelled
        /// </summary>
        public string Status { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Background sync settings (universal per user, single row)
    /// </summary>
    public class BackgroundSyncSettings
    {
        public int Id { get; set; }

        /// <summary>
        /// Master enable/disable background sync for this model
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        // ── Sync Mode ──────────────────────────────────────────────────────────

        /// <summary>
        /// When true: sync continuously regardless of user activity .
        /// When false (default): sync only when user is idle .
        /// </summary>
        public bool SyncAllTheTime { get; set; } = false;

        // ── Timing ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Interval in minutes for automatic sync (default 60 min)
        /// </summary>
        public int SyncIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Interval in minutes for automatic relinquish (default 60 min)
        /// </summary>
        public int RelinquishIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Enable sync when user is idle (used when SyncAllTheTime = false)
        /// </summary>
        public bool EnableIdleSync { get; set; } = true;

        /// <summary>
        /// Minutes of inactivity before triggering idle sync (default 15 min)
        /// </summary>
        public int IdleTimeoutMinutes { get; set; } = 15;

        // ── Behaviour ──────────────────────────────────────────────────────────

        /// <summary>
        /// Enable automatic relinquish
        /// </summary>
        public bool EnableRelinquish { get; set; } = true;

        /// <summary>
        /// Sync automatically when user saves locally
        /// </summary>
        public bool SyncOnSave { get; set; } = false;

        /// <summary>
        /// Sync even when the document has no pending changes 
        /// </summary>
        public bool SyncEvenIfNoChanges { get; set; } = false;

        // ── Schedule ───────────────────────────────────────────────────────────

        /// <summary>
        /// Restrict sync to a defined time window 
        /// </summary>
        public bool EnableSchedule { get; set; } = false;

        /// <summary>
        /// Schedule start time as "HH:mm" (e.g. "21:00"). Supports overnight ranges.
        /// </summary>
        public string ScheduleStartTime { get; set; } = "21:00";

        /// <summary>
        /// Schedule end time as "HH:mm" (e.g. "09:00"). Supports overnight ranges.
        /// </summary>
        public string ScheduleEndTime { get; set; } = "09:00";

        // ── Compact Model ──────────────────────────────────────────────────────

        /// <summary>
        /// Compact the model on the first sync of each calendar day 
        /// </summary>
        public bool CompactModelOnceADay { get; set; } = false;

        /// <summary>
        /// Only compact during the night window (00:00–06:00) 
        /// </summary>
        public bool CompactAtNightOnly { get; set; } = false;

        // ── Auto-Exit ──────────────────────────────────────────────────────────

        /// <summary>
        /// Close Revit automatically after a prolonged idle period 
        /// </summary>
        public bool ExitRevitOnIdle { get; set; } = false;

        /// <summary>
        /// Minutes of inactivity before auto-closing Revit (default 1440 = 24 h)
        /// </summary>
        public int ExitRevitAfterMinutes { get; set; } = 1440;

        // ── Open Views Behaviour ───────────────────────────────────────────────

        /// <summary>
        /// What to do with currently-open views when sync starts:
        /// 0 = Keep them open (default)
        /// 1 = Close all views
        /// 2 = Close all views and reopen after sync
        /// </summary>
        public int OpenViewsOnSyncMode { get; set; } = 0;

        /// <summary>
        /// Prevent background sync when more than this many views are open.
        /// Minimum allowed value is 2. Default 10.
        /// </summary>
        public int PreventSyncWhenViewsOpenedOver { get; set; } = 10;

        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
}
