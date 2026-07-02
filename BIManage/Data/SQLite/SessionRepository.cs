using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Common.Helpers;
using BIManage.Core.Sessions;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Crash;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for session management and tracking
    /// Handles Revit session lifecycle, document tracking, and heartbeat monitoring
    /// </summary>
    public class SessionRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private string _currentSessionId;
        private bool _disposed;

        public SessionRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
            EnsureSchemaExists();
        }

        private void EnsureSchemaExists()
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();

                    // Check if sessions table already exists (indicates schema was loaded by SchemaMigration)
                    var checkTableCmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'", connection);
                    var sessionsExists = checkTableCmd.ExecuteScalar() != null;

                    if (sessionsExists)
                    {
                        _logger?.LogDebug("Session tables already exist - schema was initialized by SchemaMigration");
                        return; // Schema already loaded, no need to reload
                    }

                    _logger?.LogInfo("Sessions table does not exist - initializing schema from SessionRepository");

                    var assemblyDir = System.IO.Path.GetDirectoryName(typeof(SessionRepository).Assembly.Location);

                    // Load Schema_Persistence.sql FIRST (creates schema_version and session tables)
                    // Then Schema.sql (creates rules table — references schema_version via INSERT)
                    var persistenceSchemaPath = System.IO.Path.Combine(assemblyDir, "BIManage", "Data", "SQLite", "Schema_Persistence.sql");
                    var baseSchemaPath = System.IO.Path.Combine(assemblyDir, "BIManage", "Data", "SQLite", "Schema.sql");

                    // Load persistence schema first (creates schema_version, sessions, events tables)
                    if (System.IO.File.Exists(persistenceSchemaPath))
                    {
                        var persistenceSchemaSql = System.IO.File.ReadAllText(persistenceSchemaPath);
                        using (var command = new SQLiteCommand(persistenceSchemaSql, connection))
                        {
                            command.ExecuteNonQuery();
                        }
                        _logger?.LogInfo("Persistence schema (Schema_Persistence.sql) loaded successfully");
                    }
                    else
                    {
                        _logger?.LogWarning($"Schema_Persistence.sql not found at: {persistenceSchemaPath}");
                        _logger?.LogInfo("Attempting to create tables directly...");

                        // Fallback: Create tables directly if schema file not found
                        CreateTablesDirectly(connection);
                        return;
                    }

                    // Load base schema second (creates rules, audit, protection tables)
                    if (System.IO.File.Exists(baseSchemaPath))
                    {
                        var baseSchemaSql = System.IO.File.ReadAllText(baseSchemaPath);
                        using (var command = new SQLiteCommand(baseSchemaSql, connection))
                        {
                            command.ExecuteNonQuery();
                        }
                        _logger?.LogInfo("Base schema (Schema.sql) loaded successfully");
                    }
                    else
                    {
                        _logger?.LogWarning($"Schema.sql not found at: {baseSchemaPath}");
                    }

                    _logger?.LogInfo("Session persistence schema verified");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to ensure session schema: {ex.Message}", ex);
                throw;
            }
        }

        private void CreateTablesDirectly(SQLiteConnection connection)
        {
            // Create schema version table first
            var createSchemaVersion = @"
                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER PRIMARY KEY NOT NULL
                );
                INSERT OR REPLACE INTO schema_version (version) VALUES (1);
            ";

            using (var command = new SQLiteCommand(createSchemaVersion, connection))
            {
                command.ExecuteNonQuery();
            }

            // Create sessions table directly as fallback — keep in sync with Schema_Persistence.sql
            var createSessionsTable = @"
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY NOT NULL,

                    -- Machine & Process
                    machine_id TEXT NULL,
                    process_id INTEGER NULL,

                    -- User
                    username TEXT NOT NULL,
                    revit_username TEXT NULL,
                    user_email TEXT NULL,
                    computer_name TEXT NOT NULL,

                    -- Revit Environment
                    revit_version TEXT NOT NULL,
                    revit_build TEXT NULL,
                    desktop_connector_version TEXT NULL,
                    bimanage_version TEXT NULL,
                    autodesk_addins INTEGER NULL,
                    external_addins INTEGER NULL,
                    loaded_plugin_count INTEGER NULL,
                    journal_file_name TEXT NULL,

                    -- Lifecycle Timing
                    started_at TEXT NOT NULL,
                    opened_at TEXT NULL,
                    opening_duration_seconds REAL NULL,
                    ended_at TEXT NULL,
                    closed_at TEXT NULL,

                    -- Status & Counters
                    status TEXT NOT NULL DEFAULT 'Active',
                    is_active INTEGER NOT NULL DEFAULT 1,
                    crash_detected INTEGER NOT NULL DEFAULT 0,
                    total_commands INTEGER NOT NULL DEFAULT 0,
                    total_events INTEGER NOT NULL DEFAULT 0,
                    last_heartbeat TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS document_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    model_guid TEXT NULL,
                    central_model_path TEXT NULL,
                    central_model_name TEXT NULL,
                    model_location TEXT NULL,
                    is_local INTEGER NOT NULL DEFAULT 0,
                    document_title TEXT NOT NULL,
                    project_name TEXT NULL,
                    cloud_project_name TEXT NULL,
                    opened_at TEXT NOT NULL,
                    opening_started_at TEXT NULL,
                    opening_duration_seconds REAL NULL,
                    interactive_ready_seconds REAL NULL,
                    opened_worksets_count INTEGER NULL,
                    closed_at TEXT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    is_crashed INTEGER NOT NULL DEFAULT 0,
                    total_modifications INTEGER NOT NULL DEFAULT 0,
                    last_saved_at TEXT NULL,
                    created_by TEXT NULL,
                    modified_by TEXT NULL,
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS session_heartbeats (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    active_document_id TEXT NULL,
                    memory_usage_percent REAL NULL,
                    cpu_usage_percent REAL NULL,
                    disk_usage_percent REAL NULL,
                    graphics_usage_percent REAL NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    modified_at TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_sessions_active ON sessions(is_active);
                CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_at DESC);
            ";

            using (var command = new SQLiteCommand(createSessionsTable, connection))
            {
                command.ExecuteNonQuery();
            }

            _logger?.LogInfo("Session tables created directly (fallback method)");
        }

        #region Session Management

        /// <summary>
        /// Detect and mark crashed sessions on startup
        /// Should be called before starting a new session
        /// </summary>
        public async Task<System.Collections.Generic.List<string>> DetectCrashedSessionsAsync(int heartbeatTimeoutMinutes = 5)
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                var currentMachineId = MachineIdentifier.GetMachineId(_logger);
                var currentProcessId = ProcessHelper.GetCurrentProcessId();

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Step 1: Find potentially crashed sessions on THIS MACHINE ONLY
                    var getCandidatesSql = @"
                        SELECT session_id, process_id
                        FROM sessions
                        WHERE is_active = 1
                        AND machine_id = @machineId
                        AND datetime(last_heartbeat) < datetime('now', @timeout)";

                    var candidates = new System.Collections.Generic.List<(string sessionId, int? processId)>();

                    using (var cmd = new SQLiteCommand(getCandidatesSql, connection))
                    {
                        cmd.Parameters.AddWithValue("@machineId", currentMachineId);
                        cmd.Parameters.AddWithValue("@timeout", $"-{heartbeatTimeoutMinutes} minutes");

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var sessionId = reader.GetString(0);
                                var processId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                                candidates.Add((sessionId, processId));
                            }
                        }
                    }

                    // Step 2: Verify each candidate - only mark as crashed if process is truly dead
                    foreach (var (sessionId, processId) in candidates)
                    {
                        // If no process ID stored (old sessions), mark as crashed based on heartbeat alone
                        if (!processId.HasValue)
                        {
                            result.Add(sessionId);
                            continue;
                        }

                        // If current process owns the session, DO NOT mark as crashed
                        // (Handles edge case where heartbeat hasn't updated yet)
                        if (processId.Value == currentProcessId)
                        {
                            continue;
                        }

                        // Verify process is actually dead before marking as crashed
                        if (!ProcessHelper.IsProcessRunning(processId.Value))
                        {
                            result.Add(sessionId);
                        }
                    }

                    // Step 3: Mark verified crashed sessions
                    if (result.Count == 0)
                    {
                        return result;
                    }

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            foreach (var sessionId in result)
                            {
                                // 3a. Read last_heartbeat for use as ended_at timestamp
                                string lastHeartbeat;
                                using (var hbTimeCmd = new SQLiteCommand(
                                    "SELECT last_heartbeat FROM sessions WHERE session_id = @sid", connection, transaction))
                                {
                                    hbTimeCmd.Parameters.AddWithValue("@sid", sessionId);
                                    lastHeartbeat = (hbTimeCmd.ExecuteScalar() as string) ?? DateTime.UtcNow.ToString("o");
                                }

                                // 3b. Analyze journal file for crash evidence
                                var (journalFile, revitVersion) = GetSessionJournalInfo(connection, transaction, sessionId);
                                var evidence = RevitJournalCrashDetector.Analyze(journalFile, revitVersion);
                                var isCrash    = evidence.IsCrashed;
                                var statusCode = isCrash ? (int)SessionStatusCode.Crashed : (int)SessionStatusCode.Unknown;
                                var statusText = isCrash ? "Crashed" : "Unknown";
                                var logLabel   = isCrash
                                    ? (evidence.IsDefinitiveCrash
                                        ? $"Crashed (keyword: {evidence.KeywordFound})"
                                        : "Crashed (no clean exit)")
                                    : "Unknown (no crash evidence)";

                                // 3c. Find which document was active at last heartbeat
                                string? activeDocId = null;
                                using (var hbCmd = new SQLiteCommand(
                                    @"SELECT active_document_id FROM session_heartbeats
                                      WHERE session_id = @sid ORDER BY timestamp DESC LIMIT 1",
                                    connection, transaction))
                                {
                                    hbCmd.Parameters.AddWithValue("@sid", sessionId);
                                    activeDocId = hbCmd.ExecuteScalar() as string;
                                }

                                // 3d. Update session row
                                using (var upCmd = new SQLiteCommand(
                                    @"UPDATE sessions
                                      SET crash_detected = @isCrash,
                                          is_active = 0,
                                          status = @status,
                                          status_code = @statusCode,
                                          ended_at = @endedAt
                                      WHERE session_id = @sid",
                                    connection, transaction))
                                {
                                    upCmd.Parameters.AddWithValue("@isCrash",    isCrash ? 1 : 0);
                                    upCmd.Parameters.AddWithValue("@status",     statusText);
                                    upCmd.Parameters.AddWithValue("@statusCode", statusCode);
                                    upCmd.Parameters.AddWithValue("@endedAt",    lastHeartbeat);
                                    upCmd.Parameters.AddWithValue("@sid",        sessionId);
                                    upCmd.ExecuteNonQuery();
                                }

                                // 3e. Update document_sessions
                                //     If Crashed AND we know the active model → mark that doc as Crashed, others as Closed
                                //     If Unknown OR no activeDocId → mark all as Closed
                                if (isCrash && !string.IsNullOrEmpty(activeDocId))
                                {
                                    // Active model at crash time → Crashed (status_code = 2, is_crashed = 1)
                                    using (var cmCrash = new SQLiteCommand(
                                        @"UPDATE document_sessions
                                          SET is_active = 0, closed_at = @endedAt, status_code = 2, is_crashed = 1
                                          WHERE session_id = @sid AND is_active = 1
                                            AND (model_guid = @docId OR document_id = @docId)",
                                        connection, transaction))
                                    {
                                        cmCrash.Parameters.AddWithValue("@endedAt", lastHeartbeat);
                                        cmCrash.Parameters.AddWithValue("@sid",     sessionId);
                                        cmCrash.Parameters.AddWithValue("@docId",   activeDocId);
                                        cmCrash.ExecuteNonQuery();
                                    }

                                    // All other open documents → Closed (status_code = 0)
                                    using (var cmOther = new SQLiteCommand(
                                        @"UPDATE document_sessions
                                          SET is_active = 0, closed_at = @endedAt, status_code = 0, is_crashed = 0
                                          WHERE session_id = @sid AND is_active = 1",
                                        connection, transaction))
                                    {
                                        cmOther.Parameters.AddWithValue("@endedAt", lastHeartbeat);
                                        cmOther.Parameters.AddWithValue("@sid",     sessionId);
                                        cmOther.ExecuteNonQuery();
                                    }
                                }
                                else
                                {
                                    // Unknown session or no heartbeat data → all docs Closed (status_code = 0)
                                    using (var cmAll = new SQLiteCommand(
                                        @"UPDATE document_sessions
                                          SET is_active = 0, closed_at = @endedAt, status_code = 0, is_crashed = 0
                                          WHERE session_id = @sid AND is_active = 1",
                                        connection, transaction))
                                    {
                                        cmAll.Parameters.AddWithValue("@endedAt", lastHeartbeat);
                                        cmAll.Parameters.AddWithValue("@sid",     sessionId);
                                        cmAll.ExecuteNonQuery();
                                    }
                                }

                                _logger?.LogWarning($"Session {sessionId} marked as {logLabel}");
                            }

                            transaction.Commit();
                            _logger?.LogWarning($"Detected {result.Count} dead session(s) on machine {currentMachineId} — closed orphaned document_sessions");
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            _logger?.LogError($"Failed to mark crashed sessions: {ex.Message}", ex);
                            throw;
                        }
                    }

                    // === Second pass: re-verify recently-closed sessions ===
                    // Catches the case where Revit's crash handler ran OnShutdown() and marked
                    // the session as "Closed" before the process died. The journal may show crash
                    // keywords even though the session was "gracefully" closed.
                    await ReclassifyMismarkedCrashedSessionsAsync(connection, currentMachineId);

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to detect crashed sessions: {ex.Message}", ex);
                return result;
            }
        }

        /// <summary>
        /// Re-checks sessions that were closed in the last 30 minutes on this machine.
        /// If their journal shows crash evidence but they were marked as "Closed",
        /// upgrades them to "Crashed". This catches the OnShutdown-ran-during-crash scenario.
        /// </summary>
        private async Task ReclassifyMismarkedCrashedSessionsAsync(SQLiteConnection connection, string machineId)
        {
            try
            {
                // Find sessions closed in the last 30 minutes that are marked Closed (not Crashed)
                var sql = @"
                    SELECT session_id, journal_file_name, revit_version
                    FROM sessions
                    WHERE is_active = 0
                      AND machine_id = @machineId
                      AND (status = 'Closed' OR status_code = 0)
                      AND crash_detected = 0
                      AND closed_at IS NOT NULL
                      AND datetime(closed_at) > datetime('now', '-30 minutes')
                      AND journal_file_name IS NOT NULL
                      AND revit_version IS NOT NULL";

                var candidates = new System.Collections.Generic.List<(string sessionId, string journalFile, string revitVersion)>();

                using (var cmd = new SQLiteCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@machineId", machineId);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            candidates.Add((
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetString(2)
                            ));
                        }
                    }
                }

                if (candidates.Count == 0) return;

                foreach (var (sessionId, journalFile, revitVersion) in candidates)
                {
                    try
                    {
                        var evidence = RevitJournalCrashDetector.Analyze(journalFile, revitVersion);
                        if (!evidence.IsDefinitiveCrash) continue;

                        // Upgrade from Closed → Crashed
                        using (var upCmd = new SQLiteCommand(
                            @"UPDATE sessions
                              SET crash_detected = 1,
                                  status = 'Crashed',
                                  status_code = @statusCode
                              WHERE session_id = @sid",
                            connection))
                        {
                            upCmd.Parameters.AddWithValue("@statusCode", (int)SessionStatusCode.Crashed);
                            upCmd.Parameters.AddWithValue("@sid", sessionId);
                            upCmd.ExecuteNonQuery();
                        }

                        // Also update the active document_session at crash time (from last heartbeat)
                        string? activeDocId = null;
                        using (var hbCmd = new SQLiteCommand(
                            @"SELECT active_document_id FROM session_heartbeats
                              WHERE session_id = @sid ORDER BY timestamp DESC LIMIT 1",
                            connection))
                        {
                            hbCmd.Parameters.AddWithValue("@sid", sessionId);
                            activeDocId = hbCmd.ExecuteScalar() as string;
                        }

                        if (!string.IsNullOrEmpty(activeDocId))
                        {
                            using (var docCmd = new SQLiteCommand(
                                @"UPDATE document_sessions
                                  SET status_code = 2, is_crashed = 1
                                  WHERE session_id = @sid
                                    AND (model_guid = @docId OR document_id = @docId)",
                                connection))
                            {
                                docCmd.Parameters.AddWithValue("@sid", sessionId);
                                docCmd.Parameters.AddWithValue("@docId", activeDocId);
                                docCmd.ExecuteNonQuery();
                            }
                        }

                        _logger?.LogWarning($"[ReclassifyCrash] Session {sessionId} upgraded from Closed → Crashed (keyword: {evidence.KeywordFound})");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"[ReclassifyCrash] Failed to reclassify {sessionId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ReclassifyCrash] Post-hoc verification failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads journal_file_name and revit_version from the sessions row for a given sessionId.
        /// Used within DetectCrashedSessionsAsync to locate the Revit journal for crash analysis.
        /// </summary>
        private (string? journalFileName, string? revitVersion) GetSessionJournalInfo(
            SQLiteConnection conn, SQLiteTransaction transaction, string sessionId)
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT journal_file_name, revit_version FROM sessions WHERE session_id = @sid",
                    conn, transaction))
                {
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var journalFile  = reader.IsDBNull(0) ? null : reader.GetString(0);
                            var revitVersion = reader.IsDBNull(1) ? null : reader.GetString(1);
                            return (journalFile, revitVersion);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Could not read journal info for session {sessionId}: {ex.Message}");
            }
            return (null, null);
        }

        /// <summary>
        /// Get model_guids for document sessions belonging to a given session_id.
        /// Used to sync "Crashed" status to model sessions API.
        /// </summary>
        public async Task<List<string>> GetDocumentSessionModelGuidsAsync(string sessionId)
        {
            var modelGuids = new List<string>();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT DISTINCT model_guid
                        FROM document_sessions
                        WHERE session_id = @sessionId
                        AND model_guid IS NOT NULL AND model_guid != ''";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelGuids.Add(reader.GetString(0));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get model GUIDs for session {sessionId}: {ex.Message}", ex);
            }
            return modelGuids;
        }

        /// <summary>
        /// Detect and mark inactive sessions (no heartbeat for 4+ hours but not crashed)
        /// Should be called periodically to maintain accurate session status
        /// </summary>
        public async Task<int> DetectInactiveSessionsAsync(int inactiveTimeoutHours = 4)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE sessions
                        SET status = 'Inactive', status_code = 3
                        WHERE is_active = 1
                        AND status = 'Active'
                        AND crash_detected = 0
                        AND datetime(last_heartbeat) < datetime('now', @timeout)";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@timeout", $"-{inactiveTimeoutHours} hours");
                        var inactiveCount = await command.ExecuteNonQueryAsync();

                        if (inactiveCount > 0)
                        {
                            _logger?.LogInfo($"Marked {inactiveCount} session(s) as Inactive (no heartbeat for {inactiveTimeoutHours}+ hours)");
                        }

                        return inactiveCount;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to detect inactive sessions: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Start a new Revit session
        /// </summary>
        public async Task<RevitSession> StartSessionAsync(
            string sessionId,
            string revitVersion,
            string revitBuild,
            string username,
            string revitUsername,
            string userEmail,
            string computerName)
        {
            try
            {
                // Get machine ID based on hardware
                var machineId = MachineIdentifier.GetMachineId(_logger);
                // Get current process ID for session ownership
                var processId = ProcessHelper.GetCurrentProcessId();

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO sessions (
                            session_id, machine_id, process_id, started_at, revit_version, revit_build,
                            username, revit_username, user_email, computer_name,
                            is_active, status, status_code, last_heartbeat
                        ) VALUES (
                            @sessionId, @machineId, @processId, @startedAt, @revitVersion, @revitBuild,
                            @username, @revitUsername, @userEmail, @computerName,
                            1, 'Active', 1, @lastHeartbeat
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var now = DateTime.UtcNow;
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@machineId", machineId);
                        command.Parameters.AddWithValue("@processId", processId);
                        command.Parameters.AddWithValue("@startedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@revitVersion", revitVersion);
                        command.Parameters.AddWithValue("@revitBuild", revitBuild ?? string.Empty);
                        command.Parameters.AddWithValue("@username", username);
                        command.Parameters.AddWithValue("@revitUsername", revitUsername ?? string.Empty);
                        command.Parameters.AddWithValue("@userEmail", userEmail ?? string.Empty);
                        command.Parameters.AddWithValue("@computerName", computerName);
                        command.Parameters.AddWithValue("@lastHeartbeat", now.ToString("o"));

                        await command.ExecuteNonQueryAsync();
                    }
                }

                // Store current session ID for disposal
                _currentSessionId = sessionId;

                _logger?.LogInfo($"Session started: {sessionId} (Machine: {machineId}, Process: {processId}, Windows: {username}, Revit: {revitUsername ?? "N/A"}, Version: {revitVersion})");

                return new RevitSession
                {
                    SessionId = sessionId,
                    MachineId = machineId,
                    ProcessId = processId,
                    StartedAt = DateTime.UtcNow,
                    RevitVersion = revitVersion,
                    RevitBuild = revitBuild,
                    Username = username,
                    RevitUsername = revitUsername,
                    UserEmail = userEmail,
                    ComputerName = computerName,
                    IsActive = true,
                    Status = "Active"
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to start session: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// End a Revit session
        /// </summary>
        public async Task<bool> EndSessionAsync(string sessionId, bool crashDetected = false)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Wrap session end and document closure in a transaction
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            var now = DateTime.UtcNow;

                            // Update session - ended_at marks when close initiated
                            // closed_at will be set when shutdown completes
                            var status = crashDetected ? "Crashed" : "Closed";
                            var statusCode = crashDetected ? (int)SessionStatusCode.Crashed : (int)SessionStatusCode.Closed;
                            var updateSessionSql = @"
                                UPDATE sessions
                                SET ended_at = @endedAt,
                                    closed_at = @closedAt,
                                    is_active = 0,
                                    crash_detected = @crashDetected,
                                    status = @status,
                                    status_code = @statusCode
                                WHERE session_id = @sessionId";

                            using (var command = new SQLiteCommand(updateSessionSql, connection, transaction))
                            {
                                command.Parameters.AddWithValue("@endedAt", now.ToString("o"));
                                command.Parameters.AddWithValue("@closedAt", now.ToString("o"));
                                command.Parameters.AddWithValue("@crashDetected", crashDetected ? 1 : 0);
                                command.Parameters.AddWithValue("@status", status);
                                command.Parameters.AddWithValue("@statusCode", statusCode);
                                command.Parameters.AddWithValue("@sessionId", sessionId);
                                await command.ExecuteNonQueryAsync();
                            }

                            // Close all active documents (in same transaction)
                            // modified_by populated from session's revit_username via subquery
                            var closeDocsSql = @"
                                UPDATE document_sessions
                                SET closed_at = @closedAt,
                                    is_active = 0,
                                    status_code = 0,
                                    modified_by = (SELECT revit_username FROM sessions WHERE session_id = @sessionId)
                                WHERE session_id = @sessionId AND is_active = 1";

                            using (var command = new SQLiteCommand(closeDocsSql, connection, transaction))
                            {
                                command.Parameters.AddWithValue("@closedAt", now.ToString("o"));
                                command.Parameters.AddWithValue("@sessionId", sessionId);
                                await command.ExecuteNonQueryAsync();
                            }

                            // Commit transaction - both operations succeed or both fail
                            transaction.Commit();
                        }
                        catch
                        {
                            // Rollback on any error
                            transaction.Rollback();
                            throw;
                        }
                    }
                }

                _logger?.LogInfo($"Session ended: {sessionId} (Crash: {crashDetected})");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to end session: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Update session when Revit is ready for user input (ApplicationInitialized)
        /// Updates: opened_at, opening_duration_seconds, revit_username, loaded_plugin_count
        /// </summary>
        public async Task<bool> UpdateSessionReadyAsync(
            string sessionId,
            string revitUsername,
            int? loadedPluginCount,
            string journalFileName,
            int? autodeskAddins = null,
            int? externalAddins = null)
        {
            try
            {
                _logger?.LogDebug($">>> UpdateSessionReadyAsync: Start (SessionId={sessionId})");

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    _logger?.LogDebug($">>> Opening database connection: {_connectionString}");
                    await connection.OpenAsync();
                    _logger?.LogDebug(">>> Database connection opened");

                    // Use actual Revit process start time (most accurate, no file I/O)
                    var revitStartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
                    _logger?.LogDebug($">>> Revit process start time: {revitStartedAt:o}");

                    // Calculate raw opening duration
                    var openedAt = DateTime.UtcNow;
                    var rawDurationSeconds = (openedAt - revitStartedAt).TotalSeconds;

                    // Subtract time spent on modal dialogs (plugin warnings, license prompts, etc.)
                    var dialogSeconds = BIManage.Revit.Timing.StartupDialogTracker.TotalDialogSeconds;
                    var openingDurationSeconds = rawDurationSeconds - dialogSeconds;
                    if (openingDurationSeconds < 0) openingDurationSeconds = rawDurationSeconds;

                    // Cap at ~15 minutes to filter outliers (user walked away during startup)
                    const double MaxOpeningDurationSeconds = 891; // ~14m 51s — avoids looking like a hard round cap
                    if (openingDurationSeconds > MaxOpeningDurationSeconds)
                        openingDurationSeconds = MaxOpeningDurationSeconds;

                    _logger?.LogInfo($">>> Opening duration: {openingDurationSeconds:F1}s (raw: {rawDurationSeconds:F1}s, dialog time subtracted: {dialogSeconds:F1}s)");

                    // Extract just the filename from the full path
                    var journalFileNameOnly = !string.IsNullOrEmpty(journalFileName)
                        ? System.IO.Path.GetFileName(journalFileName)
                        : null;

                    // Update session with corrected started_at (from journal file)
                    // This ensures started_at reflects actual Revit launch, not add-in bootstrap
                    var sql = @"
                        UPDATE sessions
                        SET started_at = @startedAt,
                            opened_at = @openedAt,
                            opening_duration_seconds = @openingDuration,
                            revit_username = @revitUsername,
                            loaded_plugin_count = @loadedPluginCount,
                            autodesk_addins = @autodeskAddins,
                            external_addins = @externalAddins,
                            journal_file_name = @journalFileName
                        WHERE session_id = @sessionId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@startedAt", revitStartedAt.ToString("o"));
                        command.Parameters.AddWithValue("@openedAt", openedAt.ToString("o"));
                        command.Parameters.AddWithValue("@openingDuration", openingDurationSeconds);
                        command.Parameters.AddWithValue("@revitUsername", revitUsername ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@loadedPluginCount", loadedPluginCount.HasValue ? (object)loadedPluginCount.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@autodeskAddins", autodeskAddins.HasValue ? (object)autodeskAddins.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@externalAddins", externalAddins.HasValue ? (object)externalAddins.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@journalFileName", journalFileNameOnly ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        _logger?.LogDebug($">>> Executing UPDATE with: started_at={revitStartedAt:o}, opened_at={openedAt:o}, duration={openingDurationSeconds:F2}s");

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        _logger?.LogDebug($">>> UPDATE affected {rowsAffected} rows");

                        if (rowsAffected > 0)
                        {
                            _logger?.LogInfo($"Session ready: {sessionId} (Duration: {openingDurationSeconds:F1}s, User: {revitUsername ?? "N/A"}, Plugins: {loadedPluginCount?.ToString() ?? "N/A"}, Journal: {journalFileNameOnly ?? "N/A"})");
                            return true;
                        }
                        else
                        {
                            _logger?.LogWarning($"Session not found for ready update: {sessionId}");
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update session ready: {ex.Message}", ex);
                _logger?.LogError($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Helper to get session start time from database (fallback when journal not available)
        /// </summary>
        private async Task<DateTime> GetSessionStartTimeAsync(SQLiteConnection connection, string sessionId)
        {
            var getStartSql = "SELECT started_at FROM sessions WHERE session_id = @sessionId";
            using (var getCmd = new SQLiteCommand(getStartSql, connection))
            {
                getCmd.Parameters.AddWithValue("@sessionId", sessionId);
                var result = await getCmd.ExecuteScalarAsync();
                if (result == null)
                {
                    _logger?.LogWarning($"Session not found in database: {sessionId}, using current time");
                    return DateTime.UtcNow;
                }
                return DateTime.ParseExact(
                    result.ToString(),
                    "o",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
            }
        }

        /// <summary>
        /// Update session with BIManage and Desktop Connector versions
        /// </summary>
        public async Task<bool> UpdateSessionVersionsAsync(
            string sessionId,
            string bimanageVersion,
            string desktopConnectorVersion)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE sessions
                        SET bimanage_version = @bimanageVersion,
                            desktop_connector_version = @desktopConnectorVersion
                        WHERE session_id = @sessionId";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@bimanageVersion", bimanageVersion ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@desktopConnectorVersion", desktopConnectorVersion ?? (object)DBNull.Value);

                        var updated = await cmd.ExecuteNonQueryAsync();
                        if (updated > 0)
                        {
                            _logger?.LogInfo($"Session versions updated: {sessionId} (BIManage: {bimanageVersion ?? "N/A"}, Desktop Connector: {desktopConnectorVersion ?? "N/A"})");
                            return true;
                        }
                        else
                        {
                            _logger?.LogWarning($"Session not found for version update: {sessionId}");
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update session versions: {ex.Message}", ex);
                return false;
            }
        }

        // Tracks which sessions have already had their first heartbeat row inserted this process lifetime.
        // First call per session_id → INSERT (sets created_at). Subsequent calls → UPDATE (updates modified_at).
        private static readonly System.Collections.Generic.HashSet<string> _heartbeatInsertedSessions = new();
        private static readonly object _heartbeatLock = new();

        /// <summary>
        /// Record session heartbeat for health monitoring.
        /// Uses UPSERT semantics: INSERTs on first call per session so <c>created_at</c> reflects session
        /// start, then UPDATEs the same row on each subsequent call so <c>modified_at</c> reflects the
        /// latest heartbeat time. Memory, CPU, disk, and GPU metrics are populated when available.
        /// </summary>
        public async Task<bool> RecordHeartbeatAsync(
            string sessionId,
            string activeDocumentId = null,
            double? memoryUsagePercent = null,
            double? cpuUsagePercent = null,
            double? diskUsagePercent = null,
            double? graphicsUsagePercent = null)
        {
            try
            {
                bool isFirstInsert;
                lock (_heartbeatLock)
                    isFirstInsert = _heartbeatInsertedSessions.Add(sessionId);

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            var now = DateTime.UtcNow;
                            var nowStr = now.ToString("o");

                            // 1. Update sessions.last_heartbeat
                            using (var cmd = new SQLiteCommand(
                                "UPDATE sessions SET last_heartbeat = @ts WHERE session_id = @sid",
                                connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@ts", nowStr);
                                cmd.Parameters.AddWithValue("@sid", sessionId);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            if (isFirstInsert)
                            {
                                // 2a. First call for this session — INSERT row (created_at = now)
                                var insertSql = @"
                                    INSERT INTO session_heartbeats (
                                        session_id, timestamp, active_document_id,
                                        memory_usage_percent, cpu_usage_percent, disk_usage_percent, graphics_usage_percent,
                                        created_at, modified_at
                                    ) VALUES (
                                        @sid, @ts, @docId,
                                        @memPct, @cpuPct, @diskPct, @gpuPct,
                                        @now, @now
                                    )";

                                using (var cmd = new SQLiteCommand(insertSql, connection, transaction))
                                {
                                    cmd.Parameters.AddWithValue("@sid", sessionId);
                                    cmd.Parameters.AddWithValue("@ts", nowStr);
                                    cmd.Parameters.AddWithValue("@docId", activeDocumentId ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@memPct", memoryUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@cpuPct", cpuUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@diskPct", diskUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@gpuPct", graphicsUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@now", nowStr);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                // 2b. Subsequent call — UPDATE the existing row, modified_at advances each time
                                var updateSql = @"
                                    UPDATE session_heartbeats
                                    SET timestamp              = @ts,
                                        active_document_id     = @docId,
                                        memory_usage_percent   = @memPct,
                                        cpu_usage_percent      = @cpuPct,
                                        disk_usage_percent     = @diskPct,
                                        graphics_usage_percent = @gpuPct,
                                        modified_at            = @now
                                    WHERE id = (
                                        SELECT id FROM session_heartbeats
                                        WHERE session_id = @sid
                                        ORDER BY id DESC LIMIT 1
                                    )";

                                using (var cmd = new SQLiteCommand(updateSql, connection, transaction))
                                {
                                    cmd.Parameters.AddWithValue("@sid", sessionId);
                                    cmd.Parameters.AddWithValue("@ts", nowStr);
                                    cmd.Parameters.AddWithValue("@docId", activeDocumentId ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@memPct", memoryUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@cpuPct", cpuUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@diskPct", diskUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@gpuPct", graphicsUsagePercent ?? (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@now", nowStr);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            transaction.Commit();
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to record heartbeat: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get active sessions (for crash detection)
        /// </summary>
        public async Task<List<RevitSession>> GetActiveSessionsAsync()
        {
            var sessions = new List<RevitSession>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT session_id, started_at, ended_at, revit_version, revit_build,
                               username, revit_username, user_email, computer_name, is_active, crash_detected,
                               total_commands, total_events, last_heartbeat
                        FROM sessions
                        WHERE is_active = 1
                        ORDER BY started_at DESC";

                    using (var command = new SQLiteCommand(sql, connection))
                    using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            sessions.Add(ReadSession(reader));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active sessions: {ex.Message}", ex);
            }

            return sessions;
        }

        /// <summary>
        /// Get a specific session by ID
        /// </summary>
        public async Task<RevitSession?> GetSessionAsync(string sessionId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT session_id, started_at, ended_at, revit_version, revit_build,
                               username, revit_username, user_email, computer_name, is_active, crash_detected,
                               total_commands, total_events, last_heartbeat
                        FROM sessions
                        WHERE session_id = @sessionId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return ReadSession(reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get session {sessionId}: {ex.Message}", ex);
            }

            return null;
        }

            /// <summary>
        /// Get a session with ALL columns including versions and addin counts (for API sync)
        /// </summary>
        public async Task<RevitSession?> GetSessionFullAsync(string sessionId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT session_id, started_at, ended_at, revit_version, revit_build,
                               username, revit_username, user_email, computer_name, is_active, crash_detected,
                               total_commands, total_events, last_heartbeat, machine_id, process_id,
                               opened_at, opening_duration_seconds, closed_at, status, loaded_plugin_count, journal_file_name,
                               desktop_connector_version, bimanage_version, autodesk_addins, external_addins, status_code
                        FROM sessions
                        WHERE session_id = @sessionId";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var session = ReadSessionFull(reader);
                                // Read additional columns (index 22-25)
                                session.DesktopConnectorVersion = reader.IsDBNull(22) ? null : reader.GetString(22);
                                session.BimanageVersion = reader.IsDBNull(23) ? null : reader.GetString(23);
                                session.AutodeskAddins = reader.IsDBNull(24) ? null : (int?)reader.GetInt32(24);
                                session.ExternalAddins = reader.IsDBNull(25) ? null : (int?)reader.GetInt32(25);
                                session.StatusCode = reader.IsDBNull(26) ? null : (int?)reader.GetInt32(26);
                                return session;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get full session {sessionId}: {ex.Message}", ex);
            }

            return null;
        }

    #endregion

        #region Document Session Management

        /// <summary>
        /// Open a document in current session
        /// </summary>
        public async Task<DocumentSession> OpenDocumentAsync(
            string sessionId,
            string documentId,
            string documentPath,
            string documentTitle,
            bool isWorkshared,
            bool isFamily,
            string createdBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO document_sessions (
                            session_id, document_id, model_location, document_title,
                            opened_at, is_active, created_by
                        ) VALUES (
                            @sessionId, @documentId, @modelLocation, @documentTitle,
                            @openedAt, 1, @createdBy
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var now = DateTime.UtcNow;
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@documentId", documentId);
                        command.Parameters.AddWithValue("@modelLocation", documentPath);
                        command.Parameters.AddWithValue("@documentTitle", documentTitle);
                        command.Parameters.AddWithValue("@openedAt", now.ToString("o"));
                        command.Parameters.AddWithValue("@createdBy", createdBy ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Document opened: {documentTitle} (ID: {documentId})");

                return new DocumentSession
                {
                    SessionId = sessionId,
                    DocumentId = documentId,
                    DocumentPath = documentPath,
                    DocumentTitle = documentTitle,
                    IsWorkshared = isWorkshared,
                    IsFamily = isFamily,
                    OpenedAt = DateTime.UtcNow,
                    IsActive = true
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to open document: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Close a document in current session
        /// </summary>
        public async Task<bool> CloseDocumentAsync(string sessionId, string documentId, string modifiedBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE document_sessions
                        SET closed_at = @closedAt,
                            is_active = 0,
                            modified_by = @modifiedBy
                        WHERE session_id = @sessionId AND document_id = @documentId AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@closedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@documentId", documentId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Document closed: {documentId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to close document: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Close all documents in session
        /// </summary>
        private async Task CloseAllDocumentsAsync(string sessionId, string modifiedBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE document_sessions
                        SET closed_at = @closedAt,
                            is_active = 0,
                            modified_by = @modifiedBy
                        WHERE session_id = @sessionId AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@closedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to close all documents: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Update document saved timestamp for local (non-workshared) models only.
        /// For workshared models, last_saved_at is updated via RecordDocumentSyncAsync on sync,
        /// not on save, because saves to local cache don't represent authoritative model state.
        /// </summary>
        /// <param name="sessionId">The session ID</param>
        /// <param name="documentId">The document ID</param>
        /// <param name="isWorkshared">Whether the document is workshared (if true, last_saved_at is NOT updated)</param>
        public async Task<bool> RecordDocumentSaveAsync(string sessionId, string documentId, bool isWorkshared = false, string modifiedBy = null)
        {
            // For workshared models, save to local cache should not update last_saved_at
            // Only sync to central should update last_saved_at (via RecordDocumentSyncAsync)
            if (isWorkshared)
            {
                _logger?.LogDebug($"Skipping last_saved_at update for workshared model save (use Sync instead)");
                return true;
            }

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE document_sessions
                        SET last_saved_at = @savedAt,
                            modified_by = @modifiedBy
                        WHERE session_id = @sessionId AND document_id = @documentId AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@savedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@documentId", documentId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to record document save: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Update document saved timestamp when a workshared model syncs with central.
        /// This is the authoritative "last saved" timestamp for workshared models.
        /// For local models, use RecordDocumentSaveAsync instead.
        /// </summary>
        /// <param name="sessionId">The session ID</param>
        /// <param name="documentId">The document ID</param>
        public async Task<bool> RecordDocumentSyncAsync(string sessionId, string documentId, string modifiedBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Update last_saved_at on sync for workshared models
                    // This represents the authoritative save to central model
                    var sql = @"
                        UPDATE document_sessions
                        SET last_saved_at = @savedAt,
                            modified_by = @modifiedBy
                        WHERE session_id = @sessionId AND document_id = @documentId AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@savedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@documentId", documentId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogDebug($"Recorded document sync for session {sessionId}, document {documentId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to record document sync: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Create a document session record in database
        /// </summary>
        public async Task<bool> CreateDocumentSessionAsync(
            string sessionId,
            string documentId,
            string modelLocation,
            string documentTitle,
            string projectName,
            string cloudProjectName,
            DateTime? openingStartedAt = null,
            double? openingDurationSeconds = null,
            string modelGuid = null,
            string centralModelPath = null,
            string centralModelName = null,
            int? openedWorksetsCount = null,
            bool isLocal = false,
            string createdBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO document_sessions (
                            session_id, document_id, model_location, document_title,
                            model_guid, central_model_path, central_model_name,
                            project_name, cloud_project_name, is_local,
                            opened_at, opening_started_at, opening_duration_seconds,
                            opened_worksets_count, is_active, created_by
                        ) VALUES (
                            @sessionId, @documentId, @modelLocation, @documentTitle,
                            @modelGuid, @centralModelPath, @centralModelName,
                            @projectName, @cloudProjectName, @isLocal,
                            @openedAt, @openingStartedAt, @openingDurationSeconds,
                            @openedWorksetsCount, 1, @createdBy
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@documentId", documentId);
                        command.Parameters.AddWithValue("@modelLocation", modelLocation ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@documentTitle", documentTitle ?? "Untitled");
                        command.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@centralModelPath", centralModelPath ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@centralModelName", centralModelName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@projectName", projectName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@cloudProjectName", cloudProjectName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@isLocal", isLocal ? 1 : 0);
                        command.Parameters.AddWithValue("@openedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@openingStartedAt", openingStartedAt?.ToString("o") ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@openingDurationSeconds", openingDurationSeconds ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@openedWorksetsCount", openedWorksetsCount ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@createdBy", createdBy ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Document session created in database: {documentTitle} (isLocal: {isLocal})");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to create document session: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Close a document session in database
        /// </summary>
        public async Task<bool> CloseDocumentSessionAsync(string documentId, string modifiedBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE document_sessions
                        SET closed_at = @closedAt,
                            is_active = 0,
                            modified_by = @modifiedBy
                        WHERE document_id = @documentId
                        AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@closedAt", DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@documentId", documentId);

                        await command.ExecuteNonQueryAsync();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to close document session: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Update document session with interactive ready time (called when ViewActivated fires)
        /// </summary>
        public async Task<bool> UpdateDocumentInteractiveReadyTimeAsync(
            string documentId,
            double interactiveReadySeconds,
            string modifiedBy = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE document_sessions
                        SET interactive_ready_seconds = @interactiveReadySeconds,
                            modified_by = @modifiedBy
                        WHERE document_id = @documentId
                        AND is_active = 1
                        AND interactive_ready_seconds IS NULL";  // Only update if not already set

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@interactiveReadySeconds", interactiveReadySeconds);
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@documentId", documentId);

                        var rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            _logger?.LogInfo($"Updated interactive ready time for document {documentId}: {interactiveReadySeconds:F2}s");
                            return true;
                        }
                        else
                        {
                            _logger?.LogDebug($"No rows updated for interactive ready time (document may already have value or be closed)");
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update interactive ready time: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Atomically increment the post-open Worksets-dialog idle accumulator on the active
        /// document_sessions row identified by (sessionId, modelGuid). Returns the new total
        /// in seconds, or null on miss/failure. Used by WorksetsCommandBinding after each
        /// Worksets-dialog close.
        /// </summary>
        public async Task<double?> IncrementWorksetOpenTimeAsync(string sessionId, string modelGuid, double additionalSeconds, string modifiedBy = null)
        {
            return await IncrementIdleAccumulatorAsync(
                sessionId, modelGuid, additionalSeconds, "total_workset_open_seconds", modifiedBy);
        }

        /// <summary>
        /// Atomically increment the post-open Manage-Links idle accumulator on the active
        /// document_sessions row identified by (sessionId, modelGuid). Returns the new total
        /// in seconds, or null on miss/failure. Used by ManageLinksCommandBinding after each
        /// Manage-Links-dialog close.
        /// </summary>
        public async Task<double?> IncrementLinkLoadTimeAsync(string sessionId, string modelGuid, double additionalSeconds, string modifiedBy = null)
        {
            return await IncrementIdleAccumulatorAsync(
                sessionId, modelGuid, additionalSeconds, "total_link_load_seconds", modifiedBy);
        }

        private async Task<double?> IncrementIdleAccumulatorAsync(
            string sessionId, string modelGuid, double additionalSeconds, string column, string modifiedBy)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(modelGuid)) return null;
            if (additionalSeconds <= 0 || double.IsNaN(additionalSeconds) || double.IsInfinity(additionalSeconds)) return null;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Column name comes from a private literal — never user input — so direct
                    // interpolation is safe; SQLite parameters can't bind column identifiers.
                    var sql = $@"
                        UPDATE document_sessions
                        SET {column} = COALESCE({column}, 0) + @delta,
                            modified_by = COALESCE(@modifiedBy, modified_by)
                        WHERE session_id = @sessionId
                          AND model_guid = @modelGuid
                          AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@delta", additionalSeconds);
                        command.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@sessionId", sessionId);
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);

                        var rowsAffected = await command.ExecuteNonQueryAsync();
                        if (rowsAffected == 0)
                        {
                            _logger?.LogDebug($"Idle accumulator update missed: no active document_sessions row for session={sessionId}, model={modelGuid} (column={column})");
                            return null;
                        }
                    }

                    using (var read = new SQLiteCommand(
                        $@"SELECT {column} FROM document_sessions
                           WHERE session_id = @sessionId AND model_guid = @modelGuid AND is_active = 1",
                        connection))
                    {
                        read.Parameters.AddWithValue("@sessionId", sessionId);
                        read.Parameters.AddWithValue("@modelGuid", modelGuid);
                        var result = await read.ExecuteScalarAsync();
                        if (result == null || result == DBNull.Value) return null;
                        return Convert.ToDouble(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to increment {column}: {ex.Message}", ex);
                return null;
            }
        }

        #endregion

        #region Query Methods for UI

        /// <summary>
        /// Get active documents for a session
        /// </summary>
        public async Task<List<DocumentSession>> GetActiveDocumentsAsync(string sessionId)
        {
            var documents = new List<DocumentSession>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, session_id, document_id,
                               COALESCE(central_model_path, model_location, '') as document_path,
                               document_title,
                               model_location, project_name, cloud_project_name,
                               is_local, is_local, opened_at, closed_at,
                               is_active, total_modifications, last_saved_at, is_crashed
                        FROM document_sessions
                        WHERE session_id = @sessionId AND is_active = 1
                        ORDER BY opened_at DESC";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@sessionId", sessionId);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                documents.Add(ReadDocumentSession(reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active documents: {ex.Message}", ex);
            }

            return documents;
        }

        /// <summary>
        /// Get last N crashed sessions
        /// </summary>
        public async Task<List<RevitSession>> GetCrashedSessionsAsync(int limit = 3)
        {
            var sessions = new List<RevitSession>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT session_id, started_at, ended_at, revit_version, revit_build,
                               username, revit_username, user_email, computer_name, is_active, crash_detected,
                               total_commands, total_events, last_heartbeat, machine_id, process_id,
                               opened_at, opening_duration_seconds, closed_at, status, loaded_plugin_count, journal_file_name
                        FROM sessions
                        WHERE crash_detected = 1
                        ORDER BY started_at DESC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                sessions.Add(ReadSessionFull(reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get crashed sessions: {ex.Message}", ex);
            }

            return sessions;
        }

        /// <summary>
        /// Get last N sessions regardless of status (Active, Crashed, Closed)
        /// </summary>
        public async Task<List<RevitSession>> GetRecentSessionsAsync(int limit = 50)
        {
            var sessions = new List<RevitSession>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT session_id, started_at, ended_at, revit_version, revit_build,
                               username, revit_username, user_email, computer_name, is_active, crash_detected,
                               total_commands, total_events, last_heartbeat, machine_id, process_id,
                               opened_at, opening_duration_seconds, closed_at, status, loaded_plugin_count, journal_file_name
                        FROM sessions
                        ORDER BY started_at DESC
                        LIMIT @limit";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = (SQLiteDataReader)await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                sessions.Add(ReadSessionFull(reader));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get recent sessions: {ex.Message}", ex);
            }

            return sessions;
        }

        private DocumentSession ReadDocumentSession(SQLiteDataReader reader)
        {
            return new DocumentSession
            {
                Id = reader.GetInt32(0),
                SessionId = reader.GetString(1),
                DocumentId = reader.GetString(2),
                DocumentPath = reader.GetString(3),
                DocumentTitle = reader.GetString(4),
                ModelLocation = reader.IsDBNull(5) ? null : reader.GetString(5),
                ProjectName = reader.IsDBNull(6) ? null : reader.GetString(6),
                CloudProjectName = reader.IsDBNull(7) ? null : reader.GetString(7),
                IsWorkshared = reader.GetInt32(8) == 1,
                IsFamily = reader.GetInt32(9) == 1,
                OpenedAt = DateTime.Parse(reader.GetString(10)),
                ClosedAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                IsActive = reader.GetInt32(12) == 1,
                TotalModifications = reader.GetInt32(13),
                LastSavedAt = reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14)),
                IsCrashed = reader.FieldCount > 15 && !reader.IsDBNull(15) && reader.GetInt32(15) == 1
            };
        }

        private RevitSession ReadSessionFull(SQLiteDataReader reader)
        {
            return new RevitSession
            {
                SessionId = reader.GetString(0),
                StartedAt = DateTime.Parse(reader.GetString(1)),
                EndedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                RevitVersion = reader.GetString(3),
                RevitBuild = reader.IsDBNull(4) ? null : reader.GetString(4),
                Username = reader.GetString(5),
                RevitUsername = reader.IsDBNull(6) ? null : reader.GetString(6),
                UserEmail = reader.IsDBNull(7) ? null : reader.GetString(7),
                ComputerName = reader.GetString(8),
                IsActive = reader.GetInt32(9) == 1,
                CrashDetected = reader.GetInt32(10) == 1,
                TotalCommands = reader.GetInt32(11),
                TotalEvents = reader.GetInt32(12),
                LastHeartbeat = DateTime.Parse(reader.GetString(13)),
                MachineId = reader.IsDBNull(14) ? null : reader.GetString(14),
                ProcessId = reader.IsDBNull(15) ? null : (int?)reader.GetInt32(15),
                OpenedAt = reader.IsDBNull(16) ? null : DateTime.Parse(reader.GetString(16)),
                OpeningDurationSeconds = reader.IsDBNull(17) ? null : (double?)reader.GetDouble(17),
                ClosedAt = reader.IsDBNull(18) ? null : DateTime.Parse(reader.GetString(18)),
                Status = reader.IsDBNull(19) ? null : reader.GetString(19),
                LoadedPluginCount = reader.IsDBNull(20) ? null : (int?)reader.GetInt32(20),
                JournalFileName = reader.IsDBNull(21) ? null : reader.GetString(21)
            };
        }

        #endregion

        #region Helper Methods

        private RevitSession ReadSession(SQLiteDataReader reader)
        {
            return new RevitSession
            {
                SessionId = reader.GetString(0),
                StartedAt = DateTime.Parse(reader.GetString(1)),
                EndedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                RevitVersion = reader.GetString(3),
                RevitBuild = reader.IsDBNull(4) ? null : reader.GetString(4),
                Username = reader.GetString(5),
                RevitUsername = reader.IsDBNull(6) ? null : reader.GetString(6),
                UserEmail = reader.IsDBNull(7) ? null : reader.GetString(7),
                ComputerName = reader.GetString(8),
                IsActive = reader.GetInt32(9) == 1,
                CrashDetected = reader.GetInt32(10) == 1,
                TotalCommands = reader.GetInt32(11),
                TotalEvents = reader.GetInt32(12),
                LastHeartbeat = DateTime.Parse(reader.GetString(13))
            };
        }

        #endregion

        #region Model Activities Queries

        /// <summary>
        /// Get active session count for a specific model
        /// </summary>
        public async Task<int> GetActiveSessionCountByModelGuidAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return 0;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT COUNT(DISTINCT ds.session_id)
                        FROM document_sessions ds
                        JOIN sessions s ON ds.session_id = s.session_id
                        WHERE ds.model_guid = @modelGuid
                          AND ds.is_active = 1
                          AND s.status = 'Active'";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active session count for model {modelGuid}: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Get active users working on a specific model (by model_guid)
        /// </summary>
        public async Task<List<ModelActiveUser>> GetActiveUsersByModelGuidAsync(string modelGuid)
        {
            var users = new List<ModelActiveUser>();
            if (string.IsNullOrEmpty(modelGuid)) return users;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT s.username, s.revit_username, s.user_email,
                               s.computer_name, ds.opened_at, s.session_id
                        FROM document_sessions ds
                        JOIN sessions s ON ds.session_id = s.session_id
                        WHERE ds.model_guid = @modelGuid
                          AND ds.is_active = 1
                          AND s.status = 'Active'
                        GROUP BY s.username, s.computer_name
                        ORDER BY s.username";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                users.Add(new ModelActiveUser
                                {
                                    Username = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                    RevitUsername = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    UserEmail = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    ComputerName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                    OpenedAt = reader.IsDBNull(4) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(4)),
                                    SessionId = reader.IsDBNull(5) ? "" : reader.GetString(5)
                                });
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Found {users.Count} active users for model {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active users for model {modelGuid}: {ex.Message}", ex);
            }

            return users;
        }

        /// <summary>
        /// Get active users for a model by central model path.
        /// Used for duplicate username detection before the document is opened
        /// (when model GUID is not yet available).
        /// </summary>
        public async Task<List<ModelActiveUser>> GetActiveUsersByModelPathAsync(string modelPath)
        {
            var users = new List<ModelActiveUser>();
            if (string.IsNullOrEmpty(modelPath)) return users;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT s.username, s.revit_username, s.user_email,
                               s.computer_name, ds.opened_at, s.session_id, s.machine_id
                        FROM document_sessions ds
                        JOIN sessions s ON ds.session_id = s.session_id
                        WHERE (ds.central_model_path = @modelPath
                               OR ds.central_model_path = @modelPathAlt)
                          AND ds.is_active = 1
                          AND s.status = 'Active'
                        GROUP BY s.revit_username, s.machine_id
                        ORDER BY s.revit_username";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        // Normalize path separators for matching
                        var normalized = modelPath.Replace("/", "\\");
                        cmd.Parameters.AddWithValue("@modelPath", modelPath);
                        cmd.Parameters.AddWithValue("@modelPathAlt", normalized);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                users.Add(new ModelActiveUser
                                {
                                    Username = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                    RevitUsername = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    UserEmail = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    ComputerName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                    OpenedAt = reader.IsDBNull(4) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(4)),
                                    SessionId = reader.IsDBNull(5) ? "" : reader.GetString(5)
                                });
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Found {users.Count} active users for model path: {modelPath}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active users for model path {modelPath}: {ex.Message}", ex);
            }

            return users;
        }

        #endregion

        #region API Sync Helpers

        /// <summary>
        /// Checks if a session already exists in the local database.
        /// </summary>
        public async Task<bool> SessionExistsAsync(string sessionId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = "SELECT COUNT(1) FROM sessions WHERE session_id = @sessionId";
                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check session existence: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Inserts a session from API response into local DB (INSERT OR IGNORE to skip duplicates).
        /// </summary>
        public async Task<bool> InsertSessionFromApiAsync(
            string sessionId, string startedAt, string endedAt,
            string revitVersion, string revitBuild,
            string username, string revitUsername, string userEmail,
            string computerName, string status, bool isActive)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        INSERT OR IGNORE INTO sessions (
                            session_id, started_at, ended_at, revit_version, revit_build,
                            username, revit_username, user_email, computer_name,
                            status, is_active, last_heartbeat
                        ) VALUES (
                            @sessionId, @startedAt, @endedAt, @revitVersion, @revitBuild,
                            @username, @revitUsername, @userEmail, @computerName,
                            @status, @isActive, @lastHeartbeat
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@startedAt", startedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@endedAt", endedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@revitVersion", revitVersion ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@revitBuild", revitBuild ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@username", username ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@revitUsername", revitUsername ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@userEmail", userEmail ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@computerName", computerName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@status", status ?? "Unknown");
                        cmd.Parameters.AddWithValue("@isActive", isActive ? 1 : 0);
                        cmd.Parameters.AddWithValue("@lastHeartbeat", startedAt ?? DateTime.UtcNow.ToString("o"));

                        var rows = await cmd.ExecuteNonQueryAsync();
                        return rows > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert session from API: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Checks if a document session already exists by sessionId + modelGuid combination.
        /// </summary>
        public async Task<bool> DocumentSessionExistsAsync(string sessionId, string modelGuid)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = "SELECT COUNT(1) FROM document_sessions WHERE session_id = @sessionId AND model_guid = @modelGuid";
                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check document session existence: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Inserts a document session from API response (INSERT OR IGNORE to skip duplicates).
        /// </summary>
        public async Task<bool> InsertDocumentSessionFromApiAsync(
            string sessionId, string modelGuid, string centralModelPath,
            string centralModelName, string modelLocation, string documentTitle,
            string projectName, string cloudProjectName,
            string openedAt, string openingStartedAt, double? openingDurationSeconds,
            int? openedWorksetsCount, string closedAt, bool isActive,
            int? totalModifications, string lastSavedAt,
            string createdBy, string modifiedBy)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        INSERT OR IGNORE INTO document_sessions (
                            session_id, model_guid, central_model_path, central_model_name,
                            model_location, document_title, project_name, cloud_project_name,
                            opened_at, opening_started_at, opening_duration_seconds,
                            opened_worksets_count, closed_at, is_active,
                            total_modifications, last_saved_at, created_by, modified_by
                        ) VALUES (
                            @sessionId, @modelGuid, @centralModelPath, @centralModelName,
                            @modelLocation, @documentTitle, @projectName, @cloudProjectName,
                            @openedAt, @openingStartedAt, @openingDurationSeconds,
                            @openedWorksetsCount, @closedAt, @isActive,
                            @totalModifications, @lastSavedAt, @createdBy, @modifiedBy
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", sessionId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@centralModelPath", centralModelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@centralModelName", centralModelName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelLocation", modelLocation ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@documentTitle", documentTitle ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@projectName", projectName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@cloudProjectName", cloudProjectName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@openedAt", openedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@openingStartedAt", openingStartedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@openingDurationSeconds", openingDurationSeconds ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@openedWorksetsCount", openedWorksetsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@closedAt", closedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@isActive", isActive ? 1 : 0);
                        cmd.Parameters.AddWithValue("@totalModifications", totalModifications ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@lastSavedAt", lastSavedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdBy", createdBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modifiedBy", modifiedBy ?? (object)DBNull.Value);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        return rows > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert document session from API: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region Shutdown & Disposal

        /// <summary>
        /// Returns journal_file_name and revit_version for the current session.
        /// Used during shutdown to check for crash evidence before closing.
        /// </summary>
        public (string? journalFileName, string? revitVersion) GetCurrentSessionJournalInfo()
        {
            if (string.IsNullOrEmpty(_currentSessionId))
                return (null, null);

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT journal_file_name, revit_version FROM sessions WHERE session_id = @sid",
                        connection))
                    {
                        cmd.Parameters.AddWithValue("@sid", _currentSessionId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var journalFile = reader.IsDBNull(0) ? null : reader.GetString(0);
                                var revitVersion = reader.IsDBNull(1) ? null : reader.GetString(1);
                                return (journalFile, revitVersion);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Could not read journal info for current session: {ex.Message}");
            }
            return (null, null);
        }

        /// <summary>
        /// Gracefully shutdown the session repository.
        /// Automatically checks journal for crash evidence before closing.
        /// Call this BEFORE Dispose() during application shutdown.
        /// </summary>
        public async Task ShutdownAsync(bool crashDetected = false)
        {
            if (_disposed || string.IsNullOrEmpty(_currentSessionId))
                return;

            try
            {
                // If caller didn't detect a crash, check the journal for crash evidence.
                // Revit's crash handler often calls OnShutdown before the process dies,
                // so the journal may already contain crash keywords at this point.
                if (!crashDetected)
                {
                    try
                    {
                        var (journalFile, revitVersion) = GetCurrentSessionJournalInfo();
                        var evidence = RevitJournalCrashDetector.Analyze(journalFile, revitVersion);
                        if (evidence.IsDefinitiveCrash)
                        {
                            crashDetected = true;
                            _logger?.LogWarning($"[ShutdownAsync] Journal shows crash evidence (keyword: {evidence.KeywordFound}) — marking as Crashed");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"[ShutdownAsync] Journal check failed: {ex.Message}");
                    }
                }

                await EndSessionAsync(_currentSessionId, crashDetected);
                _logger?.LogInfo($"Session shutdown complete: {_currentSessionId} (Crash: {crashDetected})");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during session shutdown: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // DO NOT call async methods here - causes deadlocks during Revit shutdown
            // Use ShutdownAsync() before calling Dispose()

            _disposed = true;
            _logger?.LogDebug("SessionRepository disposed");
        }

        #endregion
    }

    #region Data Models

    /// <summary>
    /// Revit session model
    /// </summary>
    public class RevitSession
    {
        public string SessionId { get; set; }
        public string MachineId { get; set; }
        public int? ProcessId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? OpenedAt { get; set; }
        public double? OpeningDurationSeconds { get; set; }
        public DateTime? EndedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string Status { get; set; }
        public string RevitVersion { get; set; }
        public string RevitBuild { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string UserEmail { get; set; }
        public string ComputerName { get; set; }
        public int? LoadedPluginCount { get; set; }
        public string JournalFileName { get; set; }
        public string DesktopConnectorVersion { get; set; }
        public string BimanageVersion { get; set; }
        public int? AutodeskAddins { get; set; }
        public int? ExternalAddins { get; set; }
        public int? StatusCode { get; set; }
        public bool IsActive { get; set; } // Deprecated, use Status field
        public bool CrashDetected { get; set; }
        public int TotalCommands { get; set; }
        public int TotalEvents { get; set; }
        public DateTime LastHeartbeat { get; set; }

        public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;

        public TimeSpan? OpeningDuration => OpeningDurationSeconds.HasValue
            ? TimeSpan.FromSeconds(OpeningDurationSeconds.Value)
            : null;

        public TimeSpan? ClosingDuration => (ClosedAt.HasValue && EndedAt.HasValue)
            ? ClosedAt.Value - EndedAt.Value
            : null;

        public bool IsHealthy(TimeSpan heartbeatTimeout)
        {
            return IsActive && (DateTime.UtcNow - LastHeartbeat) < heartbeatTimeout;
        }
    }

    /// <summary>
    /// Document session model
    /// </summary>
    public class DocumentSession
    {
        public int Id { get; set; }
        public string SessionId { get; set; }
        public string DocumentId { get; set; }
        public string DocumentPath { get; set; }
        public string DocumentTitle { get; set; }
        public string ModelLocation { get; set; }
        public string ProjectName { get; set; }
        public string CloudProjectName { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsFamily { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool IsActive { get; set; }
        public bool IsCrashed { get; set; }
        public int TotalModifications { get; set; }
        public DateTime? LastSavedAt { get; set; }

        public TimeSpan OpenDuration => (ClosedAt ?? DateTime.UtcNow) - OpenedAt;
    }

    /// <summary>
    /// Active user on a model (for Model Activities dialog)
    /// </summary>
    public class ModelActiveUser
    {
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string UserEmail { get; set; }
        public string ComputerName { get; set; }
        public DateTime OpenedAt { get; set; }
    }

    #endregion
}
