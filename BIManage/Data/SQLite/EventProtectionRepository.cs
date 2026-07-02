using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Protection;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for event protection settings in SQLite database
    /// Event-based protections
    /// </summary>
    public class EventProtectionRepository
    {
        private readonly string _databasePath;
        private readonly ILogger? _logger;
        private readonly DatabaseIntegrityService? _integrityService;

        public EventProtectionRepository(string databasePath, ILogger? logger, DatabaseIntegrityService? integrityService = null)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
            _logger = logger;
            _integrityService = integrityService;
            EnsureSchemaUpToDate();
        }

        /// <summary>
        /// Ensures the event_protection_settings table has all required columns
        /// </summary>
        private void EnsureSchemaUpToDate()
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                using var connection = OpenConnection();

                // Add columns for project, company, and model assignment
                AddColumnIfNotExists(connection, "event_protection_settings", "project_id", "TEXT");
                AddColumnIfNotExists(connection, "event_protection_settings", "company_id", "TEXT");
                AddColumnIfNotExists(connection, "event_protection_settings", "model_guid", "TEXT");
                AddColumnIfNotExists(connection, "event_protection_settings", "custom_message_image_path", "TEXT");
                AddColumnIfNotExists(connection, "event_protection_settings", "rule_ids", "TEXT");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update event_protection_settings schema: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Add a column to a table if it doesn't exist
        /// </summary>
        private void AddColumnIfNotExists(SQLiteConnection connection, string tableName, string columnName, string columnDef)
        {
            try
            {
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SQLiteCommand($"PRAGMA table_info({tableName})", connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString(1));
                    }
                }

                if (columns.Contains(columnName))
                    return;

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
        /// Open a new SQLite connection
        /// </summary>
        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Load all event protection settings for a project/model
        /// </summary>
        public List<EventProtectionSettings> LoadEventProtectionSettings(int? projectId, string? modelGuid = null)
        {
            var result = new List<EventProtectionSettings>();

            try
            {
                using var connection = OpenConnection();

                // Load settings matching the project, model, global (no project_id), or company-wide.
                //
                // The leading "@projectId IS NULL" clause is the enforcement-time fallback:
                // CommandInterceptionService and SetupProtectionBindingsExternalEventHandler
                // both call LoadSettingsFromDatabase(projectId: null) because they don't have
                // a Revit project-id mapped at command-execute time. Without this clause,
                // project-level OVERRIDE rows (is_company_level=0, project_id NOT NULL,
                // model_guid NULL — created by Project Admins toggling a company-level seed)
                // would be silently filtered out and enforcement would only see the company-
                // level seed (Enabled=false by default), so toggling Import CAD = Protect
                // appeared to save but never actually fired. The scope-precedence logic in
                // EventProtectionService.LoadSettingsFromDatabase (project > company per
                // DummyCommandId) handles dedup correctly when we load extra rows here.
                var sql = @"
                    SELECT id, event_type, dummy_command_id, protection_name,
                           is_enabled, intervention_mode, custom_message,
                           is_company_level, configuration_json,
                           capture_before_screenshot, capture_after_screenshot,
                           require_comment, allow_admin_override, send_email,
                           modified_by, updated_at,
                           project_id, company_id, model_guid,
                           custom_message_image_path, rule_ids,
                           created_by, row_hmac
                    FROM event_protection_settings
                    WHERE @projectId IS NULL
                          OR project_id IS @projectId OR project_id IS NULL
                          OR is_company_level = 1
                          OR (@modelGuid IS NOT NULL AND model_guid = @modelGuid)
                    ORDER BY event_type, protection_name";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@projectId", projectId.HasValue ? (object)projectId.Value.ToString() : DBNull.Value);
                cmd.Parameters.AddWithValue("@modelGuid", (object?)modelGuid ?? DBNull.Value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var setting = new EventProtectionSettings
                    {
                        Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        EventType = (RevitEventType)reader.GetInt32(1),
                        DummyCommandId = reader.GetString(2),
                        ProtectionName = reader.GetString(3),
                        Enabled = reader.GetInt32(4) == 1,
                        Mode = (InterventionMode)reader.GetInt32(5),
                        CustomMessage = reader.IsDBNull(6) ? null : reader.GetString(6),
                        IsCompanyLevel = reader.GetInt32(7) == 1,
                        ConfigurationJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                        CaptureBeforeScreenshot = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                        CaptureAfterScreenshot = !reader.IsDBNull(10) && reader.GetInt32(10) == 1,
                        RequireComment = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
                        AllowAdminOverride = reader.IsDBNull(12) || reader.GetInt32(12) == 1,
                        SendEmail = !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                        ModifiedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
                        ModifiedAt = reader.IsDBNull(15) ? null : (DateTime.TryParse(reader.GetString(15), out var dt) ? dt : (DateTime?)null),
                        ProjectId = reader.IsDBNull(16) ? null : reader.GetString(16),
                        CompanyId = reader.IsDBNull(17) ? null : reader.GetString(17),
                        ModelGuid = reader.IsDBNull(18) ? null : reader.GetString(18),
                        CustomMessageImagePath = reader.IsDBNull(19) ? null : reader.GetString(19),
                        RuleIds = ParseRuleIds(reader.IsDBNull(20) ? null : reader.GetString(20)),
                        CreatedBy = reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetString(21) : null
                    };

                    // Verify HMAC — log mismatches but don't exclude (race conditions with SignalR updates)
                    if (_integrityService?.IsIntegrityAvailable == true)
                    {
                        var storedHmac = reader.FieldCount > 22 ? (reader.IsDBNull(22) ? null : reader.GetString(22)) : null;
                        if (!VerifyEventProtectionHmac(setting, storedHmac))
                            _logger?.LogDebug($"HMAC mismatch for event_protection_settings.{setting.Id} — may be mid-update, including anyway");
                    }

                    result.Add(setting);
                }

                _logger?.LogInfo($"Loaded {result.Count} event protection settings from database");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load event protection settings: {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// Load protections for a specific event type
        /// </summary>
        public List<EventProtectionSettings> LoadProtectionsForEvent(RevitEventType eventType, int? projectId)
        {
            var result = new List<EventProtectionSettings>();

            try
            {
                using var connection = OpenConnection();

                var sql = @"
                    SELECT id, event_type, dummy_command_id, protection_name,
                           is_enabled, intervention_mode, custom_message,
                           is_company_level, configuration_json,
                           capture_before_screenshot, capture_after_screenshot,
                           require_comment, allow_admin_override, send_email,
                           modified_by, updated_at,
                           project_id, company_id, model_guid,
                           custom_message_image_path, rule_ids,
                           created_by, row_hmac
                    FROM event_protection_settings
                    WHERE event_type = :eventType
                    ORDER BY protection_name";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":eventType", (int)eventType);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var setting = MapReaderToSettings(reader);
                    var storedHmac = reader.FieldCount > 22 ? (reader.IsDBNull(22) ? null : reader.GetString(22)) : null;

                    if (_integrityService?.IsIntegrityAvailable == true && !VerifyEventProtectionHmac(setting, storedHmac))
                        _logger?.LogDebug($"HMAC mismatch for event_protection_settings.{setting.Id} — may be mid-update, including anyway");

                    result.Add(setting);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load protections for event {eventType}: {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// Get specific protection by dummy command ID
        /// </summary>
        public EventProtectionSettings? GetProtectionByDummyCommandId(string dummyCommandId, int? projectId)
        {
            try
            {
                using var connection = OpenConnection();

                var sql = @"
                    SELECT id, event_type, dummy_command_id, protection_name,
                           is_enabled, intervention_mode, custom_message,
                           is_company_level, configuration_json,
                           capture_before_screenshot, capture_after_screenshot,
                           require_comment, allow_admin_override, send_email,
                           modified_by, updated_at,
                           project_id, company_id, model_guid,
                           custom_message_image_path, rule_ids,
                           created_by, row_hmac
                    FROM event_protection_settings
                    WHERE dummy_command_id = :dummyCommandId
                    LIMIT 1";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":dummyCommandId", dummyCommandId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var setting = MapReaderToSettings(reader);
                    var storedHmac = reader.FieldCount > 22 ? (reader.IsDBNull(22) ? null : reader.GetString(22)) : null;

                    if (_integrityService?.IsIntegrityAvailable == true && !VerifyEventProtectionHmac(setting, storedHmac))
                    {
                        _logger?.LogWarning($"HMAC verification FAILED for event_protection_settings.{setting.Id} — row tampered");
                        return null;
                    }

                    return setting;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get protection {dummyCommandId}: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Save or update event protection setting
        /// </summary>
        public async Task<string> SaveEventProtectionAsync(EventProtectionSettings setting, int? projectId, DateTime? explicitUpdatedAt = null)
        {
            try
            {
                using var connection = OpenConnection();

                if (!string.IsNullOrEmpty(setting.Id))
                {
                    // Update existing.
                    //
                    // Resilience to id drift between server and local DB: the dialog can hold
                    // settings with the SERVER's id (e.g. from FetchByModelGuidFromApiAsync)
                    // while the local DB still has rows with locally-minted GUIDs from a prior
                    // INSERT (e.g. when the API returned no id at first sync). UPDATE WHERE id=X
                    // would then match 0 rows and silently no-op — leaving the user's toggle
                    // un-persisted, the row stuck at is_enabled=0, and enforcement disabled.
                    // Verified in field 2026-05-04: CAD Import / CAD Explode protections never
                    // enforced because every save UPDATE matched 0 rows but logged success.
                    //
                    // Strategy: try UPDATE WHERE id=X first. If 0 rows changed, fall back to the
                    // natural key (dummy_command_id + scope) to find the actual local row, then
                    // UPDATE THAT row in place — keeping its existing local id stable so any
                    // foreign references stay valid.
                    var updateSql = BuildUpdateByIdSql();

                    string idToUse = setting.Id;
                    int rowsAffected;
                    using (var cmd = new SQLiteCommand(updateSql, connection))
                    {
                        cmd.Parameters.AddWithValue(":id", idToUse);
                        AddSettingParameters(cmd, setting, explicitUpdatedAt);
                        rowsAffected = await cmd.ExecuteNonQueryAsync();
                    }

                    // Natural-key fallback: an existing local row may exist under a *different*
                    // id (e.g. created with a locally-minted GUID before the API returned its id).
                    // Try to find that row by (dummy_command_id + scope), REKEY it to the
                    // server's id, then UPDATE the row in place.
                    //
                    // Why rekey instead of keeping the local id: the dialog reads model.Id
                    // from local SQLite and ships it to the server in subsequent PUT/PATCH
                    // calls. If local keeps a stale locally-minted id (e.g. 479a3eb2) but
                    // the server's actual id is different (e.g. eb4b9b8a — verified in field
                    // 2026-05-16 from production logs), every PUT/PATCH returns 404
                    // "Event protection with Id X not found". The user's edit saves locally,
                    // the toast says "saved", but the server never persists it; on the next
                    // refresh the server's unchanged row overwrites the local edit and the
                    // change appears lost. Rekeying makes local-id == server-id so the next
                    // API call lands on the right row.
                    if (rowsAffected == 0)
                    {
                        var localId = FindLocalIdByNaturalKey(connection, setting);
                        if (!string.IsNullOrEmpty(localId))
                        {
                            _logger?.LogInfo($"UPDATE by id matched 0 rows for '{setting.DummyCommandId}' (id={setting.Id}). " +
                                $"Found local row with same natural key (id={localId}) — re-running UPDATE.");

                            if (!string.IsNullOrEmpty(setting.Id)
                                && !string.Equals(localId, setting.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                // Rekey the local row to the server's id so subsequent API
                                // calls (PUT / PATCH / DELETE) use the id the server actually
                                // recognises. Without this, every server-bound write 404s.
                                try
                                {
                                    using var rekey = new SQLiteCommand(
                                        "UPDATE event_protection_settings SET id = :newId WHERE id = :oldId",
                                        connection);
                                    rekey.Parameters.AddWithValue(":newId", setting.Id);
                                    rekey.Parameters.AddWithValue(":oldId", localId!);
                                    var rekeyed = await rekey.ExecuteNonQueryAsync();
                                    if (rekeyed > 0)
                                    {
                                        _logger?.LogInfo($"Rekeyed local row '{setting.DummyCommandId}' from {localId} to server-id {setting.Id}.");
                                        idToUse = setting.Id!;
                                    }
                                    else
                                    {
                                        idToUse = localId!;
                                    }
                                }
                                catch (SQLiteException rekeyEx)
                                {
                                    // Constraint conflict (e.g., a row already exists with
                                    // the server's id from a partially-completed prior sync).
                                    // Fall back to updating under the local id — at least the
                                    // edit lands locally for enforcement. The id mismatch will
                                    // still cause server-bound writes to 404, but that's a
                                    // separate path the offline queue can retry.
                                    _logger?.LogWarning($"Rekey from {localId} to {setting.Id} for '{setting.DummyCommandId}' failed: {rekeyEx.Message}. Continuing with local id.");
                                    idToUse = localId!;
                                }
                            }
                            else
                            {
                                idToUse = localId!;
                            }

                            using var cmd2 = new SQLiteCommand(updateSql, connection);
                            cmd2.Parameters.AddWithValue(":id", idToUse);
                            AddSettingParameters(cmd2, setting, explicitUpdatedAt);
                            rowsAffected = await cmd2.ExecuteNonQueryAsync();
                        }
                    }

                    if (rowsAffected == 0)
                    {
                        // Neither id nor natural key matched — first time this protection lands
                        // locally. Fall through to the INSERT block below WITHOUT clearing
                        // setting.Id, so the new local row preserves the API-issued id and
                        // subsequent UPDATE-by-id calls match cleanly. Without this fall-through
                        // the API row's is_enabled / intervention_mode / scope never lands
                        // locally; enforcement reloads see only the seeded defaults and the
                        // dialog toggle appears to save server-side but never fires in-Revit.
                        _logger?.LogInfo($"No local row matched id={setting.Id} or natural key for '{setting.DummyCommandId}' — inserting from API.");
                        // Intentional fall-through; INSERT branch below uses setting.Id (preserved).
                    }
                    else
                    {
                        UpdateEventProtectionHmac(connection, idToUse, setting);
                        _logger?.LogDebug($"Updated event protection: {setting.DummyCommandId} (id={idToUse})");
                        return idToUse;
                    }
                }

                {
                    // Insert new with GUID. Schema's unique index is (protection_settings_id,
                    // event_type, dummy_command_id), so a project-level override needs a
                    // DIFFERENT protection_settings_id from the seeded company-level row
                    // (which uses the default sentinel). Picking the namespace:
                    //   - Company-level row → seeded default sentinel
                    //   - Project-level override with known projectId → the project's GUID
                    //   - Project-level override without a projectId (e.g. unsaved file
                    //     where we couldn't resolve the project from the server) →
                    //     a fresh GUID so the row is unique. Subsequent toggles on the
                    //     same protection find this row via FindExistingProjectOverride
                    //     (matched by DummyCommandId) and UPDATE it in place — no new
                    //     rows are minted, so the DB doesn't grow unbounded.
                    // Preserve the API-issued id when we got here via UPDATE-fall-through (so
                    // the next API refresh can match this row by id). Generate a fresh GUID only
                    // when the caller really had no id to begin with.
                    var newId = !string.IsNullOrEmpty(setting.Id)
                        ? setting.Id!
                        : Guid.NewGuid().ToString();
                    const string defaultProtectionSettingsId = "00000000-0000-0000-0000-000000000001";
                    string protectionSettingsId;
                    if (setting.IsCompanyLevel)
                    {
                        protectionSettingsId = defaultProtectionSettingsId;
                    }
                    else if (!string.IsNullOrWhiteSpace(setting.ProjectId))
                    {
                        protectionSettingsId = setting.ProjectId!;
                    }
                    else
                    {
                        // Project-level override without a known projectId — use a fresh
                        // GUID so the unique index passes. Without this fallback, the
                        // INSERT collided with the seeded company-level row when the
                        // current model wasn't registered on the server (Project1 etc.).
                        protectionSettingsId = Guid.NewGuid().ToString();
                    }
                    // INSERT OR REPLACE handles the case where the unique index
                    // (protection_settings_id, event_type, dummy_command_id) already has a row
                    // with a DIFFERENT id (e.g., the seeded company-level row from schema migration).
                    // Plain INSERT collides on that unique tuple; REPLACE deletes the colliding
                    // row and writes ours, which has the API-issued id. After this, subsequent
                    // refreshes match by id and the UPDATE branch works without falling through.
                    // Without this, every API row whose id differs from the seeded id (which is
                    // every API-returned row) fails to land — cache stays empty, enforcement
                    // sees 0 enabled, web changes never reflect in Revit.
                    var insertSql = @"
                        INSERT OR REPLACE INTO event_protection_settings (
                            id, protection_settings_id, event_type, dummy_command_id, protection_name,
                            is_enabled, intervention_mode, custom_message, is_company_level,
                            configuration_json, capture_before_screenshot, capture_after_screenshot,
                            require_comment, allow_admin_override, send_email,
                            custom_message_image_path,
                            project_id, company_id, model_guid, rule_ids,
                            modified_by, updated_at, created_by
                        ) VALUES (
                            :id, :protectionSettingsId, :eventType, :dummyCommandId, :protectionName,
                            :enabled, :mode, :customMessage, :isCompanyLevel,
                            :configJson, :capBefore, :capAfter,
                            :reqComment, :allowOverride, :sendEmail,
                            :customMessageImagePath,
                            :projectId, :companyId, :modelGuid, :ruleIds,
                            :modifiedBy, :updatedAt, :createdBy
                        )";

                    using var cmd = new SQLiteCommand(insertSql, connection);
                    cmd.Parameters.AddWithValue(":id", newId);
                    cmd.Parameters.AddWithValue(":protectionSettingsId", protectionSettingsId);
                    cmd.Parameters.AddWithValue(":eventType", (int)setting.EventType);
                    cmd.Parameters.AddWithValue(":dummyCommandId", setting.DummyCommandId);
                    cmd.Parameters.AddWithValue(":protectionName", setting.ProtectionName);
                    AddSettingParameters(cmd, setting, explicitUpdatedAt);

                    try
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SQLiteException sqlEx) when (sqlEx.ResultCode == SQLiteErrorCode.Constraint)
                    {
                        // INSERT collided with the unique key (protection_settings_id,
                        // event_type, dummy_command_id). This means an existing row already
                        // covers the same logical slot — typically a legacy seed row that
                        // FindLocalIdByNaturalKey didn't match (e.g. is_company_level=0 on
                        // the legacy row but the API now says LevelScope=1/Company).
                        // Look it up by the exact unique-key tuple and UPDATE in place so
                        // the user's edit lands on the existing row instead of failing.
                        var existingId = FindLocalIdByUniqueKey(connection, protectionSettingsId, (int)setting.EventType, setting.DummyCommandId);
                        if (string.IsNullOrEmpty(existingId))
                        {
                            _logger?.LogError($"INSERT for '{setting.DummyCommandId}' failed with unique-key collision but no row found by unique key — re-throwing.", sqlEx);
                            throw;
                        }

                        _logger?.LogInfo($"INSERT for '{setting.DummyCommandId}' collided with unique key — UPDATING existing row id={existingId} instead.");
                        using (var update = new SQLiteCommand(BuildUpdateByIdSql(), connection))
                        {
                            update.Parameters.AddWithValue(":id", existingId);
                            AddSettingParameters(update, setting, explicitUpdatedAt);
                            await update.ExecuteNonQueryAsync();
                        }
                        UpdateEventProtectionHmac(connection, existingId!, setting);
                        _logger?.LogInfo($"Updated event protection (post-collision recovery): {setting.DummyCommandId} (id={existingId})");
                        return existingId!;
                    }
                    UpdateEventProtectionHmac(connection, newId, setting);

                    _logger?.LogInfo($"Inserted event protection: {setting.DummyCommandId} (id: {newId})");
                    return newId;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save event protection: {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Builds the parameterised UPDATE SQL used by SaveEventProtectionAsync. Extracted so
        /// the by-id path and the by-natural-key fallback can run the exact same statement
        /// (with the same column set + COALESCE for modified_by) without drifting.
        /// </summary>
        private static string BuildUpdateByIdSql() => @"
            UPDATE event_protection_settings SET
                is_enabled = :enabled,
                intervention_mode = :mode,
                custom_message = :customMessage,
                is_company_level = :isCompanyLevel,
                configuration_json = :configJson,
                capture_before_screenshot = :capBefore,
                capture_after_screenshot = :capAfter,
                require_comment = :reqComment,
                allow_admin_override = :allowOverride,
                send_email = :sendEmail,
                custom_message_image_path = :customMessageImagePath,
                project_id = :projectId,
                company_id = :companyId,
                model_guid = :modelGuid,
                rule_ids = :ruleIds,
                modified_by = COALESCE(:modifiedBy, modified_by),
                updated_at = :updatedAt
            WHERE id = :id";

        /// <summary>
        /// Finds the local DB row's id by the natural key (dummy_command_id + scope) for the
        /// given setting. Used as a fallback when the dialog passes a server-side id that
        /// doesn't match any local row — we still want to update the user's protection in
        /// place rather than silently no-op.
        ///
        /// Scope matching (broadened 2026-05-05 to cover legacy rows from earlier versions
        /// that never had is_company_level/project_id/model_guid populated correctly):
        ///   - Model-level (ModelGuid set):       match dummy_command_id + model_guid
        ///   - Project-level (ProjectId set):     match dummy_command_id + project_id
        ///   - Company-level (IsCompanyLevel):    match dummy_command_id where row has co_lvl=1
        ///                                        OR is scopeless (no project/model, co_lvl=0).
        ///                                        The latter covers legacy seed rows whose
        ///                                        is_company_level was never set; without this
        ///                                        we'd INSERT a new company row using the
        ///                                        sentinel protection_settings_id and collide
        ///                                        on the (psi, event_type, dummy_command_id)
        ///                                        unique key with the legacy scopeless row.
        ///   - Otherwise:                         match dummy_command_id alone (legacy rows
        ///                                        without scope columns set)
        /// Returns the local id, or null/empty if no row matches.
        /// </summary>
        private string? FindLocalIdByNaturalKey(SQLiteConnection connection, EventProtectionSettings setting)
        {
            if (string.IsNullOrWhiteSpace(setting.DummyCommandId))
                return null;

            try
            {
                string sql;
                if (!string.IsNullOrWhiteSpace(setting.ModelGuid))
                {
                    sql = "SELECT id FROM event_protection_settings WHERE dummy_command_id = :d AND model_guid = :m LIMIT 1";
                }
                else if (!string.IsNullOrWhiteSpace(setting.ProjectId))
                {
                    sql = "SELECT id FROM event_protection_settings WHERE dummy_command_id = :d AND project_id = :p AND model_guid IS NULL LIMIT 1";
                }
                else if (setting.IsCompanyLevel)
                {
                    // Match a real company-level row first; fall back to a scopeless legacy
                    // row (co_lvl=0, no project/model). Without the OR-clause we'd miss
                    // legacy seeds and the subsequent INSERT would hit the unique-key
                    // collision on (sentinel-PSI, event_type, dummy_command_id).
                    sql = @"SELECT id FROM event_protection_settings
                              WHERE dummy_command_id = :d
                                AND (
                                      is_company_level = 1
                                   OR (is_company_level = 0 AND project_id IS NULL AND model_guid IS NULL)
                                )
                              ORDER BY is_company_level DESC, updated_at DESC
                              LIMIT 1";
                }
                else
                {
                    // No explicit scope — match dummy_command_id with no project/model scope.
                    // Most common path on machines whose initial sync seeded scope-less rows.
                    sql = "SELECT id FROM event_protection_settings WHERE dummy_command_id = :d AND project_id IS NULL AND model_guid IS NULL AND is_company_level = 0 LIMIT 1";
                }

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":d", setting.DummyCommandId);
                if (!string.IsNullOrWhiteSpace(setting.ModelGuid))
                    cmd.Parameters.AddWithValue(":m", setting.ModelGuid);
                if (!string.IsNullOrWhiteSpace(setting.ProjectId))
                    cmd.Parameters.AddWithValue(":p", setting.ProjectId);

                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return result.ToString();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"FindLocalIdByNaturalKey({setting.DummyCommandId}) failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Defensive fallback when an INSERT collides on the unique key
        /// (protection_settings_id, event_type, dummy_command_id). Looks up the colliding
        /// row's id by that exact tuple so the caller can re-route to UPDATE instead of
        /// surfacing a "constraint failed" error to the user. Covers the case where
        /// FindLocalIdByNaturalKey missed (e.g. an unknown legacy scope combination) but
        /// a row with the same unique-key tuple does exist.
        /// </summary>
        private string? FindLocalIdByUniqueKey(SQLiteConnection connection, string protectionSettingsId, int eventType, string dummyCommandId)
        {
            if (string.IsNullOrWhiteSpace(protectionSettingsId) || string.IsNullOrWhiteSpace(dummyCommandId))
                return null;
            try
            {
                using var cmd = new SQLiteCommand(@"
                    SELECT id FROM event_protection_settings
                    WHERE protection_settings_id = :psi
                      AND event_type = :et
                      AND dummy_command_id = :d
                    LIMIT 1", connection);
                cmd.Parameters.AddWithValue(":psi", protectionSettingsId);
                cmd.Parameters.AddWithValue(":et", eventType);
                cmd.Parameters.AddWithValue(":d", dummyCommandId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    return result.ToString();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"FindLocalIdByUniqueKey({dummyCommandId}) failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Update protection configuration JSON
        /// </summary>
        public async Task<bool> UpdateConfigurationAsync(string settingId, string configurationJson)
        {
            try
            {
                using var connection = OpenConnection();

                var sql = @"
                    UPDATE event_protection_settings
                    SET configuration_json = :configJson, updated_at = datetime('now')
                    WHERE id = :id";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":id", settingId);
                cmd.Parameters.AddWithValue(":configJson", configurationJson);

                var affected = await cmd.ExecuteNonQueryAsync();

                // Recompute HMAC since configuration_json changed — need full row for recomputation
                if (affected > 0 && _integrityService?.IsIntegrityAvailable == true)
                {
                    var reloadSql = @"SELECT * FROM event_protection_settings WHERE id = :reloadId";
                    using var reloadCmd = new SQLiteCommand(reloadSql, connection);
                    reloadCmd.Parameters.AddWithValue(":reloadId", settingId);
                    using var reloadReader = reloadCmd.ExecuteReader();
                    if (reloadReader.Read())
                    {
                        var setting = MapReaderToSettings(reloadReader);
                        UpdateEventProtectionHmac(connection, settingId, setting);
                    }
                }

                return affected > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update configuration for setting {settingId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Returns full local rows keyed by id for every event protection that matches
        /// the given model_guid (or null model_guid when modelGuid arg is null). Used by
        /// sync services to swap a stale server payload for the fresher local copy when
        /// the local row's updated_at is newer than the server's modifiedAt.
        /// </summary>
        public Dictionary<string, EventProtectionSettings> GetEventProtectionRowsByModel(string? modelGuid)
        {
            var map = new Dictionary<string, EventProtectionSettings>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var connection = OpenConnection();
                // When a modelGuid is provided, return BOTH model-specific rows AND rows with
                // model_guid IS NULL (project-scope and company-scope overrides apply to all
                // models in the project / company). Without including the NULL rows, the
                // sync-service recency guard couldn't see project-admin overrides — server's
                // stale response would silently overwrite the user's just-saved edit on the
                // next dialog reopen.
                var sql = @"
                    SELECT id, event_type, dummy_command_id, protection_name,
                           is_enabled, intervention_mode, custom_message,
                           is_company_level, configuration_json,
                           capture_before_screenshot, capture_after_screenshot,
                           require_comment, allow_admin_override, send_email,
                           modified_by, updated_at,
                           project_id, company_id, model_guid,
                           custom_message_image_path, rule_ids,
                           created_by, row_hmac
                    FROM event_protection_settings
                    WHERE " + (modelGuid != null ? "(model_guid = :modelGuid OR model_guid IS NULL)" : "model_guid IS NULL");
                using var cmd = new SQLiteCommand(sql, connection);
                if (modelGuid != null)
                    cmd.Parameters.AddWithValue(":modelGuid", modelGuid);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var setting = MapReaderToSettings(reader);
                    if (!string.IsNullOrEmpty(setting.Id))
                        map[setting.Id] = setting;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get event protection rows by model: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Returns local modified_at timestamps keyed by id for every event protection
        /// row whose model_guid matches (or is null when modelGuid arg is null).
        /// Used by sync services to detect "local edit is newer than server" so a fetch
        /// won't overwrite a recent local change with stale server data.
        /// </summary>
        public async Task<Dictionary<string, DateTime>> GetEventProtectionModifiedAtByModelAsync(string? modelGuid)
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var connection = OpenConnection();
                var sql = modelGuid != null
                    ? "SELECT id, updated_at FROM event_protection_settings WHERE model_guid = :modelGuid"
                    : "SELECT id, updated_at FROM event_protection_settings WHERE model_guid IS NULL";
                using var cmd = new SQLiteCommand(sql, connection);
                if (modelGuid != null)
                    cmd.Parameters.AddWithValue(":modelGuid", modelGuid);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (reader.IsDBNull(1)) continue;
                    if (DateTime.TryParse(reader.GetString(1), out var ts))
                        map[id] = ts;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get event protection modified-at map: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Gets all event protection IDs for a specific model (for reconciliation).
        /// </summary>
        public async Task<List<(string Id, string DummyCommandId)>> GetEventProtectionIdsByModelGuidAsync(string? modelGuid)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = OpenConnection();
                var sql = modelGuid != null
                    ? "SELECT id, dummy_command_id FROM event_protection_settings WHERE model_guid = :modelGuid"
                    : "SELECT id, dummy_command_id FROM event_protection_settings WHERE model_guid IS NULL";
                using var cmd = new SQLiteCommand(sql, connection);
                if (modelGuid != null)
                    cmd.Parameters.AddWithValue(":modelGuid", modelGuid);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var dummyId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, dummyId ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get event protections by model: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Returns every locally-cached <c>event_protection_settings</c> row that the
        /// server's <c>/event-protections/by-model/{modelGuid}</c> response is expected
        /// to include — model-level for the current model, project-level for the project
        /// the model belongs to, plus company-level rows for the current company. Used
        /// by <c>FetchByModelGuidFromApiAsync</c>'s reconciliation block to find orphans
        /// precisely without sweeping events that belong to OTHER projects of the same
        /// company (which the old "GetEventProtectionIdsByModelGuidAsync(null)" call
        /// would do).
        /// </summary>
        public async Task<List<(string Id, string DummyCommandId)>> GetEventProtectionIdsForFetchScopeAsync(string currentModelGuid, string? companyId)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = OpenConnection();
                const string sql = @"
                    SELECT id, dummy_command_id FROM event_protection_settings
                    WHERE (:companyId IS NULL OR company_id IS NULL OR company_id = :companyId COLLATE NOCASE)
                      AND (
                          model_guid = :currentModelGuid
                          OR (model_guid IS NULL AND project_id IS NULL)
                          OR (model_guid IS NULL AND project_id IS NOT NULL AND project_id = (
                              SELECT zemanage_project_id FROM registered_models WHERE model_guid = :currentModelGuid LIMIT 1
                          ))
                      )";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":currentModelGuid", currentModelGuid ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue(":companyId", string.IsNullOrWhiteSpace(companyId) ? (object)DBNull.Value : companyId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var dummyId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, dummyId ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get event protections for fetch scope: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Delete event protection setting
        /// </summary>
        public async Task<bool> DeleteEventProtectionAsync(string settingId)
        {
            try
            {
                using var connection = OpenConnection();

                var sql = "DELETE FROM event_protection_settings WHERE id = :id";
                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":id", settingId);

                var affected = await cmd.ExecuteNonQueryAsync();
                return affected > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete event protection {settingId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get configuration object from JSON
        /// </summary>
        public T? GetConfiguration<T>(EventProtectionSettings setting) where T : EventProtectionConfig
        {
            if (string.IsNullOrEmpty(setting.ConfigurationJson))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(setting.ConfigurationJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to deserialize config for {setting.DummyCommandId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set configuration object as JSON
        /// </summary>
        public void SetConfiguration<T>(EventProtectionSettings setting, T config) where T : EventProtectionConfig
        {
            setting.ConfigurationJson = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        #region Private Helper Methods

        private EventProtectionSettings MapReaderToSettings(SQLiteDataReader reader)
        {
            return new EventProtectionSettings
            {
                Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                EventType = (RevitEventType)reader.GetInt32(1),
                DummyCommandId = reader.GetString(2),
                ProtectionName = reader.GetString(3),
                Enabled = reader.GetInt32(4) == 1,
                Mode = (InterventionMode)reader.GetInt32(5),
                CustomMessage = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsCompanyLevel = reader.GetInt32(7) == 1,
                ConfigurationJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                CaptureBeforeScreenshot = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                CaptureAfterScreenshot = !reader.IsDBNull(10) && reader.GetInt32(10) == 1,
                RequireComment = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
                AllowAdminOverride = reader.IsDBNull(12) || reader.GetInt32(12) == 1,
                SendEmail = !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                ModifiedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
                ModifiedAt = reader.IsDBNull(15) ? null : (DateTime.TryParse(reader.GetString(15), out var dt) ? dt : (DateTime?)null),
                ProjectId = reader.IsDBNull(16) ? null : reader.GetString(16),
                CompanyId = reader.IsDBNull(17) ? null : reader.GetString(17),
                ModelGuid = reader.IsDBNull(18) ? null : reader.GetString(18),
                CustomMessageImagePath = reader.IsDBNull(19) ? null : reader.GetString(19),
                RuleIds = reader.FieldCount > 20 ? ParseRuleIds(reader.IsDBNull(20) ? null : reader.GetString(20)) : new List<string>(),
                CreatedBy = reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetString(21) : null
            };
        }

        private void AddSettingParameters(SQLiteCommand cmd, EventProtectionSettings setting, DateTime? explicitUpdatedAt = null)
        {
            cmd.Parameters.AddWithValue(":enabled", setting.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue(":mode", (int)setting.Mode);
            cmd.Parameters.AddWithValue(":customMessage", (object?)setting.CustomMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":isCompanyLevel", setting.IsCompanyLevel ? 1 : 0);
            cmd.Parameters.AddWithValue(":configJson", (object?)setting.ConfigurationJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":capBefore", setting.CaptureBeforeScreenshot ? 1 : 0);
            cmd.Parameters.AddWithValue(":capAfter", setting.CaptureAfterScreenshot ? 1 : 0);
            cmd.Parameters.AddWithValue(":reqComment", setting.RequireComment ? 1 : 0);
            cmd.Parameters.AddWithValue(":allowOverride", setting.AllowAdminOverride ? 1 : 0);
            cmd.Parameters.AddWithValue(":sendEmail", setting.SendEmail ? 1 : 0);
            cmd.Parameters.AddWithValue(":customMessageImagePath", (object?)setting.CustomMessageImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":projectId", (object?)setting.ProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":companyId", (object?)setting.CompanyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":modelGuid", (object?)setting.ModelGuid ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":modifiedBy", (object?)setting.ModifiedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":ruleIds", (object?)SerializeRuleIds(setting.RuleIds) ?? DBNull.Value);
            cmd.Parameters.AddWithValue(":createdBy", (object?)setting.CreatedBy ?? DBNull.Value);
            // updated_at: when called from a server fetch, pass the server's modifiedAt so the
            // local row's timestamp matches the server's. Without this, every fetch bumps
            // updated_at to NOW, making subsequent fetches incorrectly preserve the local copy
            // (the "preserve local edit" branch in EventProtectionSyncService.FetchByModelGuidFromApiAsync).
            // Format must match SQLite's datetime('now') (ISO without 'T', no timezone): "yyyy-MM-dd HH:mm:ss"
            // so that lexical ordering on the column stays consistent across rows.
            var ts = (explicitUpdatedAt?.ToUniversalTime() ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss");
            cmd.Parameters.AddWithValue(":updatedAt", ts);
        }

        private static List<string> ParseRuleIds(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<string>();

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string? SerializeRuleIds(List<string>? ruleIds)
        {
            if (ruleIds == null || ruleIds.Count == 0)
                return null;

            return JsonSerializer.Serialize(ruleIds);
        }

        private void UpdateEventProtectionHmac(SQLiteConnection connection, string id, EventProtectionSettings setting)
        {
            if (_integrityService == null || !_integrityService.IsIntegrityAvailable) return;

            try
            {
                var hmac = ComputeEventProtectionHmac(id, setting);
                if (!string.IsNullOrEmpty(hmac))
                {
                    using var hmacCmd = new SQLiteCommand(
                        "UPDATE event_protection_settings SET row_hmac = :hmac WHERE id = :id", connection);
                    hmacCmd.Parameters.AddWithValue(":hmac", hmac);
                    hmacCmd.Parameters.AddWithValue(":id", id);
                    hmacCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update HMAC for event_protection_settings.{id}: {ex.Message}");
            }
        }

        private string ComputeEventProtectionHmac(string id, EventProtectionSettings setting)
        {
            if (_integrityService == null) return string.Empty;

            return _integrityService.ComputeHmac("event_protection_settings", id,
                ((int)setting.EventType).ToString(),
                setting.Enabled ? "1" : "0",
                ((int)setting.Mode).ToString(),
                setting.CustomMessage,
                setting.IsCompanyLevel ? "1" : "0",
                setting.ConfigurationJson,
                SerializeRuleIds(setting.RuleIds)
            );
        }

        private bool VerifyEventProtectionHmac(EventProtectionSettings setting, string? storedHmac)
        {
            if (_integrityService == null) return true;

            return _integrityService.VerifyHmac("event_protection_settings", setting.Id,
                storedHmac,
                ((int)setting.EventType).ToString(),
                setting.Enabled ? "1" : "0",
                ((int)setting.Mode).ToString(),
                setting.CustomMessage,
                setting.IsCompanyLevel ? "1" : "0",
                setting.ConfigurationJson,
                SerializeRuleIds(setting.RuleIds)
            );
        }

        #endregion
    }
}
