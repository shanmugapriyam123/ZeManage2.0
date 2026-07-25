using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Handles automatic schema migration for SQLite database.
    /// v1 = production schema — all tables at final structure from SQL files.
    /// Future migrations: add MigrateToV2/V3 methods and bump TargetVersion.
    /// </summary>
    public class SchemaMigration
    {
        private readonly string _databasePath;
        private readonly ILogger? _logger;

        /// <summary>Current target schema version.</summary>
        private const int TargetVersion = 4;

        /// <summary>Set to true after MigrateToLatest() succeeds so repositories can skip EnsureSchemaExists().</summary>
        internal static bool SchemaReady { get; private set; } = false;

        /// <summary>
        /// Background migration task started by <see cref="KickOff"/>. Repository
        /// EnsureSchemaExists implementations can await this if they need a guarantee
        /// that schema work is done before they open their first connection.
        /// </summary>
        internal static Task<bool>? MigrationTask { get; private set; }

        private static readonly object _kickOffLock = new object();

        /// <summary>
        /// Starts <see cref="MigrateToLatest"/> on a background thread (once per process).
        /// Called from bootstrap to move the ~440ms SQLite cold-start + schema check off
        /// the OnStartup critical path. Safe to call multiple times — only the first call
        /// actually starts the task. Repos that race the migration fall through to their
        /// own idempotent EnsureSchemaExists fallback (CREATE TABLE IF NOT EXISTS).
        /// </summary>
        public static void KickOff(string databasePath, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(databasePath)) return;
            lock (_kickOffLock)
            {
                if (MigrationTask != null) return;
                var migration = new SchemaMigration(databasePath, logger);
                MigrationTask = Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var result = migration.MigrateToLatest();
                    sw.Stop();
                    logger?.LogInfo($"[SchemaMigration] Background migration completed in {sw.ElapsedMilliseconds}ms (success={result})");
                    return result;
                });
            }
        }

        /// <summary>
        /// Fast-path check that skips the full migration when the DB was already verified
        /// at the current target schema version by this plugin version on a previous run.
        ///
        /// <para>How it works: after a successful <see cref="MigrateToLatest"/> we write a
        /// small marker file next to the DB containing the target schema version + the
        /// plugin's informational version. On the next launch this method reads that marker
        /// synchronously (no SQLite open, no PRAGMA queries — just a ~1 ms File.ReadAllText)
        /// and, if the marker still matches expectations and the DB file still exists,
        /// declares the schema ready immediately. The 350+ ms SQLite cold-start that the
        /// migration would otherwise incur is pushed off the OnStartup critical path to
        /// whenever a repo actually needs to read or write something.</para>
        ///
        /// <para>Returns true → the caller MUST NOT call <see cref="KickOff"/>; SchemaReady
        /// has been flipped to true and repos that gate on it can proceed.</para>
        /// <para>Returns false → the caller proceeds with the normal background KickOff
        /// path. The marker is regenerated after the next successful migration.</para>
        ///
        /// <para>Reasons the marker is rejected (any one of these forces a full migration):
        /// <list type="bullet">
        ///   <item>Marker file missing (first run, or DB was reset)</item>
        ///   <item>DB file missing (something deleted the SQLite file)</item>
        ///   <item>Marker's schema version != current <c>TargetVersion</c> (plugin bumped TargetVersion)</item>
        ///   <item>Marker's plugin version != currently-loaded plugin version (plugin upgraded — new defensive checks may need to run)</item>
        ///   <item>Marker is older than the DB file (DB modified out-of-band, e.g. someone restored from backup)</item>
        /// </list></para>
        /// </summary>
        public static bool TryFastPath(string databasePath, string currentPluginVersion, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(databasePath)) return false;
            try
            {
                if (!File.Exists(databasePath))
                {
                    logger?.LogDebug("[SchemaMigration] Fast path: DB file missing — falling back to full migration");
                    return false;
                }

                var markerPath = databasePath + ".verified";
                if (!File.Exists(markerPath))
                {
                    logger?.LogDebug("[SchemaMigration] Fast path: marker missing — falling back to full migration");
                    return false;
                }

                // Marker must be at least as new as the DB file. If someone restored a
                // backup or another process touched the DB outside our schema-write path,
                // we re-verify from scratch.
                var markerInfo = new FileInfo(markerPath);
                var dbInfo = new FileInfo(databasePath);
                if (markerInfo.LastWriteTimeUtc < dbInfo.LastWriteTimeUtc.AddSeconds(-5))
                {
                    logger?.LogInfo("[SchemaMigration] Fast path: marker older than DB — falling back to full migration");
                    return false;
                }

                var content = File.ReadAllText(markerPath).Trim();
                // Marker format: "schema=<int>|plugin=<string>"
                // Simple key=value pipe-delimited so it's resilient to whitespace/encoding
                // without taking a dependency on a JSON serializer for ~30 chars of data.
                int markerSchema = -1;
                string? markerPlugin = null;
                foreach (var part in content.Split('|'))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = part.Substring(0, eq).Trim();
                    var v = part.Substring(eq + 1).Trim();
                    if (k == "schema" && int.TryParse(v, out var n)) markerSchema = n;
                    else if (k == "plugin") markerPlugin = v;
                }

                if (markerSchema != TargetVersion)
                {
                    logger?.LogInfo($"[SchemaMigration] Fast path: marker schema={markerSchema} != target={TargetVersion} — falling back to full migration");
                    return false;
                }
                if (!string.Equals(markerPlugin, currentPluginVersion, StringComparison.Ordinal))
                {
                    logger?.LogInfo($"[SchemaMigration] Fast path: plugin upgraded ({markerPlugin} → {currentPluginVersion}) — running full migration once");
                    return false;
                }

                SchemaReady = true;
                logger?.LogInfo($"[SchemaMigration] Fast path: schema verified by plugin {currentPluginVersion} at v{markerSchema} — skipping migration");
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[SchemaMigration] Fast path check failed ({ex.GetType().Name}: {ex.Message}) — falling back to full migration");
                return false;
            }
        }

        /// <summary>
        /// Writes the fast-path marker file after a successful <see cref="MigrateToLatest"/>.
        /// Subsequent runs use <see cref="TryFastPath"/> to short-circuit the migration when
        /// this marker still matches the plugin version + target schema version.
        /// </summary>
        private void WriteFastPathMarker(string pluginVersion)
        {
            try
            {
                var markerPath = _databasePath + ".verified";
                var content = $"schema={TargetVersion}|plugin={pluginVersion}";
                File.WriteAllText(markerPath, content);
                _logger?.LogDebug($"[SchemaMigration] Fast-path marker written ({markerPath})");
            }
            catch (Exception ex)
            {
                // Non-critical — the next launch will just run the full migration again
                _logger?.LogDebug($"[SchemaMigration] Failed to write fast-path marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Blocks until the background migration finishes. Returns true if migration
        /// succeeded (or was never kicked off — caller proceeds with its own EnsureSchemaExists
        /// fallback in that case).
        /// </summary>
        public static bool WaitForReady(int timeoutMs = 30000)
        {
            if (SchemaReady) return true;
            var task = MigrationTask;
            if (task == null) return false;
            try
            {
                return task.Wait(timeoutMs) && task.Result;
            }
            catch
            {
                return false;
            }
        }

        public SchemaMigration(string databasePath, ILogger? logger = null)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
            _logger = logger;
        }

        /// <summary>
        /// Tables that MUST exist for the plugin's core paths (registration, audit, sync, rules,
        /// command protection, background sync) to function. If any are missing on startup we
        /// re-run InitializeFullSchema to rebuild them, regardless of what schema_version reports.
        /// Picked from the table names that recurred as "no such table: X" SQLiteException stacks
        /// when a v3-versioned-but-empty DB was diagnosed in the field.
        /// </summary>
        private static readonly string[] CoreTablesRequired = new[]
        {
            "audit_log",
            "offline_queue",
            "background_sync_settings",
            "rules",
            "command_settings",
            "protection_settings",
            "registered_models",
        };

        /// <summary>
        /// Returns the names of all tables currently present in the SQLite database.
        /// Used by the self-heal check in MigrateToLatest to decide whether to re-run
        /// the full schema script when schema_version reports a version but core tables
        /// are missing (e.g. an aborted earlier migration).
        /// </summary>
        private HashSet<string> GetExistingTableNames(SQLiteConnection connection)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table'", connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var n = reader.GetString(0);
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SchemaMigration] Could not enumerate tables for self-heal check: {ex.Message}");
            }
            return names;
        }

        /// <summary>
        /// Get current schema version from database
        /// </summary>
        public int GetCurrentVersion()
        {
            try
            {
                using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    connection.Open();

                    var checkTableSql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'";
                    using (var checkCmd = new SQLiteCommand(checkTableSql, connection))
                    {
                        var tableExists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
                        if (!tableExists)
                        {
                            _logger?.LogWarning("schema_version table does not exist, assuming version 0");
                            return 0;
                        }
                    }

                    var versionSql = "SELECT version FROM schema_version ORDER BY version DESC LIMIT 1";
                    using (var versionCmd = new SQLiteCommand(versionSql, connection))
                    {
                        var result = versionCmd.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            _logger?.LogWarning("No schema version found, assuming version 0");
                            return 0;
                        }
                        return Convert.ToInt32(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get schema version: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Migrate database to latest schema version.
        /// v0 → v1: Fresh install — load full schema from SQL files + seed event protections.
        /// </summary>
        public bool MigrateToLatest()
        {
            try
            {
                var currentVersion = GetCurrentVersion();
                _logger?.LogInfo($"Current schema version: {currentVersion}");

                // Defensive self-heal: a DB can end up with schema_version reporting v3 (or any
                // value > 0) while the actual application tables don't exist — e.g. an aborted
                // first-run migration that wrote the version row before failing, a manually-edited
                // DB, or AV deletion mid-init. In that state currentVersion > 0 so the v0→v1
                // branch below is skipped, the per-version migrations are also skipped (nothing
                // to migrate from v3 to v3), and the user ends up with every repository failing
                // "no such table: X" forever. Force a re-run of InitializeFullSchema when any
                // critical core table is missing — the SQL files use CREATE TABLE IF NOT EXISTS
                // so this is a no-op when the DB is healthy.
                bool selfHealed = false;
                using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    connection.Open();
                    var existingTables = GetExistingTableNames(connection);
                    var missingCritical = CoreTablesRequired
                        .Where(t => !existingTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (missingCritical.Count > 0 && currentVersion > 0)
                    {
                        _logger?.LogWarning(
                            $"[SchemaMigration] Self-heal triggered: schema_version reports v{currentVersion} " +
                            $"but {missingCritical.Count} critical table(s) are missing: {string.Join(", ", missingCritical)}. " +
                            "Re-running InitializeFullSchema to rebuild missing tables.");
                        InitializeFullSchema(connection);
                        SeedEventProtections();
                        selfHealed = true;
                    }
                }

                if (currentVersion == 0)
                {
                    using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                    {
                        connection.Open();
                        InitializeFullSchema(connection);
                    }

                    SeedEventProtections();
                    currentVersion = 1;
                    _logger?.LogInfo("Database schema initialized at v1");
                }
                else if (selfHealed)
                {
                    _logger?.LogInfo($"[SchemaMigration] Self-heal complete — tables rebuilt, schema_version unchanged at v{currentVersion}");
                }

                // v1 → v2: Chat history persistence tables
                if (currentVersion < 2)
                {
                    MigrateToV2();
                    currentVersion = 2;
                    _logger?.LogInfo("Migrated to v2: chat_sessions + chat_messages tables");
                }

                // v2 → v3: Back-fill model_file_metrics_* tables for DBs that advanced past v0
                // before these tables were added to Schema_Persistence.sql. Idempotent —
                // uses CREATE TABLE IF NOT EXISTS so it's a no-op when the tables already exist.
                if (currentVersion < 3)
                {
                    MigrateToV3();
                    currentVersion = 3;
                    _logger?.LogInfo("Migrated to v3: model_file_metrics_* tables");
                }

                // v3 → v4: Add ze_identity table for MachineId + SID + ZeUserId storage.
                if (currentVersion < 4)
                {
                    MigrateToV4();
                    currentVersion = 4;
                    _logger?.LogInfo("Migrated to v4: ze_identity table");
                }

                // Defensive column checks — run regardless of version number.
                // Handles old v27 databases (from previous schema system) that predate
                // row_hmac, created_by, and renamed heartbeat columns.
                RunDefensiveColumnChecks();

                // Clean up deprecated event protections — runs every startup.
                // Removes old protections that are no longer in the active set.
                CleanupDeprecatedEventProtections();

                // Clean up ownerless sample rules — runs every startup, idempotent.
                // Removes rules with NO company_id AND NO project_id AND NO model_guid
                // — that combination identifies the auto-imported sample rules from
                // BIManage\Rules\Samples\*.json that polluted local SQLite in earlier
                // versions. Legitimate user-authored rules always have at least one
                // of those three scope columns set. Server-fetched rules always have
                // a company_id. Verified in field 2026-05-26: web showed 1 rule
                // (Testing Company), Revit showed 10 — the 9 extras were ownerless
                // samples. This cleanup makes Revit show only API data.
                CleanupOrphanedSampleRules();

                _logger?.LogInfo($"Database schema is at v{currentVersion}");
                SchemaReady = true;

                // Stamp the fast-path marker so the NEXT launch can skip this whole
                // routine. See TryFastPath for the marker semantics. The plugin version
                // is read from this assembly's InformationalVersion so a release bump
                // invalidates the marker exactly once (in case new defensive checks
                // were added in the new build).
                try
                {
                    var pluginVersion = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault()?.InformationalVersion
                        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                        ?? "unknown";
                    WriteFastPathMarker(pluginVersion);
                }
                catch (Exception markerEx)
                {
                    _logger?.LogDebug($"[SchemaMigration] Could not stamp fast-path marker: {markerEx.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Database migration failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Ensures all required columns exist, adding them if missing.
        /// Handles databases from the old v27 schema system that predate HMAC,
        /// created_by, and renamed heartbeat columns. Safe to run on every startup.
        /// </summary>
        /// <summary>
        /// v1 → v2: Create AI chat history tables for persistent conversations.
        /// </summary>
        private void MigrateToV2()
        {
            using var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
            connection.Open();

            const string chatSessionsSql = @"
                CREATE TABLE IF NOT EXISTS chat_sessions (
                    chat_session_id TEXT PRIMARY KEY,
                    session_id TEXT,
                    model_guid TEXT,
                    model_name TEXT,
                    user_name TEXT,
                    started_at TEXT NOT NULL,
                    ended_at TEXT,
                    message_count INTEGER DEFAULT 0,
                    title TEXT
                )";

            const string chatMessagesSql = @"
                CREATE TABLE IF NOT EXISTS chat_messages (
                    chat_message_id TEXT PRIMARY KEY,
                    chat_session_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    character_count INTEGER,
                    estimated_tokens INTEGER,
                    response_time_ms INTEGER,
                    feedback_rating INTEGER,
                    FOREIGN KEY (chat_session_id) REFERENCES chat_sessions(chat_session_id)
                )";

            const string indexSql = @"
                CREATE INDEX IF NOT EXISTS idx_chat_messages_session
                ON chat_messages(chat_session_id)";

            const string updateVersion = @"
                INSERT OR REPLACE INTO schema_version (version)
                VALUES (2)";

            using (var cmd = new SQLiteCommand(chatSessionsSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(chatMessagesSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(indexSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(updateVersion, connection))
                cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// v2 → v3: Create model_file_metrics_* tables for DBs that advanced past v0
        /// before these tables were added to Schema_Persistence.sql. DDL mirrors lines
        /// 330-436 of that file. Uses CREATE TABLE IF NOT EXISTS so it's safe for users
        /// whose tables already exist (the migration is a no-op in that case).
        /// </summary>
        private void MigrateToV3()
        {
            using var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
            connection.Open();

            const string syncSaveTableSql = @"
                CREATE TABLE IF NOT EXISTS model_file_metrics_sync_save (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    capture_id TEXT NOT NULL UNIQUE,
                    session_id TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    model_guid TEXT NULL,
                    model_path TEXT NULL,
                    model_name TEXT NOT NULL,
                    capture_type TEXT NOT NULL,
                    sync_guid TEXT NULL,
                    captured_at TEXT NOT NULL,
                    captured_by TEXT NOT NULL,
                    file_size_bytes INTEGER NULL,
                    levels_count INTEGER NULL,
                    grids_count INTEGER NULL,
                    design_options_count INTEGER NULL,
                    linked_dwg_count INTEGER NULL,
                    imported_dwg_count INTEGER NULL,
                    linked_revit_count INTEGER NULL,
                    raster_images_count INTEGER NULL,
                    warnings_count INTEGER NULL,
                    duplicate_elements_count INTEGER NULL,
                    model_groups_count INTEGER NULL,
                    detail_groups_count INTEGER NULL,
                    total_views_count INTEGER NULL,
                    total_families_count INTEGER NULL,
                    total_worksets_count INTEGER NULL,
                    sheets_count INTEGER NULL,
                    non_native_object_styles_count INTEGER NULL,
                    view_templates_count INTEGER NULL,
                    shared_coord_ns REAL NULL,
                    shared_coord_ew REAL NULL,
                    shared_coord_elevation REAL NULL,
                    shared_coord_unit TEXT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
                    FOREIGN KEY (sync_guid) REFERENCES model_sync(sync_guid) ON DELETE SET NULL
                )";

            const string periodicTableSql = @"
                CREATE TABLE IF NOT EXISTS model_file_metrics_periodic (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    capture_id TEXT NOT NULL UNIQUE,
                    session_id TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    model_guid TEXT NULL,
                    model_path TEXT NULL,
                    model_name TEXT NOT NULL,
                    captured_at TEXT NOT NULL,
                    captured_by TEXT NOT NULL,
                    capture_interval_hours INTEGER NULL DEFAULT 24,
                    is_manual_trigger INTEGER NOT NULL DEFAULT 0,
                    total_elements_count INTEGER NULL,
                    model_elements_count INTEGER NULL,
                    annotative_elements_count INTEGER NULL,
                    inplace_families_count INTEGER NULL,
                    unplaced_rooms_count INTEGER NULL,
                    views_not_on_sheets_count INTEGER NULL,
                    unenclosed_rooms_count INTEGER NULL,
                    walls_not_connected_count INTEGER NULL,
                    pipes_not_connected_count INTEGER NULL,
                    ducts_not_connected_count INTEGER NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
                )";

            const string manualTableSql = @"
                CREATE TABLE IF NOT EXISTS model_file_metrics_manual (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    capture_id TEXT NOT NULL UNIQUE,
                    session_id TEXT NOT NULL,
                    document_id TEXT NOT NULL,
                    model_guid TEXT NULL,
                    model_path TEXT NULL,
                    model_name TEXT NOT NULL,
                    captured_at TEXT NOT NULL,
                    captured_by TEXT NOT NULL,
                    command_source TEXT NULL DEFAULT 'ribbon',
                    capture_reason TEXT NULL,
                    families_over_5mb_count INTEGER NULL,
                    purgeable_elements_count INTEGER NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
                )";

            const string indicesSql = @"
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_session ON model_file_metrics_sync_save(session_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_capture ON model_file_metrics_sync_save(capture_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_model ON model_file_metrics_sync_save(model_guid);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_type ON model_file_metrics_sync_save(capture_type);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_time ON model_file_metrics_sync_save(captured_at DESC);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_sync_save_sync_guid ON model_file_metrics_sync_save(sync_guid) WHERE sync_guid IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_session ON model_file_metrics_periodic(session_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_capture ON model_file_metrics_periodic(capture_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_model ON model_file_metrics_periodic(model_guid);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_time ON model_file_metrics_periodic(captured_at DESC);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_periodic_manual ON model_file_metrics_periodic(is_manual_trigger) WHERE is_manual_trigger = 1;
                CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_session ON model_file_metrics_manual(session_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_capture ON model_file_metrics_manual(capture_id);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_model ON model_file_metrics_manual(model_guid);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_time ON model_file_metrics_manual(captured_at DESC);
                CREATE INDEX IF NOT EXISTS idx_file_metrics_manual_source ON model_file_metrics_manual(command_source);";

            const string updateVersion = @"
                INSERT OR REPLACE INTO schema_version (version)
                VALUES (3)";

            using (var cmd = new SQLiteCommand(syncSaveTableSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(periodicTableSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(manualTableSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(indicesSql, connection))
                cmd.ExecuteNonQuery();
            using (var cmd = new SQLiteCommand(updateVersion, connection))
                cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Removes ownerless sample rules from the local database on every startup.
        /// Idempotent — no-op when the rules table is missing or no orphans remain.
        /// Targets rules where company_id, project_id, AND model_guid are all NULL,
        /// which uniquely identifies sample-JSON imports (legitimate user-authored
        /// rules and server-fetched rules always have at least one scope column set).
        /// </summary>
        private void CleanupOrphanedSampleRules()
        {
            try
            {
                using var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
                connection.Open();

                using (var checkCmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rules'", connection))
                {
                    if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                        return;
                }

                using var deleteCmd = new SQLiteCommand(@"
                    DELETE FROM rules
                    WHERE (company_id IS NULL OR company_id = '')
                      AND (project_id IS NULL OR project_id = '')
                      AND (model_guid IS NULL OR model_guid = '')",
                    connection);
                var deleted = deleteCmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger?.LogInfo($"[SchemaMigration] CleanupOrphanedSampleRules: deleted {deleted} ownerless sample rule(s) (no company_id/project_id/model_guid).");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SchemaMigration] CleanupOrphanedSampleRules failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes deprecated event protections from the local database on every startup.
        /// This ensures old protections don't linger after plugin updates.
        /// </summary>
        private void CleanupDeprecatedEventProtections()
        {
            try
            {
                using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    connection.Open();

                    // Check if table exists
                    using (var checkCmd = new SQLiteCommand(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_protection_settings'", connection))
                    {
                        if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                            return;
                    }

                    using (var cleanupCmd = new SQLiteCommand(@"
                        DELETE FROM event_protection_settings
                        WHERE dummy_command_id IN (
                            'Ze_OpenFileFromNonApprovedProtection',
                            'Ze_FamilyLoadingProtection',
                            'Ze_FamilyVersionMismatch',
                            'Ze_SyncConflictDetection',
                            'Ze_CopyMonitorPinProtection',
                            'Ze_CADImportPinPrompt'
                        )", connection))
                    {
                        var removed = cleanupCmd.ExecuteNonQuery();
                        if (removed > 0)
                            _logger?.LogInfo($"Removed {removed} deprecated event protection(s) from local DB");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to clean up deprecated event protections: {ex.Message}");
            }
        }

        private void RunDefensiveColumnChecks()
        {
            try
            {
                using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    connection.Open();
                    int fixes = 0;

                    // HMAC columns on protection tables
                    fixes += EnsureColumnExists(connection, "command_settings", "row_hmac", "TEXT");
                    fixes += EnsureColumnExists(connection, "event_protection_settings", "row_hmac", "TEXT");
                    fixes += EnsureColumnExists(connection, "audit_log", "row_hmac", "TEXT");
                    fixes += EnsureColumnExists(connection, "pin_protections", "row_hmac", "TEXT");
                    fixes += EnsureColumnExists(connection, "passwords", "row_hmac", "TEXT");

                    // created_by / modified_by on protection tables (from UI branch)
                    fixes += EnsureColumnExists(connection, "event_protection_settings", "created_by", "TEXT");
                    fixes += EnsureColumnExists(connection, "command_settings", "created_by", "TEXT");

                    // Heartbeat columns: rename _mb → _percent if old names exist
                    fixes += RenameColumnIfExists(connection, "session_heartbeats", "memory_usage_mb", "memory_usage_percent");
                    fixes += RenameColumnIfExists(connection, "session_heartbeats", "disk_usage_mb", "disk_usage_percent");
                    fixes += RenameColumnIfExists(connection, "session_heartbeats", "graphics_usage_mb", "graphics_usage_percent");

                    // If new names don't exist either (very old schema), add them
                    fixes += EnsureColumnExists(connection, "session_heartbeats", "memory_usage_percent", "REAL");
                    fixes += EnsureColumnExists(connection, "session_heartbeats", "cpu_usage_percent", "REAL");
                    fixes += EnsureColumnExists(connection, "session_heartbeats", "disk_usage_percent", "REAL");
                    fixes += EnsureColumnExists(connection, "session_heartbeats", "graphics_usage_percent", "REAL");

                    // Audit log mail columns
                    fixes += EnsureColumnExists(connection, "audit_log", "sent_mail", "INTEGER DEFAULT 0");
                    fixes += EnsureColumnExists(connection, "audit_log", "evidence_id", "TEXT");
                    fixes += EnsureColumnExists(connection, "audit_log", "sent_mail_at", "TEXT");

                    // Audit log element metadata
                    fixes += EnsureColumnExists(connection, "audit_log", "element_category", "TEXT");
                    fixes += EnsureColumnExists(connection, "audit_log", "element_family_type", "TEXT");
                    fixes += EnsureColumnExists(connection, "audit_log", "element_name", "TEXT");

                    // Session columns
                    fixes += EnsureColumnExists(connection, "sessions", "machine_id", "TEXT");
                    fixes += EnsureColumnExists(connection, "sessions", "process_id", "INTEGER");
                    fixes += EnsureColumnExists(connection, "sessions", "is_crashed", "INTEGER DEFAULT 0");
                    fixes += EnsureColumnExists(connection, "sessions", "opened_at", "TEXT");
                    fixes += EnsureColumnExists(connection, "sessions", "opening_completed_at", "TEXT");
                    fixes += EnsureColumnExists(connection, "sessions", "opening_duration", "REAL");
                    fixes += EnsureColumnExists(connection, "sessions", "external_addin_names", "TEXT");

                    // Session status code columns (enhanced crash detection)
                    fixes += EnsureColumnExists(connection, "sessions", "status_code", "INTEGER NULL");
                    fixes += EnsureColumnExists(connection, "document_sessions", "status_code", "INTEGER NULL");

                    // Post-open idle waits accumulators on document_sessions
                    fixes += EnsureColumnExists(connection, "document_sessions", "total_workset_open_seconds", "REAL NULL DEFAULT 0");
                    fixes += EnsureColumnExists(connection, "document_sessions", "total_link_load_seconds", "REAL NULL DEFAULT 0");

                    // Background sync: open-views behaviour (added later)
                    fixes += EnsureColumnExists(connection, "background_sync_settings", "open_views_on_sync_mode", "INTEGER NOT NULL DEFAULT 0");
                    fixes += EnsureColumnExists(connection, "background_sync_settings", "prevent_sync_when_views_opened_over", "INTEGER NOT NULL DEFAULT 10");

                    // Shared coordinate display unit label (ft / m / mm / cm) captured from project units
                    fixes += EnsureColumnExists(connection, "model_file_metrics_sync_save", "shared_coord_unit", "TEXT NULL");

                    // Unmonitored user detections table
                    EnsureTableExists(connection, "unmonitored_user_detections", @"
                        CREATE TABLE unmonitored_user_detections (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            model_guid TEXT NOT NULL,
                            revit_username TEXT NOT NULL,
                            detection_source TEXT NOT NULL,
                            detected_at TEXT NOT NULL,
                            synced INTEGER DEFAULT 0,
                            UNIQUE(model_guid, revit_username, detection_source)
                        )");

                    // Normalise legacy audit_log.event_source values to the four ribbon-panel
                    // display strings so server-side filters and UI labels see a consistent set.
                    // One-time UPDATEs guarded by the legacy literal — idempotent, no-ops once
                    // every row has been migrated. Adds to fixes counter for visibility.
                    fixes += NormalizeAuditEventSource(connection);

                    if (fixes > 0)
                        _logger?.LogInfo($"[DefensiveChecks] Applied {fixes} column fix(es) to existing database");
                    else
                        _logger?.LogInfo("[DefensiveChecks] All columns present — no fixes needed");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[DefensiveChecks] Failed: {ex.Message}");
                // Don't throw — allow startup to continue
            }
        }

        /// <summary>
        /// Normalises legacy audit_log.event_source values (CommandProtection, RuleProtection,
        /// PinProtection[:BypassDetection], EventProtection) to the four ribbon-panel display
        /// strings: "Pin Protection", "Command Protection", "Event Restriction", "Rule Management".
        /// Returns the number of rows updated across all four migrations. Idempotent — once a
        /// row has been migrated the WHERE clause never matches it again.
        /// </summary>
        private int NormalizeAuditEventSource(SQLiteConnection conn)
        {
            try
            {
                if (!ColumnExists(conn, "audit_log", "event_source")) return 0;

                int total = 0;
                total += RunNormalizationUpdate(conn, "Pin Protection", "PinProtection");
                // The bypass-detection sub-source previously qualified PinProtection — collapse
                // to the panel name; the per-event detail still lives in the reason column.
                total += RunNormalizationUpdate(conn, "Pin Protection", "PinProtection:BypassDetection");
                total += RunNormalizationUpdate(conn, "Command Protection", "CommandProtection");
                total += RunNormalizationUpdate(conn, "Event Restriction", "EventProtection");
                total += RunNormalizationUpdate(conn, "Rule Management", "RuleProtection");

                // Legacy event-protection rows qualified the source with the DummyCommandId
                // (e.g. "EventProtection:Ze_OpenCentralFileProtection", "EventProtection:Ze_CADImportProtection").
                // Collapse the whole prefix family to "Event Restriction" — the specific
                // protection identity already lives in command_name / protection_id on the row,
                // so no information is lost.
                total += RunNormalizationLikeUpdate(conn, "Event Restriction", "EventProtection:%");

                if (total > 0)
                    _logger?.LogInfo($"[DefensiveChecks] Normalised {total} audit_log.event_source row(s) to ribbon-panel labels");

                return total > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[DefensiveChecks] event_source normalisation skipped: {ex.Message}");
                return 0;
            }
        }

        private int RunNormalizationUpdate(SQLiteConnection conn, string newValue, string oldValue)
        {
            using var cmd = new SQLiteCommand(
                "UPDATE audit_log SET event_source = @new WHERE event_source = @old",
                conn);
            cmd.Parameters.AddWithValue("@new", newValue);
            cmd.Parameters.AddWithValue("@old", oldValue);
            return cmd.ExecuteNonQuery();
        }

        private int RunNormalizationLikeUpdate(SQLiteConnection conn, string newValue, string oldPattern)
        {
            using var cmd = new SQLiteCommand(
                "UPDATE audit_log SET event_source = @new WHERE event_source LIKE @pattern",
                conn);
            cmd.Parameters.AddWithValue("@new", newValue);
            cmd.Parameters.AddWithValue("@pattern", oldPattern);
            return cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Adds a column to a table if it doesn't already exist. Returns 1 if added, 0 if already present.
        /// </summary>
        private int EnsureColumnExists(SQLiteConnection conn, string table, string column, string type)
        {
            try
            {
                if (ColumnExists(conn, table, column)) return 0;

                using (var cmd = new SQLiteCommand($"ALTER TABLE {table} ADD COLUMN {column} {type}", conn))
                    cmd.ExecuteNonQuery();

                _logger?.LogInfo($"[DefensiveChecks] Added {table}.{column} ({type})");
                return 1;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[DefensiveChecks] Skip {table}.{column}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Renames a column if the old name exists and the new name doesn't. Returns 1 if renamed.
        /// </summary>
        private int RenameColumnIfExists(SQLiteConnection conn, string table, string oldName, string newName)
        {
            try
            {
                if (!ColumnExists(conn, table, oldName) || ColumnExists(conn, table, newName))
                    return 0;

                using (var cmd = new SQLiteCommand($"ALTER TABLE {table} RENAME COLUMN {oldName} TO {newName}", conn))
                    cmd.ExecuteNonQuery();

                _logger?.LogInfo($"[DefensiveChecks] Renamed {table}.{oldName} → {newName}");
                return 1;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[DefensiveChecks] Skip rename {table}.{oldName}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Checks if a column exists in a table via PRAGMA table_info.
        /// </summary>
        private static bool ColumnExists(SQLiteConnection conn, string table, string column)
        {
            using (var cmd = new SQLiteCommand($"PRAGMA table_info({table})", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Creates a table if it doesn't exist.
        /// </summary>
        private void EnsureTableExists(SQLiteConnection conn, string tableName, string createSql)
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", conn))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
                }

                using (var cmd = new SQLiteCommand(createSql, conn))
                    cmd.ExecuteNonQuery();

                _logger?.LogInfo($"[DefensiveChecks] Created table {tableName}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[DefensiveChecks] Skip table {tableName}: {ex.Message}");
            }
        }

        #region Schema Initialization

        /// <summary>
        /// Load full schema from SQL files (Schema_Persistence.sql + Schema.sql).
        /// All tables, indices, triggers, and views are defined in SQL — no inline DDL.
        /// </summary>
        private void InitializeFullSchema(SQLiteConnection connection)
        {
            _logger?.LogInfo("Initializing full database schema...");

            try
            {
                // Load from embedded resources (self-contained, no external .sql files needed)
                LoadEmbeddedSql(connection, "BIManage.Data.SQLite.Schema_Persistence.sql");
                LoadEmbeddedSql(connection, "BIManage.Data.SQLite.Schema.sql");

                _logger?.LogInfo("Full schema initialization complete (v1)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initialize full schema: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Loads SQL from an embedded resource in the assembly.
        /// Falls back to file on disk if the resource is not found (dev builds).
        /// </summary>
        private void LoadEmbeddedSql(SQLiteConnection connection, string resourceName)
        {
            var assembly = typeof(SchemaMigration).Assembly;
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var sql = reader.ReadToEnd();
                        var statements = ParseSqlStatements(sql);
                        _logger?.LogInfo($"Executing {statements.Count} SQL statements from embedded {resourceName}");
                        ExecuteStatements(connection, statements, resourceName);
                        return;
                    }
                }
            }

            // Fallback: try loading from disk (development scenario)
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var filePath = Path.Combine(assemblyDir, resourceName.Replace('.', Path.DirectorySeparatorChar));
            // Fix: the last two dots are file.ext, not folder separators
            // e.g., BIManage.Data.SQLite.Schema_Persistence.sql → BIManage\Data\SQLite\Schema_Persistence.sql
            filePath = Path.Combine(assemblyDir,
                resourceName.Substring(0, resourceName.LastIndexOf('.')).Replace('.', Path.DirectorySeparatorChar)
                + resourceName.Substring(resourceName.LastIndexOf('.')));

            if (File.Exists(filePath))
            {
                _logger?.LogInfo($"Resource {resourceName} not embedded, loading from disk: {filePath}");
                LoadSqlFile(connection, filePath);
            }
            else
            {
                _logger?.LogWarning($"SQL resource not found: {resourceName} (also not at {filePath})");
            }
        }

        private void ExecuteStatements(SQLiteConnection connection, List<string> statements, string source)
        {
            int executed = 0;
            foreach (var statement in statements)
            {
                var trimmed = statement.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                try
                {
                    using (var cmd = new SQLiteCommand(trimmed, connection))
                    {
                        cmd.ExecuteNonQuery();
                        executed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to execute statement from {source}: {ex.Message}");
                    _logger?.LogDebug($"Statement: {trimmed.Substring(0, Math.Min(100, trimmed.Length))}...");
                }
            }
            _logger?.LogInfo($"{source}: {executed} statements executed");
        }

        private void LoadSqlFile(SQLiteConnection connection, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            if (!File.Exists(filePath))
            {
                _logger?.LogWarning($"{fileName} not found at {filePath}");
                return;
            }

            var sql = File.ReadAllText(filePath);
            var statements = ParseSqlStatements(sql);

            _logger?.LogInfo($"Executing {statements.Count} SQL statements from {fileName}");

            int executed = 0;
            foreach (var statement in statements)
            {
                var trimmed = statement.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                try
                {
                    using (var cmd = new SQLiteCommand(trimmed, connection))
                    {
                        cmd.ExecuteNonQuery();
                        executed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to execute {fileName} statement: {ex.Message}");
                    _logger?.LogDebug($"Statement: {trimmed.Substring(0, Math.Min(150, trimmed.Length))}...");
                }
            }

            _logger?.LogInfo($"{fileName} loaded ({executed} statements executed)");
        }

        /// <summary>
        /// Parse SQL file into individual statements, handling multi-line statements.
        /// Properly tracks parentheses, quotes, and comments to split only at statement boundaries.
        /// </summary>
        private List<string> ParseSqlStatements(string sql)
        {
            var statements = new List<string>();
            var currentStatement = new System.Text.StringBuilder();
            int parenthesesDepth = 0;
            int beginEndDepth = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                char next = (i + 1 < sql.Length) ? sql[i + 1] : '\0';

                if (!inSingleQuote && !inDoubleQuote && !inBlockComment && c == '-' && next == '-')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (inLineComment && (c == '\n' || c == '\r'))
                {
                    inLineComment = false;
                    currentStatement.Append(c);
                    continue;
                }

                if (inLineComment)
                    continue;

                if (!inSingleQuote && !inDoubleQuote && !inLineComment && c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (inBlockComment && c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    continue;
                }

                if (inBlockComment)
                    continue;

                if (c == '\'' && !inDoubleQuote)
                {
                    if (i > 0 && sql[i - 1] == '\\')
                    {
                        currentStatement.Append(c);
                        continue;
                    }
                    inSingleQuote = !inSingleQuote;
                }

                if (c == '"' && !inSingleQuote)
                {
                    if (i > 0 && sql[i - 1] == '\\')
                    {
                        currentStatement.Append(c);
                        continue;
                    }
                    inDoubleQuote = !inDoubleQuote;
                }

                if (!inSingleQuote && !inDoubleQuote)
                {
                    if (c == '(')
                        parenthesesDepth++;
                    else if (c == ')')
                        parenthesesDepth--;

                    if (IsKeywordAt(sql, i, "BEGIN"))
                        beginEndDepth++;
                    else if (IsKeywordAt(sql, i, "END"))
                    {
                        if (beginEndDepth > 0)
                            beginEndDepth--;
                    }
                }

                currentStatement.Append(c);

                if (c == ';' && parenthesesDepth == 0 && beginEndDepth == 0 && !inSingleQuote && !inDoubleQuote)
                {
                    var stmt = currentStatement.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(stmt))
                        statements.Add(stmt);
                    currentStatement.Clear();
                }
            }

            var final_stmt = currentStatement.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(final_stmt))
                statements.Add(final_stmt);

            return statements;
        }

        private static bool IsKeywordAt(string sql, int position, string keyword)
        {
            if (position + keyword.Length > sql.Length)
                return false;

            for (int k = 0; k < keyword.Length; k++)
            {
                if (char.ToUpperInvariant(sql[position + k]) != keyword[k])
                    return false;
            }

            if (position > 0 && IsWordChar(sql[position - 1]))
                return false;

            int afterPos = position + keyword.Length;
            if (afterPos < sql.Length && IsWordChar(sql[afterPos]))
                return false;

            return true;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        #endregion

        #region Event Protection Seeding

        /// <summary>
        /// Seed default event protections.
        /// All protections are disabled by default — admin must enable them.
        /// </summary>
        private void SeedEventProtections()
        {
            _logger?.LogInfo("Seeding default event protections...");

            try
            {
                using (var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    connection.Open();

                    using (var checkCmd = new SQLiteCommand(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_protection_settings'", connection))
                    {
                        if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                        {
                            _logger?.LogWarning("event_protection_settings table does not exist, skipping seed");
                            return;
                        }
                    }

                    // eventType: 1=DocumentOpening, 2=DocumentSaving, 3=FamilyLoading, 4=DocumentPrinting, 5=DocumentChanged, 6=CommandProtection
                    var eventProtections = new (int eventType, string dummyCommandId, string protectionName, int isCompanyLevel, string configJson)[]
                    {
                        // --- Model Access & Security (DocumentOpening = 1) ---
                        (1, "Ze_DuplicateUserSessionProtection", "Duplicate Username in Same Model", 1,
                            @"{""blockDuplicateUsername"":true,""checkApiForRemoteSessions"":true}"),
                        (1, "Ze_OpenCentralFileProtection", "Open Central File Directly", 1,
                            @"{""warnOnCentralAccess"":true,""blockDirectCentralOpen"":false}"),
                        (1, "Ze_ModelUpgradeProtection", "Model Upgrade Protection", 0,
                            @"{""protectProjectFiles"":true,""protectFamilyFiles"":true,""minimumVersionDifference"":1}"),

                        // --- File Operations ---
                        (2, "Ze_SaveOverEarlierFileVersionProtection", "Save Over Earlier File Version", 0,
                            @"{""warnOnDowngrade"":true,""blockDowngrade"":false}"),
                        (4, "Ze_DocumentPrintingProtection", "Document Printing Protection", 0,
                            @"{""blockAllPrinting"":false,""requireApprovalForPdf"":false}"),
                        (6, "Ze_DocumentExportingProtection", "Document Exporting Protection", 0,
                            @"{""blockAllExports"":false,""blockedFormats"":[],""allowedFormats"":[]}"),
                        (6, "Ze_TransferProjectStandardsProtection", "Transfer Project Standards Protection", 0,
                            @"{""verifyProjectRegistration"":true,""autoResetProjectCookie"":true,""notifyOnRegistrationFix"":true,""logTransferEvents"":true}"),

                        // --- Family Management (FamilyLoadingIntoDocument = 3) ---
                        (3, "Ze_FamilyLibrarySettings", "Load Family from Non-Approved Location", 0,
                            @"{""approvedLibraryPaths"":[],""nonApprovedPaths"":[""C:\\""],""protectedCategories"":[],""checkVersionMismatch"":true}"),

                        // --- Import & Link Protection ---
                        (6, "Ze_CADImportProtection", "CAD Import Protection", 0,
                            @"{""blockAllImports"":false,""allowedExtensions"":[],""suggestLinkInstead"":true,""maxFileSizeMB"":0}"),
                        (6, "Ze_CADExplodeProtection", "CAD Explode Protection", 0,
                            @"{""blockFullExplode"":true,""blockPartialExplode"":true,""allowedCategories"":[]}"),
                        (5, "Ze_RVTLinkPinPrompt", "Revit Link Pin Prompt", 0,
                            @"{""promptToPinAfterInsert"":true,""autoPinWithoutPrompt"":false}"),
                    };

                    var defaultProtectionSettingsId = "00000000-0000-0000-0000-000000000001";

                    foreach (var (eventType, dummyCommandId, protectionName, isCompanyLevel, configJson) in eventProtections)
                    {
                        using (var cmd = new SQLiteCommand(@"
                            INSERT OR IGNORE INTO event_protection_settings
                            (id, protection_settings_id, event_type, dummy_command_id, protection_name,
                             is_enabled, intervention_mode, is_company_level, allow_admin_override,
                             configuration_json, updated_at)
                            VALUES (@id, @protectionSettingsId, @eventType, @dummyCommandId, @protectionName,
                                    0, 0, @isCompanyLevel, 0, @configJson, datetime('now'))", connection))
                        {
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                            cmd.Parameters.AddWithValue("@protectionSettingsId", defaultProtectionSettingsId);
                            cmd.Parameters.AddWithValue("@eventType", eventType);
                            cmd.Parameters.AddWithValue("@dummyCommandId", dummyCommandId);
                            cmd.Parameters.AddWithValue("@protectionName", protectionName);
                            cmd.Parameters.AddWithValue("@isCompanyLevel", isCompanyLevel);
                            cmd.Parameters.AddWithValue("@configJson", configJson);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // Backfill configuration_json for existing rows that have NULL
                    var configDefaults = new (string dummyCommandId, string configJson)[]
                    {
                        ("Ze_DuplicateUserSessionProtection", @"{""blockDuplicateUsername"":true,""checkApiForRemoteSessions"":true}"),
                        ("Ze_OpenCentralFileProtection", @"{""warnOnCentralAccess"":true,""blockDirectCentralOpen"":false}"),
                        ("Ze_ModelUpgradeProtection", @"{""protectProjectFiles"":true,""protectFamilyFiles"":true,""minimumVersionDifference"":1}"),
                        ("Ze_SaveOverEarlierFileVersionProtection", @"{""warnOnDowngrade"":true,""blockDowngrade"":false}"),
                        ("Ze_DocumentSaveAsProtection", @"{""blockAllSaveAs"":false}"),
                        ("Ze_DocumentPrintingProtection", @"{""blockAllPrinting"":false,""requireApprovalForPdf"":false}"),
                        ("Ze_DocumentExportingProtection", @"{""blockAllExports"":false,""blockedFormats"":[],""allowedFormats"":[]}"),
                        ("Ze_TransferProjectStandardsProtection", @"{""verifyProjectRegistration"":true,""autoResetProjectCookie"":true,""notifyOnRegistrationFix"":true,""logTransferEvents"":true}"),
                        ("Ze_FamilyLibrarySettings", @"{""approvedLibraryPaths"":[],""nonApprovedPaths"":[""C:\\""],""protectedCategories"":[],""checkVersionMismatch"":true}"),
                        ("Ze_CADImportProtection", @"{""blockAllImports"":false,""allowedExtensions"":[],""suggestLinkInstead"":true,""maxFileSizeMB"":0}"),
                        ("Ze_CADExplodeProtection", @"{""blockFullExplode"":true,""blockPartialExplode"":true,""allowedCategories"":[]}"),
                        ("Ze_RVTLinkPinPrompt", @"{""promptToPinAfterInsert"":true,""autoPinWithoutPrompt"":false}"),
                    };

                    foreach (var (dummyCommandId, configJson) in configDefaults)
                    {
                        using (var backfillCmd = new SQLiteCommand(@"
                            UPDATE event_protection_settings
                            SET configuration_json = @configJson, updated_at = datetime('now')
                            WHERE dummy_command_id = @dummyCommandId
                              AND configuration_json IS NULL", connection))
                        {
                            backfillCmd.Parameters.AddWithValue("@configJson", configJson);
                            backfillCmd.Parameters.AddWithValue("@dummyCommandId", dummyCommandId);
                            backfillCmd.ExecuteNonQuery();
                        }
                    }

                    _logger?.LogInfo($"Seeded {eventProtections.Length} default event protections with configuration (all disabled)");

                    // Clean up removed protections from existing databases
                    using (var cleanupCmd = new SQLiteCommand(@"
                        DELETE FROM event_protection_settings
                        WHERE dummy_command_id IN (
                            'Ze_OpenFileFromNonApprovedProtection',
                            'Ze_FamilyLoadingProtection',
                            'Ze_FamilyVersionMismatch',
                            'Ze_SyncConflictDetection',
                            'Ze_CopyMonitorPinProtection',
                            'Ze_CADImportPinPrompt'
                        )", connection))
                    {
                        var removed = cleanupCmd.ExecuteNonQuery();
                        if (removed > 0)
                            _logger?.LogInfo($"Removed {removed} deprecated event protection entries");
                    }

                    // Update renamed protections in existing databases
                    var renames = new (string dummyCommandId, string newName)[]
                    {
                        ("Ze_FamilyLibrarySettings", "Load Family from Non-Approved Location"),
                        ("Ze_DuplicateUserSessionProtection", "Duplicate Username in Same Model"),
                    };

                    foreach (var (dummyCommandId, newName) in renames)
                    {
                        using (var renameCmd = new SQLiteCommand(@"
                            UPDATE event_protection_settings
                            SET protection_name = @newName, updated_at = datetime('now')
                            WHERE dummy_command_id = @dummyCommandId
                              AND protection_name != @newName", connection))
                        {
                            renameCmd.Parameters.AddWithValue("@newName", newName);
                            renameCmd.Parameters.AddWithValue("@dummyCommandId", dummyCommandId);
                            renameCmd.ExecuteNonQuery();
                        }
                    }

                    // Migrate event_type values to new numbering scheme
                    // 1=DocumentOpening, 2=DocumentSaving, 3=FamilyLoading, 4=DocumentPrinting, 5=DocumentChanged, 6=CommandProtection
                    var eventTypeMigrations = new (string dummyCommandId, int newEventType)[]
                    {
                        ("Ze_DuplicateUserSessionProtection", 1),   // was 28 → DocumentOpening
                        ("Ze_DocumentPrintingProtection", 4),       // was 26 → DocumentPrinting
                        ("Ze_DocumentExportingProtection", 6),      // was 27 → CommandProtection
                        ("Ze_TransferProjectStandardsProtection", 6), // was 23 → CommandProtection
                        ("Ze_CADImportProtection", 6),              // was 20 → CommandProtection
                        ("Ze_CADExplodeProtection", 6),             // was 21 → CommandProtection
                        ("Ze_RVTLinkPinPrompt", 5),                 // was 25 → DocumentChanged
                    };

                    foreach (var (dummyCommandId, newEventType) in eventTypeMigrations)
                    {
                        using (var migrateCmd = new SQLiteCommand(@"
                            UPDATE event_protection_settings
                            SET event_type = @newEventType, updated_at = datetime('now')
                            WHERE dummy_command_id = @dummyCommandId
                              AND event_type != @newEventType", connection))
                        {
                            migrateCmd.Parameters.AddWithValue("@newEventType", newEventType);
                            migrateCmd.Parameters.AddWithValue("@dummyCommandId", dummyCommandId);
                            migrateCmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to seed event protections: {ex.Message}", ex);
            }
        }

        private void MigrateToV4()
        {
            using var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
            connection.Open();
            using var cmd = new SQLiteCommand(@"
                CREATE TABLE IF NOT EXISTS ze_identity (
                    id            INTEGER PRIMARY KEY CHECK (id = 1),
                    machine_id    TEXT    NOT NULL,
                    sid           TEXT    NULL,
                    username      TEXT    NOT NULL,
                    hostname      TEXT    NOT NULL,
                    ze_user_id    TEXT    NULL,
                    captured_at   TEXT    NULL,
                    registered_at TEXT    NULL
                );
                ALTER TABLE ze_identity ADD COLUMN IF NOT EXISTS captured_at TEXT NULL;
                INSERT OR IGNORE INTO schema_version (version) VALUES (4);", connection);
            cmd.ExecuteNonQuery();
        }

        #endregion
    }
}
