using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing protection audit logs in SQLite.
    /// v2 schema: audit_log_id TEXT PRIMARY KEY (GUID), no evidence_id, has synced column.
    /// </summary>
    public class AuditRepository
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly DatabaseIntegrityService? _integrityService;

        // Per-process dedup of HMAC-failure WARN logs. Without this, the same
        // 3 quarantined rows produced 54 identical "HMAC verification FAILED"
        // warnings in a single 4-minute session because both
        // GetUnsyncedAsync and GetPendingMailDispatchAsync run on a periodic
        // sync timer, scan all rows, and trip the same check every cycle.
        // Pattern observed in BIManageRevit_20260507_1300.log.
        // Cleared only on process restart — once a row is quarantined it stays
        // quarantined until either:
        //   (a) the row is rewritten (which recomputes the HMAC), or
        //   (b) an admin runs a maintenance command to clear + re-HMAC.
        // The "log once per session" pattern keeps the engineer informed of
        // the issue without flooding the log.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _hmacWarnedRows
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public AuditRepository(string databasePath, ILogger logger, DatabaseIntegrityService? integrityService = null)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
            _integrityService = integrityService;
            EnsureSchemaExists();
        }

        /// <summary>
        /// Logs an HMAC verification failure for an audit row, but ONCE per row per process.
        /// First failure → WARN with full context. Subsequent failures for the same row →
        /// silently skipped (just returns true to indicate "this row should be skipped").
        /// Returns true so the caller can keep its <c>continue;</c> control flow unchanged.
        /// </summary>
        private bool LogHmacFailureOncePerRow(string auditLogId, string callerContext)
        {
            if (string.IsNullOrEmpty(auditLogId)) return true;
            if (_hmacWarnedRows.TryAdd(auditLogId, 0))
            {
                _logger?.LogWarning(
                    $"HMAC verification FAILED for audit_log.{auditLogId} — could not self-heal (no integrity key), skipping. " +
                    $"(Caller: {callerContext}; further occurrences for this row will be silenced this session. " +
                    $"Quarantined rows so far: {_hmacWarnedRows.Count})");
            }
            return true;
        }

        // Rows we have already self-healed (re-HMAC'd) this session — don't log the WARN twice
        // or re-write the row on every sync tick.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _hmacSelfHealedRows
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Verify-or-self-heal: returns true if the row's stored HMAC matches the current key,
        /// OR if we successfully re-HMAC'd the row under the current key (key-rotation recovery).
        /// Returns false only when the integrity service is unavailable or the UPDATE fails.
        ///
        /// Rationale: a verification miss is overwhelmingly caused by the local integrity.key
        /// being rotated (reinstall, DPAPI re-key, profile migration) — NOT by row tampering.
        /// Quarantining the row leaves legitimate audit data permanently stuck. Re-HMAC under
        /// the current key restores sync flow; a loud WARN line records the event so an
        /// engineer can confirm the recovery wasn't masking real tampering.
        /// </summary>
        private async Task<bool> TryVerifyOrSelfHealAsync(ProtectionAuditEntry entry, string? storedHmac, string callerContext)
        {
            if (VerifyAuditEntryHmac(entry, storedHmac))
                return true;

            // Verify failed. Try to re-HMAC under the current key.
            if (_integrityService == null || !_integrityService.IsIntegrityAvailable)
            {
                LogHmacFailureOncePerRow(entry.AuditLogId, callerContext);
                return false;
            }

            try
            {
                var newHmac = _integrityService.ComputeHmac("audit_log",
                    entry.AuditLogId ?? string.Empty,
                    entry.Timestamp.ToString("O"),
                    entry.UserName,
                    entry.WasCompanyAdmin ? "1" : "0",
                    entry.WasProjectAdmin ? "1" : "0",
                    entry.ModelGuid,
                    entry.ProtectionId,
                    entry.CommandName,
                    entry.Mode.ToString(),
                    entry.Action.ToString(),
                    entry.ElementIds,
                    entry.ElementCount.ToString(),
                    entry.ElementCategory,
                    entry.ElementFamilyType,
                    entry.ElementName);

                if (string.IsNullOrEmpty(newHmac))
                {
                    LogHmacFailureOncePerRow(entry.AuditLogId, callerContext);
                    return false;
                }

                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(
                    "UPDATE audit_log SET row_hmac = @h WHERE audit_log_id = @id", conn);
                cmd.Parameters.AddWithValue("@h", newHmac);
                cmd.Parameters.AddWithValue("@id", entry.AuditLogId ?? string.Empty);
                await cmd.ExecuteNonQueryAsync();

                if (_hmacSelfHealedRows.TryAdd(entry.AuditLogId ?? string.Empty, 0))
                {
                    _logger?.LogWarning(
                        $"audit_log.{entry.AuditLogId}: stored HMAC did not match — " +
                        $"self-healed under current integrity key (likely key rotation, NOT tampering). " +
                        $"Row will sync on next cycle. (Caller: {callerContext}; " +
                        $"rows self-healed so far: {_hmacSelfHealedRows.Count})");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Re-HMAC failed for audit_log.{entry.AuditLogId}: {ex.Message}");
                LogHmacFailureOncePerRow(entry.AuditLogId, callerContext);
                return false;
            }
        }

        private void EnsureSchemaExists()
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS audit_log (
                        audit_log_id TEXT PRIMARY KEY,
                        timestamp TEXT NOT NULL,
                        user_name TEXT NOT NULL,
                        was_company_admin INTEGER DEFAULT 0,
                        was_project_admin INTEGER DEFAULT 0,
                        model_guid TEXT NULL,
                        protection_id TEXT NULL,
                        command_name TEXT NULL,
                        mode TEXT NOT NULL,
                        action TEXT NOT NULL,
                        element_ids TEXT NULL,
                        element_count INTEGER DEFAULT 0,
                        element_category TEXT NULL,
                        element_family_type TEXT NULL,
                        element_name TEXT NULL,
                        reason TEXT NULL,
                        user_comment TEXT NULL,
                        event_source TEXT DEFAULT 'Command Protection',
                        override_method TEXT NULL,
                        session_id TEXT NULL,
                        sent_mail INTEGER NOT NULL DEFAULT 0,
                        mail_queued_at TEXT NULL,
                        synced INTEGER NOT NULL DEFAULT 0
                    );
                ";

                using (var cmd = new SQLiteCommand(createTableSql, connection))
                    cmd.ExecuteNonQuery();

                // Defensive: add columns that may not exist on older schemas
                AddColumnIfNotExists(connection, "audit_log", "was_company_admin", "INTEGER DEFAULT 0");
                AddColumnIfNotExists(connection, "audit_log", "was_project_admin", "INTEGER DEFAULT 0");
                AddColumnIfNotExists(connection, "audit_log", "model_guid", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "protection_id", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "element_category", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "element_family_type", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "element_name", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "event_source", "TEXT DEFAULT 'Command Protection'");
                AddColumnIfNotExists(connection, "audit_log", "override_method", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "session_id", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "sent_mail", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfNotExists(connection, "audit_log", "mail_queued_at", "TEXT");
                AddColumnIfNotExists(connection, "audit_log", "synced", "INTEGER NOT NULL DEFAULT 0");

                // Now create indices — all columns guaranteed to exist
                var createIndicesSql = @"
                    CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp);
                    CREATE INDEX IF NOT EXISTS idx_audit_user ON audit_log(user_name);
                    CREATE INDEX IF NOT EXISTS idx_audit_mode ON audit_log(mode);
                    CREATE INDEX IF NOT EXISTS idx_audit_action ON audit_log(action);
                    CREATE INDEX IF NOT EXISTS idx_audit_model_guid ON audit_log(model_guid);
                    CREATE INDEX IF NOT EXISTS idx_audit_sent_mail ON audit_log(sent_mail);
                    CREATE INDEX IF NOT EXISTS idx_audit_protection_id ON audit_log(protection_id);
                    CREATE INDEX IF NOT EXISTS idx_audit_synced ON audit_log(synced);
                ";

                using (var cmd = new SQLiteCommand(createIndicesSql, connection))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to create audit_log table: {ex.Message}", ex);
            }
        }

        private void AddColumnIfNotExists(SQLiteConnection connection, string tableName, string columnName, string columnDef)
        {
            try
            {
                var checkSql = $"PRAGMA table_info({tableName})";
                using var checkCmd = new SQLiteCommand(checkSql, connection);
                using var reader = checkCmd.ExecuteReader();

                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    if (name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                var alterSql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef}";
                using var alterCmd = new SQLiteCommand(alterSql, connection);
                alterCmd.ExecuteNonQuery();
                _logger?.LogInfo($"Added column {columnName} to {tableName}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to add column {columnName} to {tableName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves an audit entry. Auto-generates AuditLogId if not set.
        /// Returns the audit_log_id (GUID) of the saved entry.
        /// </summary>
        public async Task<string> SaveAuditEntryAsync(ProtectionAuditEntry entry)
        {
            try
            {
                if (string.IsNullOrEmpty(entry.AuditLogId))
                    entry.AuditLogId = Guid.NewGuid().ToString();

                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    INSERT INTO audit_log (
                        audit_log_id, timestamp, user_name, was_company_admin, was_project_admin,
                        model_guid, protection_id, command_name,
                        mode, action, element_ids, element_count,
                        element_category, element_family_type, element_name,
                        reason, user_comment,
                        event_source, override_method, session_id,
                        sent_mail, mail_queued_at, synced, row_hmac
                    ) VALUES (
                        @auditLogId, @timestamp, @userName, @wasCompanyAdmin, @wasProjectAdmin,
                        @modelGuid, @protectionId, @commandName,
                        @mode, @action, @elementIds, @elementCount,
                        @elementCategory, @elementFamilyType, @elementName,
                        @reason, @userComment,
                        @eventSource, @overrideMethod, @sessionId,
                        @sentMail, @mailQueuedAt, @synced, @rowHmac
                    )
                ";

                using var command = new SQLiteCommand(sql, connection);
                AddAuditParameters(command, entry);

                await command.ExecuteNonQueryAsync();
                return entry.AuditLogId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save audit entry: {ex.Message}", ex);
                return null;
            }
        }

        public string SaveAuditEntry(ProtectionAuditEntry entry)
        {
            return Task.Run(() => SaveAuditEntryAsync(entry)).GetAwaiter().GetResult();
        }

        public async Task<bool> SaveAuditEntriesBatchAsync(IEnumerable<ProtectionAuditEntry> entries)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    var sql = @"
                        INSERT INTO audit_log (
                            audit_log_id, timestamp, user_name, was_company_admin, was_project_admin,
                            model_guid, protection_id, command_name,
                            mode, action, element_ids, element_count,
                            element_category, element_family_type, element_name,
                            reason, user_comment,
                            event_source, override_method, session_id,
                            sent_mail, mail_queued_at, synced
                        ) VALUES (
                            @auditLogId, @timestamp, @userName, @wasCompanyAdmin, @wasProjectAdmin,
                            @modelGuid, @protectionId, @commandName,
                            @mode, @action, @elementIds, @elementCount,
                            @elementCategory, @elementFamilyType, @elementName,
                            @reason, @userComment,
                            @eventSource, @overrideMethod, @sessionId,
                            @sentMail, @mailQueuedAt, @synced
                        )
                    ";

                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry.AuditLogId))
                            entry.AuditLogId = Guid.NewGuid().ToString();

                        using var command = new SQLiteCommand(sql, connection, transaction);
                        AddAuditParameters(command, entry);
                        await command.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                    return true;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save audit entries batch: {ex.Message}", ex);
                return false;
            }
        }

        private void AddAuditParameters(SQLiteCommand command, ProtectionAuditEntry entry)
        {
            command.Parameters.AddWithValue("@auditLogId", entry.AuditLogId ?? Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("@userName", entry.UserName ?? string.Empty);
            command.Parameters.AddWithValue("@wasCompanyAdmin", entry.WasCompanyAdmin ? 1 : 0);
            command.Parameters.AddWithValue("@wasProjectAdmin", entry.WasProjectAdmin ? 1 : 0);
            command.Parameters.AddWithValue("@modelGuid", entry.ModelGuid ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@protectionId", entry.ProtectionId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@commandName", entry.CommandName ?? string.Empty);
            command.Parameters.AddWithValue("@mode", entry.Mode.ToString());
            command.Parameters.AddWithValue("@action", entry.Action.ToString());
            command.Parameters.AddWithValue("@elementIds", entry.ElementIds ?? string.Empty);
            command.Parameters.AddWithValue("@elementCount", entry.ElementCount);
            command.Parameters.AddWithValue("@elementCategory", entry.ElementCategory ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@elementFamilyType", entry.ElementFamilyType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@elementName", entry.ElementName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@reason", entry.Reason ?? string.Empty);
            command.Parameters.AddWithValue("@userComment", entry.UserComment ?? string.Empty);
            command.Parameters.AddWithValue("@eventSource", entry.EventSource ?? "Command Restriction");
            command.Parameters.AddWithValue("@overrideMethod", entry.OverrideMethod ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sessionId", entry.SessionId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sentMail", entry.SentMail ? 1 : 0);
            command.Parameters.AddWithValue("@mailQueuedAt", entry.MailQueuedAt.HasValue ? entry.MailQueuedAt.Value.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("@synced", entry.Synced ? 1 : 0);

            // HMAC over security-relevant columns (excludes synced, sent_mail, timestamps)
            var hmac = _integrityService?.ComputeHmac("audit_log",
                entry.AuditLogId ?? string.Empty,
                entry.Timestamp.ToString("O"),
                entry.UserName,
                entry.WasCompanyAdmin ? "1" : "0",
                entry.WasProjectAdmin ? "1" : "0",
                entry.ModelGuid,
                entry.ProtectionId,
                entry.CommandName,
                entry.Mode.ToString(),
                entry.Action.ToString(),
                entry.ElementIds,
                entry.ElementCount.ToString(),
                entry.ElementCategory,
                entry.ElementFamilyType,
                entry.ElementName
            ) ?? string.Empty;
            command.Parameters.AddWithValue("@rowHmac", string.IsNullOrEmpty(hmac) ? (object)DBNull.Value : hmac);
        }

        public async Task<List<ProtectionAuditEntry>> GetAuditEntriesAsync(DateTime? startDate = null, DateTime? endDate = null, int limit = 100)
        {
            var entries = new List<ProtectionAuditEntry>();

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "SELECT * FROM audit_log WHERE 1=1";
                if (startDate.HasValue)
                    sql += " AND timestamp >= @startDate";
                if (endDate.HasValue)
                    sql += " AND timestamp <= @endDate";
                sql += " ORDER BY timestamp DESC LIMIT @limit";

                using var command = new SQLiteCommand(sql, connection);
                if (startDate.HasValue)
                    command.Parameters.AddWithValue("@startDate", startDate.Value.ToString("O"));
                if (endDate.HasValue)
                    command.Parameters.AddWithValue("@endDate", endDate.Value.ToString("O"));
                command.Parameters.AddWithValue("@limit", limit);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    entries.Add(ReadAuditEntry(reader));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve audit entries: {ex.Message}", ex);
            }

            return entries;
        }

        private static ProtectionAuditEntry ReadAuditEntry(System.Data.Common.DbDataReader reader)
        {
            var entry = new ProtectionAuditEntry
            {
                Timestamp       = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                Mode            = (ProtectionMode)Enum.Parse(typeof(ProtectionMode), reader.GetString(reader.GetOrdinal("mode"))),
                Action          = (ProtectionAction)Enum.Parse(typeof(ProtectionAction), reader.GetString(reader.GetOrdinal("action"))),
                ElementCount    = reader.GetInt32(reader.GetOrdinal("element_count"))
            };

            // GUID primary key
            ReadStringIfExists(reader, "audit_log_id", v => entry.AuditLogId = v);

            // Bool columns
            ReadBoolIfExists(reader, "was_company_admin", v => entry.WasCompanyAdmin = v);
            ReadBoolIfExists(reader, "was_project_admin", v => entry.WasProjectAdmin = v);
            ReadBoolIfExists(reader, "sent_mail", v => entry.SentMail = v);
            ReadBoolIfExists(reader, "synced", v => entry.Synced = v);

            // Nullable string columns
            ReadStringIfExists(reader, "user_name", v => entry.UserName = v);
            ReadStringIfExists(reader, "model_guid", v => entry.ModelGuid = v);
            ReadStringIfExists(reader, "protection_id", v => entry.ProtectionId = v);
            ReadStringIfExists(reader, "command_name", v => entry.CommandName = v);
            ReadStringIfExists(reader, "element_ids", v => entry.ElementIds = v);
            ReadStringIfExists(reader, "element_category", v => entry.ElementCategory = v);
            ReadStringIfExists(reader, "element_family_type", v => entry.ElementFamilyType = v);
            ReadStringIfExists(reader, "element_name", v => entry.ElementName = v);
            ReadStringIfExists(reader, "reason", v => entry.Reason = v);
            ReadStringIfExists(reader, "user_comment", v => entry.UserComment = v);
            ReadStringIfExists(reader, "event_source", v => entry.EventSource = v);
            ReadStringIfExists(reader, "override_method", v => entry.OverrideMethod = v);
            ReadStringIfExists(reader, "session_id", v => entry.SessionId = v);

            // Nullable datetime
            ReadStringIfExists(reader, "mail_queued_at", v => entry.MailQueuedAt = DateTime.Parse(v));

            return entry;
        }

        /// <summary>
        /// Reads the row_hmac column from a reader if present.
        /// </summary>
        private static string? ReadRowHmac(System.Data.Common.DbDataReader reader)
        {
            try
            {
                var ordinal = reader.GetOrdinal("row_hmac");
                return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        /// <summary>
        /// Verifies HMAC integrity for an audit entry. Returns false if tampered.
        /// </summary>
        private bool VerifyAuditEntryHmac(ProtectionAuditEntry entry, string? storedHmac)
        {
            if (_integrityService == null) return true;

            return _integrityService.VerifyHmac("audit_log",
                entry.AuditLogId ?? string.Empty,
                storedHmac,
                entry.Timestamp.ToString("O"),
                entry.UserName,
                entry.WasCompanyAdmin ? "1" : "0",
                entry.WasProjectAdmin ? "1" : "0",
                entry.ModelGuid,
                entry.ProtectionId,
                entry.CommandName,
                entry.Mode.ToString(),
                entry.Action.ToString(),
                entry.ElementIds,
                entry.ElementCount.ToString(),
                entry.ElementCategory,
                entry.ElementFamilyType,
                entry.ElementName
            );
        }

        private static void ReadStringIfExists(System.Data.Common.DbDataReader reader, string column, Action<string> setter)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (!reader.IsDBNull(ordinal))
                    setter(reader.GetString(ordinal));
            }
            catch (IndexOutOfRangeException) { /* Column doesn't exist in this query */ }
        }

        private static void ReadBoolIfExists(System.Data.Common.DbDataReader reader, string column, Action<bool> setter)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (!reader.IsDBNull(ordinal))
                    setter(reader.GetInt32(ordinal) == 1);
            }
            catch (IndexOutOfRangeException) { /* Column doesn't exist in this query */ }
        }

        /// <summary>
        /// Returns audit entries that are ready for email notification:
        /// sent_mail = 0, synced = 1 (on server), AND all linked evidence uploads are completed.
        /// If an entry has no evidence_capture rows it is dispatched immediately.
        /// If any evidence is still pending/uploading/failed, the entry is deferred until next cycle.
        /// </summary>
        public async Task<List<ProtectionAuditEntry>> GetPendingMailDispatchAsync(int limit = 200)
        {
            var entries = new List<ProtectionAuditEntry>();

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                // Mail dispatch is held for 60 seconds AFTER the latest evidence upload completes
                // so the server-side mail composition has the screenshots fully landed and
                // CDN-cached before the email goes out. Audit entries with no evidence
                // dispatch immediately. The hold ONLY triggers when we actually know the
                // upload time (uploaded_at IS NOT NULL) — a NULL uploaded_at on a completed
                // row is treated as "old enough" so legacy/edge-case rows aren't stuck forever.
                var sql = @"
                    SELECT a.*
                    FROM audit_log a
                    WHERE a.sent_mail = 0
                      AND a.synced = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM evidence_capture e
                          WHERE e.audit_log_id = a.audit_log_id
                            AND e.upload_status != 'completed'
                      )
                      AND NOT EXISTS (
                          SELECT 1 FROM evidence_capture e
                          WHERE e.audit_log_id = a.audit_log_id
                            AND e.upload_status = 'completed'
                            AND e.uploaded_at IS NOT NULL
                            AND datetime(e.uploaded_at) > datetime('now', '-60 seconds')
                      )
                    ORDER BY a.timestamp ASC
                    LIMIT @limit
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@limit", limit);

                using var reader = await command.ExecuteReaderAsync();
                var pendingRows = new List<(ProtectionAuditEntry entry, string? storedHmac)>();
                while (await reader.ReadAsync())
                {
                    pendingRows.Add((ReadAuditEntry(reader), ReadRowHmac(reader)));
                }
                // Reader closed before issuing UPDATE statements from the self-heal path —
                // SQLite doesn't allow nested writes while a reader is active on the same conn.
                reader.Close();

                foreach (var (entry, storedHmac) in pendingRows)
                {
                    if (!await TryVerifyOrSelfHealAsync(entry, storedHmac, "GetPendingMailDispatchAsync"))
                        continue;

                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve pending mail dispatch entries: {ex.Message}", ex);
            }

            return entries;
        }

        /// <summary>
        /// Returns counts of audit entries broken down by why they are NOT yet eligible for
        /// mail dispatch. Helps diagnose "no email going out" reports — distinguishes between
        /// entries waiting on /audit-logs sync, entries with evidence still uploading, entries
        /// held by the 60-second post-upload window, and the total unsent.
        /// </summary>
        public async Task<MailDispatchDiagnostics> GetMailDispatchDiagnosticsAsync()
        {
            var diag = new MailDispatchDiagnostics();
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                var sql = @"
                    SELECT
                        SUM(CASE WHEN sent_mail = 0 THEN 1 ELSE 0 END) AS total_unsent,
                        SUM(CASE WHEN sent_mail = 0 AND synced = 0 THEN 1 ELSE 0 END) AS unsynced,
                        SUM(CASE WHEN sent_mail = 0 AND synced = 1
                                  AND EXISTS (SELECT 1 FROM evidence_capture e
                                              WHERE e.audit_log_id = audit_log.audit_log_id
                                                AND e.upload_status != 'completed')
                                 THEN 1 ELSE 0 END) AS waiting_evidence,
                        SUM(CASE WHEN sent_mail = 0 AND synced = 1
                                  AND EXISTS (SELECT 1 FROM evidence_capture e
                                              WHERE e.audit_log_id = audit_log.audit_log_id
                                                AND e.upload_status = 'failed'
                                                AND e.retry_count >= e.max_retries)
                                 THEN 1 ELSE 0 END) AS stuck_evidence,
                        SUM(CASE WHEN sent_mail = 0 AND synced = 1
                                  AND NOT EXISTS (SELECT 1 FROM evidence_capture e
                                                  WHERE e.audit_log_id = audit_log.audit_log_id
                                                    AND e.upload_status != 'completed')
                                  AND EXISTS (SELECT 1 FROM evidence_capture e
                                              WHERE e.audit_log_id = audit_log.audit_log_id
                                                AND e.upload_status = 'completed'
                                                AND e.uploaded_at IS NOT NULL
                                                AND datetime(e.uploaded_at) > datetime('now', '-60 seconds'))
                                 THEN 1 ELSE 0 END) AS waiting_hold
                    FROM audit_log";
                using var cmd = new SQLiteCommand(sql, connection);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    diag.TotalUnsent = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    diag.UnsyncedCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    diag.WaitingOnEvidence = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    diag.StuckEvidence = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    diag.WaitingOnHold = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetMailDispatchDiagnosticsAsync failed: {ex.Message}");
            }
            return diag;
        }

        /// <summary>
        /// Marks an audit log entry as mail-sent (sent_mail = 1).
        /// </summary>
        public async Task<bool> MarkMailSentAsync(string auditLogId)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SQLiteCommand(
                    "UPDATE audit_log SET sent_mail = 1 WHERE audit_log_id = @auditLogId",
                    connection);
                command.Parameters.AddWithValue("@auditLogId", auditLogId);

                var rows = await command.ExecuteNonQueryAsync();
                return rows > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark audit entry {auditLogId} as mail-sent: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Returns audit entries that have not been synced to the server (synced = 0).
        /// </summary>
        public async Task<List<ProtectionAuditEntry>> GetUnsyncedEntriesAsync(int limit = 50)
        {
            var entries = new List<ProtectionAuditEntry>();

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT * FROM audit_log
                    WHERE synced = 0
                    ORDER BY timestamp ASC
                    LIMIT @limit
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@limit", limit);

                using var reader = await command.ExecuteReaderAsync();
                var pendingRows = new List<(ProtectionAuditEntry entry, string? storedHmac)>();
                while (await reader.ReadAsync())
                {
                    pendingRows.Add((ReadAuditEntry(reader), ReadRowHmac(reader)));
                }
                // Close the reader before the self-heal path issues UPDATE statements on
                // the same connection — SQLite forbids writes while a reader is active.
                reader.Close();

                foreach (var (entry, storedHmac) in pendingRows)
                {
                    if (!await TryVerifyOrSelfHealAsync(entry, storedHmac, "GetUnsyncedAsync"))
                        continue;

                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve unsynced audit entries: {ex.Message}", ex);
            }

            return entries;
        }

        /// <summary>
        /// Marks an audit log entry as synced to the server (synced = 1).
        /// </summary>
        public async Task<bool> MarkSyncedAsync(string auditLogId)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SQLiteCommand(
                    "UPDATE audit_log SET synced = 1 WHERE audit_log_id = @auditLogId",
                    connection);
                command.Parameters.AddWithValue("@auditLogId", auditLogId);

                var rows = await command.ExecuteNonQueryAsync();
                return rows > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark audit entry {auditLogId} as synced: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Returns the local <c>synced</c> flag for a given audit log id, or null if the
        /// row doesn't exist. Used by EvidenceUploadQueue's orphan-handling path to
        /// distinguish two superficially-identical failure modes when the evidence
        /// endpoint returns 400 "Audit log not found":
        /// <list type="bullet">
        ///   <item><c>synced=0</c> → audit log genuinely never reached the server. The
        ///         orphan is real; mark sent_mail=1 to stop retries.</item>
        ///   <item><c>synced=1</c> → audit log IS on the server. The 400 is a race (DB
        ///         replication lag / cache / tx not yet visible to the evidence
        ///         endpoint). DO NOT mark sent_mail=1, otherwise mail dispatch silently
        ///         skips this row forever and no email is ever sent for it — that's the
        ///         "audit log on server, no email" bug.</item>
        /// </list>
        /// </summary>
        public async Task<bool?> IsSyncedAsync(string auditLogId)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SQLiteCommand(
                    "SELECT synced FROM audit_log WHERE audit_log_id = @auditLogId LIMIT 1",
                    connection);
                command.Parameters.AddWithValue("@auditLogId", auditLogId);

                var result = await command.ExecuteScalarAsync();
                if (result == null || result == DBNull.Value) return null;
                return Convert.ToInt32(result) == 1;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"IsSyncedAsync failed for {auditLogId}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Diagnostic counts of unsent audit-log entries broken down by why each is being held.
    /// Used by AuditLogMailDispatchService to log a clear reason when dispatch finds 0 ready entries.
    /// </summary>
    public class MailDispatchDiagnostics
    {
        public int TotalUnsent { get; set; }
        public int UnsyncedCount { get; set; }
        public int WaitingOnEvidence { get; set; }
        /// <summary>
        /// Number of unsent audit entries whose linked evidence has failed >= max_retries.
        /// These are PERMANENTLY stuck — GetPendingUploadsAsync filters them out so they
        /// will never be retried and the audit entry will never reach mail dispatch.
        /// </summary>
        public int StuckEvidence { get; set; }
        public int WaitingOnHold { get; set; }
    }
}
