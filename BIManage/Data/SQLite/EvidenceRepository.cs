using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing evidence capture metadata and upload tracking
    /// </summary>
    public class EvidenceRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private bool _disposed;

        public EvidenceRepository(string dbPath, ILogger? logger = null)
        {
            _logger = logger;
            _connectionString = SqliteConnectionHelper.BuildConnectionString(dbPath);
            EnsureSchemaExists(dbPath);
        }

        /// <summary>
        /// Record a new evidence capture entry
        /// </summary>
        public async Task<long> RecordEvidenceCaptureAsync(EvidenceCapture evidence)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO evidence_capture (
                            evidence_id, session_id, audit_log_id, protection_type, capture_type, capture_stage,
                            captured_at, rule_id, rule_name, command_id, command_name,
                            element_ids, element_count, file_path, file_size_bytes, file_format,
                            upload_status, metadata, file_hash_sha256
                        ) VALUES (
                            @evidenceId, @sessionId, @auditLogId, @protectionType, @captureType, @captureStage,
                            @capturedAt, @ruleId, @ruleName, @commandId, @commandName,
                            @elementIds, @elementCount, @filePath, @fileSizeBytes, @fileFormat,
                            @uploadStatus, @metadata, @fileHashSha256
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@evidenceId", evidence.EvidenceId);
                        command.Parameters.AddWithValue("@sessionId", evidence.SessionId);
                        command.Parameters.AddWithValue("@auditLogId", (object?)evidence.AuditLogId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@protectionType", (object?)evidence.ProtectionType ?? DBNull.Value);
                        command.Parameters.AddWithValue("@captureType", evidence.CaptureType);
                        command.Parameters.AddWithValue("@captureStage", evidence.CaptureStage);
                        command.Parameters.AddWithValue("@capturedAt", evidence.CapturedAt.ToString("o"));
                        command.Parameters.AddWithValue("@ruleId", (object?)evidence.RuleId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@ruleName", (object?)evidence.RuleName ?? DBNull.Value);
                        command.Parameters.AddWithValue("@commandId", (object?)evidence.CommandId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@commandName", (object?)evidence.CommandName ?? DBNull.Value);
                        command.Parameters.AddWithValue("@elementIds", (object?)evidence.ElementIds ?? DBNull.Value);
                        command.Parameters.AddWithValue("@elementCount", evidence.ElementCount);
                        command.Parameters.AddWithValue("@filePath", (object?)evidence.FilePath ?? DBNull.Value);
                        command.Parameters.AddWithValue("@fileSizeBytes", (object?)evidence.FileSizeBytes ?? DBNull.Value);
                        command.Parameters.AddWithValue("@fileFormat", (object?)evidence.FileFormat ?? DBNull.Value);
                        command.Parameters.AddWithValue("@uploadStatus", evidence.UploadStatus);
                        command.Parameters.AddWithValue("@metadata", (object?)evidence.Metadata ?? DBNull.Value);
                        command.Parameters.AddWithValue("@fileHashSha256", (object?)evidence.FileHashSha256 ?? DBNull.Value);

                        await command.ExecuteNonQueryAsync();

                        // Get the inserted row ID
                        command.CommandText = "SELECT last_insert_rowid()";
                        var result = await command.ExecuteScalarAsync();
                        return Convert.ToInt64(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to record evidence capture {evidence.EvidenceId}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Update upload status for an evidence entry
        /// </summary>
        public async Task<bool> UpdateUploadStatusAsync(string evidenceId, string status, string? uploadUrl = null, string? error = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE evidence_capture
                        SET upload_status = @status,
                            uploaded_at = CASE WHEN @status = 'completed' THEN datetime('now') ELSE uploaded_at END,
                            upload_url = COALESCE(@uploadUrl, upload_url),
                            upload_error = @error,
                            retry_count = CASE WHEN @status = 'failed' THEN retry_count + 1 ELSE retry_count END,
                            last_retry_at = CASE WHEN @status = 'failed' THEN datetime('now') ELSE last_retry_at END
                        WHERE evidence_id = @evidenceId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@evidenceId", evidenceId);
                        command.Parameters.AddWithValue("@status", status);
                        command.Parameters.AddWithValue("@uploadUrl", (object?)uploadUrl ?? DBNull.Value);
                        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update upload status for evidence {evidenceId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// <summary>
        /// Resets retry_count to 0 for all stuck failed-evidence rows that have reached
        /// max_retries — so they get another chance on the next upload tick. Used after
        /// a config change (token fixed, network restored) to unstick previously-failed
        /// uploads without manual DB editing. Returns count of rows reset.
        /// </summary>
        public async Task<int> ResetStuckFailedUploadsAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        UPDATE evidence_capture
                        SET retry_count = 0,
                            upload_status = 'pending',
                            upload_error = NULL
                        WHERE upload_status = 'failed'
                          AND retry_count >= max_retries";
                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        if (rowsAffected > 0)
                            _logger?.LogInfo($"Reset {rowsAffected} stuck failed evidence upload(s) for retry");
                        return rowsAffected;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"ResetStuckFailedUploadsAsync failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Update retry count for an evidence entry
        /// </summary>
        public async Task<bool> UpdateRetryCountAsync(string evidenceId, int retryCount)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "UPDATE evidence_capture SET retry_count = @retryCount WHERE evidence_id = @evidenceId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@evidenceId", evidenceId);
                        command.Parameters.AddWithValue("@retryCount", retryCount);

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update retry count for evidence {evidenceId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Update last retry timestamp for an evidence entry
        /// </summary>
        public async Task<bool> UpdateLastRetryAtAsync(string evidenceId, DateTime lastRetryAt)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "UPDATE evidence_capture SET last_retry_at = @lastRetryAt WHERE evidence_id = @evidenceId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@evidenceId", evidenceId);
                        command.Parameters.AddWithValue("@lastRetryAt", lastRetryAt.ToString("o"));

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update last retry timestamp for evidence {evidenceId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get a single evidence entry by ID
        /// </summary>
        public async Task<EvidenceCapture?> GetByIdAsync(string evidenceId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT * FROM evidence_capture WHERE evidence_id = @evidenceId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@evidenceId", evidenceId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return ReadEvidenceCapture(reader);
                            }
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get evidence by ID {evidenceId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get pending evidence uploads (for retry/upload queue processing)
        /// </summary>
        public async Task<List<EvidenceCapture>> GetPendingUploadsAsync(int limit = 100)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM evidence_capture
                        WHERE upload_status IN ('pending', 'failed')
                          AND retry_count < max_retries
                        ORDER BY captured_at ASC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            var results = new List<EvidenceCapture>();
                            while (await reader.ReadAsync())
                            {
                                results.Add(ReadEvidenceCapture(reader));
                            }
                            return results;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get pending uploads: {ex.Message}", ex);
                return new List<EvidenceCapture>();
            }
        }

        /// <summary>
        /// Get evidence entries for a specific session
        /// </summary>
        public async Task<List<EvidenceCapture>> GetEvidenceBySessionAsync(string sessionId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM evidence_capture
                        WHERE session_id = @sessionId
                        ORDER BY captured_at DESC";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            var results = new List<EvidenceCapture>();
                            while (await reader.ReadAsync())
                            {
                                results.Add(ReadEvidenceCapture(reader));
                            }
                            return results;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get evidence for session {sessionId}: {ex.Message}", ex);
                return new List<EvidenceCapture>();
            }
        }

        /// <summary>
        /// Delete evidence entry and associated file
        /// </summary>
        public async Task<bool> DeleteEvidenceAsync(string evidenceId, bool deleteFile = true)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Get file path before deletion
                    string? filePath = null;
                    if (deleteFile)
                    {
                        var selectSql = "SELECT file_path FROM evidence_capture WHERE evidence_id = @evidenceId";
                        using (var selectCmd = new SQLiteCommand(selectSql, connection))
                        {
                            selectCmd.Parameters.AddWithValue("@evidenceId", evidenceId);
                            filePath = await selectCmd.ExecuteScalarAsync() as string;
                        }
                    }

                    // Delete database entry
                    var deleteSql = "DELETE FROM evidence_capture WHERE evidence_id = @evidenceId";
                    using (var deleteCmd = new SQLiteCommand(deleteSql, connection))
                    {
                        deleteCmd.Parameters.AddWithValue("@evidenceId", evidenceId);
                        var rowsAffected = await deleteCmd.ExecuteNonQueryAsync();

                        // Delete file if requested and exists
                        if (deleteFile && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            try
                            {
                                File.Delete(filePath);
                                _logger?.LogDebug($"Deleted evidence file: {filePath}");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning($"Failed to delete evidence file {filePath}: {ex.Message}");
                            }
                        }

                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete evidence {evidenceId}: {ex.Message}", ex);
                return false;
            }
        }

        #region Private Helpers

        private void EnsureSchemaExists(string dbPath)
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                if (!File.Exists(dbPath))
                {
                    _logger?.LogWarning($"Database file does not exist: {dbPath}");
                    return;
                }

                // Schema should already exist from Schema_Persistence.sql
                // This method is here for future schema migration needs
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to ensure schema exists: {ex.Message}", ex);
            }
        }

        private EvidenceCapture ReadEvidenceCapture(SQLiteDataReader reader)
        {
            return new EvidenceCapture
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                EvidenceId = reader.GetString(reader.GetOrdinal("evidence_id")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                AuditLogId = TryGetString(reader, "audit_log_id"),
                ProtectionType = reader.IsDBNull(reader.GetOrdinal("protection_type")) ? null : reader.GetString(reader.GetOrdinal("protection_type")),
                CaptureType = reader.GetString(reader.GetOrdinal("capture_type")),
                CaptureStage = reader.GetString(reader.GetOrdinal("capture_stage")),
                CapturedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("captured_at"))),
                RuleId = reader.IsDBNull(reader.GetOrdinal("rule_id")) ? null : reader.GetString(reader.GetOrdinal("rule_id")),
                RuleName = reader.IsDBNull(reader.GetOrdinal("rule_name")) ? null : reader.GetString(reader.GetOrdinal("rule_name")),
                CommandId = reader.IsDBNull(reader.GetOrdinal("command_id")) ? null : reader.GetString(reader.GetOrdinal("command_id")),
                CommandName = reader.IsDBNull(reader.GetOrdinal("command_name")) ? null : reader.GetString(reader.GetOrdinal("command_name")),
                ElementIds = reader.IsDBNull(reader.GetOrdinal("element_ids")) ? null : reader.GetString(reader.GetOrdinal("element_ids")),
                ElementCount = reader.GetInt32(reader.GetOrdinal("element_count")),
                FilePath = reader.IsDBNull(reader.GetOrdinal("file_path")) ? null : reader.GetString(reader.GetOrdinal("file_path")),
                FileSizeBytes = reader.IsDBNull(reader.GetOrdinal("file_size_bytes")) ? null : reader.GetInt64(reader.GetOrdinal("file_size_bytes")),
                FileFormat = reader.IsDBNull(reader.GetOrdinal("file_format")) ? null : reader.GetString(reader.GetOrdinal("file_format")),
                UploadStatus = reader.GetString(reader.GetOrdinal("upload_status")),
                UploadedAt = reader.IsDBNull(reader.GetOrdinal("uploaded_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("uploaded_at"))),
                UploadUrl = reader.IsDBNull(reader.GetOrdinal("upload_url")) ? null : reader.GetString(reader.GetOrdinal("upload_url")),
                UploadError = reader.IsDBNull(reader.GetOrdinal("upload_error")) ? null : reader.GetString(reader.GetOrdinal("upload_error")),
                RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
                MaxRetries = reader.GetInt32(reader.GetOrdinal("max_retries")),
                LastRetryAt = reader.IsDBNull(reader.GetOrdinal("last_retry_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_retry_at"))),
                Metadata = reader.IsDBNull(reader.GetOrdinal("metadata")) ? null : reader.GetString(reader.GetOrdinal("metadata")),
                FileHashSha256 = reader.IsDBNull(reader.GetOrdinal("file_hash_sha256")) ? null : reader.GetString(reader.GetOrdinal("file_hash_sha256"))
            };
        }

        private string? TryGetString(SQLiteDataReader reader, string columnName)
        {
            try
            {
                var ordinal = reader.GetOrdinal(columnName);
                return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        #endregion

        /// <summary>
        /// Get all evidence entries for a given audit log entry (before + after screenshots)
        /// </summary>
        public async Task<List<EvidenceCapture>> GetByAuditLogIdAsync(string auditLogId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT * FROM evidence_capture
                        WHERE audit_log_id = @auditLogId
                        ORDER BY capture_stage ASC";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@auditLogId", auditLogId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            var results = new List<EvidenceCapture>();
                            while (await reader.ReadAsync())
                            {
                                results.Add(ReadEvidenceCapture(reader));
                            }
                            return results;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get evidence for audit log {auditLogId}: {ex.Message}", ex);
                return new List<EvidenceCapture>();
            }
        }

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        #endregion

        #region Data Models

        /// <summary>
        /// Evidence capture metadata
        /// </summary>
        public class EvidenceCapture
        {
            public long Id { get; set; }
            public string EvidenceId { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public string? AuditLogId { get; set; }
            public string? ProtectionType { get; set; } // command, pin, event, rule
            public string CaptureType { get; set; } = string.Empty; // screenshot, element_snapshot, document_state
            public string CaptureStage { get; set; } = string.Empty; // before, after
            public DateTime CapturedAt { get; set; }
            public string? RuleId { get; set; }
            public string? RuleName { get; set; }
            public string? CommandId { get; set; }
            public string? CommandName { get; set; }
            public string? ElementIds { get; set; }
            public int ElementCount { get; set; }
            public string? FilePath { get; set; }
            public long? FileSizeBytes { get; set; }
            public string? FileFormat { get; set; }
            public string UploadStatus { get; set; } = "pending"; // pending, uploading, completed, failed
            public DateTime? UploadedAt { get; set; }
            public string? UploadUrl { get; set; }
            public string? UploadError { get; set; }
            public int RetryCount { get; set; }
            public int MaxRetries { get; set; } = 3;
            public DateTime? LastRetryAt { get; set; }
            public string? Metadata { get; set; }
            public string? FileHashSha256 { get; set; } // SHA-256 hash for integrity verification
        }

        #endregion
    }
}
