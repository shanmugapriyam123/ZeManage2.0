using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;
using BIManage.Revit.Protection;
using BIManageRevit.BIManage.ViewModels.Protection;

namespace BIManage.Data.SQLite
{
    /// <summary>
    ///     Repository for command protection settings in SQLite database
    /// </summary>
    public class CommandProtectionRepository
    {
        private readonly string _databasePath;
        private readonly ILogger? _logger;
        private readonly DatabaseIntegrityService? _integrityService;

        public CommandProtectionRepository(string databasePath, ILogger? logger, DatabaseIntegrityService? integrityService = null)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
            _logger = logger;
            _integrityService = integrityService;
            EnsureSchemaUpToDate();
        }

        /// <summary>
        /// Ensures the command_settings table has all required columns
        /// </summary>
        private void EnsureSchemaUpToDate()
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                using var connection = OpenConnection();

                // Add new columns for email notification and image attachment
                AddColumnIfNotExists(connection, "command_settings", "send_email", "INTEGER DEFAULT 0");
                AddColumnIfNotExists(connection, "command_settings", "custom_message_image_path", "TEXT");

                // Add columns for project, company, and model assignment
                AddColumnIfNotExists(connection, "command_settings", "project_id", "TEXT");
                AddColumnIfNotExists(connection, "command_settings", "company_id", "TEXT");
                AddColumnIfNotExists(connection, "command_settings", "model_guid", "TEXT");

                // Command settings are managed via the API - no local seeding
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update command_settings schema: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Add a column to a table if it doesn't exist
        /// </summary>
        private void AddColumnIfNotExists(SQLiteConnection connection, string tableName, string columnName, string columnDef)
        {
            try
            {
                var columns = GetTableColumns(connection, tableName);
                if (columns.Contains(columnName))
                    return;

                var alterSql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef}";
                using var cmd = new SQLiteCommand(alterSql, connection);
                cmd.ExecuteNonQuery();
                _logger?.LogInfo($"Added column {columnName} to {tableName}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to add column {columnName} to {tableName}: {ex.Message}");
            }
        }


        /// <summary>
        /// Ensure default protection_settings row exists for a project.
        /// Returns the GUID of the protection_settings row.
        /// </summary>
        public string? EnsureProtectionSettingsExist(int? projectId)
        {
            try
            {
                using var connection = OpenConnection();

                // Check if protection_settings exists for this project
                var checkSql = @"
                    SELECT guid FROM protection_settings
                    WHERE (project_id IS :projectId OR (project_id IS NULL AND :projectId IS NULL))
                      AND guid IS NOT NULL AND guid != ''
                    ORDER BY id DESC LIMIT 1";

                using var checkCmd = new SQLiteCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue(":projectId", (object?)projectId ?? DBNull.Value);

                var existingGuid = checkCmd.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(existingGuid))
                {
                    return existingGuid;
                }

                // Create new protection_settings with GUID
                var newGuid = Guid.NewGuid().ToString();
                var insertSql = @"
                    INSERT INTO protection_settings (guid, project_id, name, is_enabled, created_at, updated_at)
                    VALUES (:guid, :projectId, 'Default', 1, :now, :now)";

                using var insertCmd = new SQLiteCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue(":guid", newGuid);
                insertCmd.Parameters.AddWithValue(":projectId", (object?)projectId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue(":now", DateTime.UtcNow.ToString("o"));
                insertCmd.ExecuteNonQuery();

                _logger?.LogInfo($"Created protection_settings with guid {newGuid} for project {projectId}");
                return newGuid;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to ensure protection settings exist: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Load ALL command settings for UI display (not just enabled)
        /// Handles both old schema (basic columns) and new schema (extended columns)
        /// </summary>
        public List<CommandSettingViewModel> LoadAllCommandSettings(int? projectId)
        {
            var totalSw = Stopwatch.StartNew();
            var result = new List<CommandSettingViewModel>();

            try
            {
                var connSw = Stopwatch.StartNew();
                using var connection = OpenConnection();
                connSw.Stop();
                _logger?.LogDebug($"[TIMING] DB connection opened: {connSw.ElapsedMilliseconds}ms");

                // First, check what columns exist in command_settings table
                var pragmaSw = Stopwatch.StartNew();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var pragmaCmd = new SQLiteCommand("PRAGMA table_info(command_settings)", connection))
                using (var pragmaReader = pragmaCmd.ExecuteReader())
                {
                    while (pragmaReader.Read())
                    {
                        columns.Add(pragmaReader.GetString(1)); // Column name is at index 1
                    }
                }
                pragmaSw.Stop();
                _logger?.LogDebug($"[TIMING] PRAGMA table_info: {pragmaSw.ElapsedMilliseconds}ms");

                // Build dynamic query based on available columns
                // Required columns (always exist): id, command_code, is_enabled, intervention_mode, custom_message, is_company_level
                // Optional columns (may not exist in older schemas): capture_before_screenshot, capture_after_screenshot,
                //                    require_comment, allow_admin_override, modified_by, updated_at,
                //                    send_email, custom_message_image_path
                // Get all command settings (both enabled and disabled)
                var sql = @"
                    SELECT id, command_code, is_enabled, intervention_mode, custom_message,
                           is_company_level, updated_at, row_hmac
                    FROM command_settings
                    ORDER BY command_code";

                using var cmd = new SQLiteCommand(sql, connection);

                var hmacMap = new Dictionary<string, string?>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var viewModel = new CommandSettingViewModel
                    {
                        Id = id,
                        CommandCode = reader.GetString(1),
                        CommandName = GetCommandNameFromCode(reader.GetString(1)),
                        IsEnabled = reader.GetInt32(2) == 1,
                        Mode = (InterventionMode)reader.GetInt32(3),
                        CustomMessage = reader.IsDBNull(4) ? null : reader.GetString(4),
                        IsCompanyLevel = reader.GetInt32(5) == 1,
                        // Set defaults for optional columns - will be updated from extended query if columns exist
                        CaptureBeforeScreenshot = false,
                        CaptureAfterScreenshot = false,
                        RequireComment = false,
                        AllowAdminOverride = false,
                        ModifiedBy = null,
                        ModifiedAt = reader.IsDBNull(6) ? null : (DateTime.TryParse(reader.GetString(6), out var dt) ? dt : (DateTime?)null),
                        // New columns with defaults
                        SendEmail = false,
                        CustomMessageImagePath = null
                    };

                    // Capture row_hmac for verification after extended columns are loaded
                    hmacMap[id] = reader.IsDBNull(7) ? null : reader.GetString(7);

                    result.Add(viewModel);
                }

                // If extended columns exist, load them separately and update the view models
                bool hasExtendedColumns = columns.Contains("capture_before_screenshot") &&
                                          columns.Contains("capture_after_screenshot") &&
                                          columns.Contains("require_comment") &&
                                          columns.Contains("allow_admin_override");

                // Check for new columns (send_email, custom_message_image_path)
                bool hasNewColumns = columns.Contains("send_email") &&
                                     columns.Contains("custom_message_image_path");

                // Check for model_guid columns (project_id, company_id, model_guid)
                bool hasModelGuidColumns = columns.Contains("project_id") &&
                                           columns.Contains("company_id") &&
                                           columns.Contains("model_guid");

                // Check for created_by column
                bool hasCreatedByColumn = columns.Contains("created_by");

                if (hasExtendedColumns && result.Count > 0)
                {
                    // Build dynamic query based on available columns
                    var extSqlBuilder = new System.Text.StringBuilder();
                    extSqlBuilder.Append(@"
                        SELECT id, capture_before_screenshot, capture_after_screenshot,
                               require_comment, allow_admin_override, modified_by, updated_at");

                    if (hasNewColumns)
                    {
                        extSqlBuilder.Append(", send_email, custom_message_image_path");
                    }

                    if (hasModelGuidColumns)
                    {
                        extSqlBuilder.Append(", project_id, company_id, model_guid");
                    }

                    if (hasCreatedByColumn)
                    {
                        extSqlBuilder.Append(", created_by");
                    }

                    extSqlBuilder.Append(@"
                        FROM command_settings");

                    using var extCmd = new SQLiteCommand(extSqlBuilder.ToString(), connection);

                    using var extReader = extCmd.ExecuteReader();
                    var extData = new Dictionary<string, (bool capBefore, bool capAfter, bool reqComment, bool allowOverride, string? modBy, DateTime? modAt, bool sendEmail, string? imagePath, string? projectId, string? companyId, string? modelGuid, string? createdBy)>();

                    while (extReader.Read())
                    {
                        var id = extReader.IsDBNull(0) ? string.Empty : extReader.GetString(0);
                        var colIdx = 7; // Next index after base extended columns (0-6)

                        bool extSendEmail = false;
                        string? extImagePath = null;
                        string? extProjectId = null;
                        string? extCompanyId = null;
                        string? extModelGuid = null;
                        string? extCreatedBy = null;

                        if (hasNewColumns)
                        {
                            extSendEmail = !extReader.IsDBNull(colIdx) && extReader.GetInt32(colIdx) == 1;
                            colIdx++;
                            extImagePath = !extReader.IsDBNull(colIdx) ? extReader.GetString(colIdx) : null;
                            colIdx++;
                        }

                        if (hasModelGuidColumns)
                        {
                            extProjectId = !extReader.IsDBNull(colIdx) ? extReader.GetString(colIdx) : null;
                            colIdx++;
                            extCompanyId = !extReader.IsDBNull(colIdx) ? extReader.GetString(colIdx) : null;
                            colIdx++;
                            extModelGuid = !extReader.IsDBNull(colIdx) ? extReader.GetString(colIdx) : null;
                            colIdx++;
                        }

                        if (hasCreatedByColumn)
                        {
                            extCreatedBy = !extReader.IsDBNull(colIdx) ? extReader.GetString(colIdx) : null;
                        }

                        extData[id] = (
                            capBefore: !extReader.IsDBNull(1) && extReader.GetInt32(1) == 1,
                            capAfter: !extReader.IsDBNull(2) && extReader.GetInt32(2) == 1,
                            reqComment: !extReader.IsDBNull(3) && extReader.GetInt32(3) == 1,
                            allowOverride: extReader.IsDBNull(4) || extReader.GetInt32(4) == 1,
                            modBy: extReader.IsDBNull(5) ? null : extReader.GetString(5),
                            modAt: extReader.IsDBNull(6) ? null : DateTime.TryParse(extReader.GetString(6), out var dt) ? dt : null,
                            sendEmail: extSendEmail,
                            imagePath: extImagePath,
                            projectId: extProjectId,
                            companyId: extCompanyId,
                            modelGuid: extModelGuid,
                            createdBy: extCreatedBy
                        );
                    }

                    // Update view models with extended data
                    foreach (var vm in result)
                    {
                        if (extData.TryGetValue(vm.Id, out var ext))
                        {
                            vm.CaptureBeforeScreenshot = ext.capBefore;
                            vm.CaptureAfterScreenshot = ext.capAfter;
                            vm.RequireComment = ext.reqComment;
                            vm.AllowAdminOverride = ext.allowOverride;
                            vm.ModifiedBy = ext.modBy;
                            vm.ModifiedAt = ext.modAt;
                            vm.SendEmail = ext.sendEmail;
                            vm.CustomMessageImagePath = ext.imagePath;
                            vm.ProjectId = ext.projectId;
                            vm.CompanyId = ext.companyId;
                            vm.ModelGuid = ext.modelGuid;
                            vm.CreatedBy = ext.createdBy;
                        }
                    }
                }

                // Verify HMAC integrity after all columns are loaded.
                // HMAC failures are logged but rows are NOT excluded — excluding them breaks
                // real-time protection updates because of race conditions between SignalR handler
                // writes and concurrent LoadAndRegisterCommands reads.
                if (_integrityService?.IsIntegrityAvailable == true)
                {
                    foreach (var vm in result)
                    {
                        hmacMap.TryGetValue(vm.Id, out var storedHmac);
                        if (!VerifyCommandSettingHmac(vm.Id, vm, storedHmac, hasExtendedColumns))
                        {
                            _logger?.LogDebug($"HMAC mismatch for command_settings.{vm.Id} ({vm.CommandCode}) — may be mid-update, including anyway");
                        }
                    }
                }

                totalSw.Stop();
                _logger?.LogInfo($"[TIMING] Loaded {result.Count} command settings for UI (extended columns: {hasExtendedColumns}) TOTAL: {totalSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                _logger?.LogError($"Failed to load all command settings ({totalSw.ElapsedMilliseconds}ms): {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// Save a new command setting to database
        /// Handles both old schema (basic columns) and new schema (extended columns)
        /// </summary>
        public Task<string> SaveCommandSettingAsync(CommandSettingViewModel command, int? projectId)
        {
            return Task.Run(() => SaveCommandSetting(command, projectId));
        }

        /// <summary>
        /// Synchronous save — all DB work on a single thread (avoids COM apartment deadlocks).
        /// </summary>
        private string SaveCommandSetting(CommandSettingViewModel command, int? projectId)
        {
            try
            {
                using var connection = OpenConnection();

                // Check what columns exist
                var columns = GetTableColumns(connection, "command_settings");
                bool hasExtendedColumns = columns.Contains("capture_before_screenshot");
                bool hasNewColumns = columns.Contains("send_email");
                bool hasModelGuidColumns = columns.Contains("model_guid");
                bool hasProtectionSettingsId = columns.Contains("protection_settings_id");

                // Resolve the protection_settings_id for the FK constraint
                string protectionSettingsGuid = "00000000-0000-0000-0000-000000000001";
                if (hasProtectionSettingsId)
                {
                    var resolved = EnsureProtectionSettingsExist(projectId);
                    if (!string.IsNullOrEmpty(resolved))
                        protectionSettingsGuid = resolved;
                }

                var newId = Guid.NewGuid().ToString();

                string sql;
                // Check for created_by column in save context
                bool saveHasCreatedBy = columns.Contains("created_by");

                if (hasExtendedColumns && hasNewColumns && hasModelGuidColumns)
                {
                    // Full schema with all columns including model_guid
                    sql = @"
                        INSERT INTO command_settings (
                            id, protection_settings_id, command_code, is_enabled, intervention_mode,
                            custom_message, is_company_level, capture_before_screenshot, capture_after_screenshot,
                            require_comment, allow_admin_override, modified_by, updated_at,
                            send_email, custom_message_image_path,
                            project_id, company_id, model_guid" + (saveHasCreatedBy ? ", created_by" : "") + @"
                        ) VALUES (
                            :id, :protectionSettingsId, :commandCode, :isEnabled, :mode,
                            :customMessage, :isCompanyLevel, :captureBefore, :captureAfter,
                            :requireComment, :allowOverride, :modifiedBy, :updatedAt,
                            :sendEmail, :customMessageImagePath,
                            :projectId, :companyId, :modelGuid" + (saveHasCreatedBy ? ", :createdBy" : "") + @"
                        )";
                }
                else if (hasExtendedColumns && hasNewColumns)
                {
                    // Schema with send_email but without model_guid
                    sql = @"
                        INSERT INTO command_settings (
                            id, protection_settings_id, command_code, is_enabled, intervention_mode,
                            custom_message, is_company_level, capture_before_screenshot, capture_after_screenshot,
                            require_comment, allow_admin_override, modified_by, updated_at,
                            send_email, custom_message_image_path
                        ) VALUES (
                            :id, :protectionSettingsId, :commandCode, :isEnabled, :mode,
                            :customMessage, :isCompanyLevel, :captureBefore, :captureAfter,
                            :requireComment, :allowOverride, :modifiedBy, :updatedAt,
                            :sendEmail, :customMessageImagePath
                        )";
                }
                else if (hasExtendedColumns)
                {
                    // Extended schema without new columns
                    sql = @"
                        INSERT INTO command_settings (
                            id, protection_settings_id, command_code, is_enabled, intervention_mode,
                            custom_message, is_company_level, capture_before_screenshot, capture_after_screenshot,
                            require_comment, allow_admin_override, modified_by, updated_at
                        ) VALUES (
                            :id, :protectionSettingsId, :commandCode, :isEnabled, :mode,
                            :customMessage, :isCompanyLevel, :captureBefore, :captureAfter,
                            :requireComment, :allowOverride, :modifiedBy, :updatedAt
                        )";
                }
                else
                {
                    // Old schema with basic columns only
                    sql = @"
                        INSERT INTO command_settings (
                            id, protection_settings_id, command_code, is_enabled, intervention_mode,
                            custom_message, is_company_level
                        ) VALUES (
                            :id, :protectionSettingsId, :commandCode, :isEnabled, :mode,
                            :customMessage, :isCompanyLevel
                        )";
                }

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":id", newId);
                cmd.Parameters.AddWithValue(":protectionSettingsId", protectionSettingsGuid);
                cmd.Parameters.AddWithValue(":commandCode", command.CommandCode);
                cmd.Parameters.AddWithValue(":isEnabled", command.IsEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue(":mode", (int)command.Mode);
                cmd.Parameters.AddWithValue(":customMessage", command.CustomMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue(":isCompanyLevel", command.IsCompanyLevel ? 1 : 0);

                if (hasExtendedColumns)
                {
                    cmd.Parameters.AddWithValue(":captureBefore", command.CaptureBeforeScreenshot ? 1 : 0);
                    cmd.Parameters.AddWithValue(":captureAfter", command.CaptureAfterScreenshot ? 1 : 0);
                    cmd.Parameters.AddWithValue(":requireComment", command.RequireComment ? 1 : 0);
                    cmd.Parameters.AddWithValue(":allowOverride", command.AllowAdminOverride ? 1 : 0);
                    cmd.Parameters.AddWithValue(":modifiedBy", command.ModifiedBy ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(":updatedAt", DateTime.UtcNow.ToString("o"));

                    if (hasNewColumns)
                    {
                        cmd.Parameters.AddWithValue(":sendEmail", command.SendEmail ? 1 : 0);
                        cmd.Parameters.AddWithValue(":customMessageImagePath", command.CustomMessageImagePath ?? (object)DBNull.Value);
                    }

                    if (hasModelGuidColumns)
                    {
                        cmd.Parameters.AddWithValue(":projectId", command.ProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue(":companyId", command.CompanyId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue(":modelGuid", command.ModelGuid ?? (object)DBNull.Value);
                    }
                }

                if (saveHasCreatedBy)
                {
                    cmd.Parameters.AddWithValue(":createdBy", command.CreatedBy ?? (object)DBNull.Value);
                }

                cmd.ExecuteNonQuery();

                // Compute and store HMAC
                UpdateCommandSettingHmac(connection, newId, command, hasExtendedColumns);

                _logger?.LogInfo($"Saved new command setting: {command.CommandCode} (id: {newId})");
                return newId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save command setting: {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Update existing command setting
        /// Handles both old schema (basic columns) and new schema (extended columns)
        /// </summary>
        public Task<bool> UpdateCommandSettingAsync(CommandSettingViewModel command)
        {
            return Task.Run(() => UpdateCommandSetting(command));
        }

        /// <summary>
        /// Synchronous update — all DB work on a single thread (avoids COM apartment deadlocks).
        /// </summary>
        private bool UpdateCommandSetting(CommandSettingViewModel command)
        {
            try
            {
                using var connection = OpenConnection();

                // Check what columns exist
                var columns = GetTableColumns(connection, "command_settings");
                bool hasExtendedColumns = columns.Contains("capture_before_screenshot");
                bool hasNewColumns = columns.Contains("send_email");
                bool hasModelGuidColumns = columns.Contains("model_guid");

                string sql;
                if (hasExtendedColumns && hasNewColumns && hasModelGuidColumns)
                {
                    // Full schema with all columns including model_guid
                    // COALESCE: don't overwrite existing modified_by/created_by with NULL (e.g. when API returns null)
                    sql = @"
                        UPDATE command_settings SET
                            is_enabled = :isEnabled,
                            intervention_mode = :mode,
                            custom_message = :customMessage,
                            is_company_level = :isCompanyLevel,
                            capture_before_screenshot = :captureBefore,
                            capture_after_screenshot = :captureAfter,
                            require_comment = :requireComment,
                            allow_admin_override = :allowOverride,
                            modified_by = COALESCE(:modifiedBy, modified_by),
                            updated_at = :updatedAt,
                            send_email = :sendEmail,
                            custom_message_image_path = :customMessageImagePath,
                            project_id = :projectId,
                            company_id = :companyId,
                            model_guid = :modelGuid
                        WHERE id = :id";
                }
                else if (hasExtendedColumns && hasNewColumns)
                {
                    // Schema with send_email but without model_guid
                    sql = @"
                        UPDATE command_settings SET
                            is_enabled = :isEnabled,
                            intervention_mode = :mode,
                            custom_message = :customMessage,
                            is_company_level = :isCompanyLevel,
                            capture_before_screenshot = :captureBefore,
                            capture_after_screenshot = :captureAfter,
                            require_comment = :requireComment,
                            allow_admin_override = :allowOverride,
                            modified_by = COALESCE(:modifiedBy, modified_by),
                            updated_at = :updatedAt,
                            send_email = :sendEmail,
                            custom_message_image_path = :customMessageImagePath
                        WHERE id = :id";
                }
                else if (hasExtendedColumns)
                {
                    sql = @"
                        UPDATE command_settings SET
                            is_enabled = :isEnabled,
                            intervention_mode = :mode,
                            custom_message = :customMessage,
                            is_company_level = :isCompanyLevel,
                            capture_before_screenshot = :captureBefore,
                            capture_after_screenshot = :captureAfter,
                            require_comment = :requireComment,
                            allow_admin_override = :allowOverride,
                            modified_by = COALESCE(:modifiedBy, modified_by),
                            updated_at = :updatedAt
                        WHERE id = :id";
                }
                else
                {
                    sql = @"
                        UPDATE command_settings SET
                            is_enabled = :isEnabled,
                            intervention_mode = :mode,
                            custom_message = :customMessage,
                            is_company_level = :isCompanyLevel
                        WHERE id = :id";
                }

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue(":id", command.Id);
                cmd.Parameters.AddWithValue(":isEnabled", command.IsEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue(":mode", (int)command.Mode);
                cmd.Parameters.AddWithValue(":customMessage", command.CustomMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue(":isCompanyLevel", command.IsCompanyLevel ? 1 : 0);

                if (hasExtendedColumns)
                {
                    cmd.Parameters.AddWithValue(":captureBefore", command.CaptureBeforeScreenshot ? 1 : 0);
                    cmd.Parameters.AddWithValue(":captureAfter", command.CaptureAfterScreenshot ? 1 : 0);
                    cmd.Parameters.AddWithValue(":requireComment", command.RequireComment ? 1 : 0);
                    cmd.Parameters.AddWithValue(":allowOverride", command.AllowAdminOverride ? 1 : 0);
                    cmd.Parameters.AddWithValue(":modifiedBy", command.ModifiedBy ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(":updatedAt", DateTime.UtcNow.ToString("o"));

                    if (hasNewColumns)
                    {
                        cmd.Parameters.AddWithValue(":sendEmail", command.SendEmail ? 1 : 0);
                        cmd.Parameters.AddWithValue(":customMessageImagePath", command.CustomMessageImagePath ?? (object)DBNull.Value);
                    }

                    if (hasModelGuidColumns)
                    {
                        cmd.Parameters.AddWithValue(":projectId", command.ProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue(":companyId", command.CompanyId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue(":modelGuid", command.ModelGuid ?? (object)DBNull.Value);
                    }
                }

                var rows = cmd.ExecuteNonQuery();

                // Recompute HMAC after update
                UpdateCommandSettingHmac(connection, command.Id, command, hasExtendedColumns);

                _logger?.LogDebug($"Updated command setting: {command.CommandCode} (id: {command.Id})");
                return rows > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update command setting: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Upsert: update existing command setting by command_code, or insert new.
        /// Prevents duplicates when the API returns rules that already exist locally.
        /// </summary>
        public Task<string> SaveOrUpdateCommandSettingAsync(CommandSettingViewModel command, int? projectId)
        {
            return Task.Run(() => SaveOrUpdateCommandSetting(command, projectId));
        }

        /// <summary>
        /// Synchronous upsert — all DB work on a single thread (avoids COM apartment deadlocks).
        /// </summary>
        private string SaveOrUpdateCommandSetting(CommandSettingViewModel command, int? projectId)
        {
            try
            {
                using var connection = OpenConnection();

                // Check if a row with this command_code already exists
                var checkSql = "SELECT id FROM command_settings WHERE command_code = :code LIMIT 1";
                using var checkCmd = new SQLiteCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue(":code", command.CommandCode);
                var existingId = checkCmd.ExecuteScalar() as string;

                if (!string.IsNullOrEmpty(existingId))
                {
                    // Update existing row (call synchronous version directly)
                    command.Id = existingId;
                    var updated = UpdateCommandSetting(command);
                    if (updated)
                    {
                        _logger?.LogDebug($"Upsert: updated existing command '{command.CommandCode}' (id: {existingId})");
                        return existingId;
                    }
                    return string.Empty;
                }

                // Insert new row (call synchronous version directly)
                return SaveCommandSetting(command, projectId);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to upsert command setting '{command.CommandCode}': {ex.Message}", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets all command setting IDs and codes for a specific model (for reconciliation).
        /// </summary>
        public async Task<List<(string Id, string CommandCode)>> GetCommandSettingIdsByModelGuidAsync(string modelGuid)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT id, command_code FROM command_settings WHERE model_guid = :modelGuid", connection);
                cmd.Parameters.AddWithValue(":modelGuid", modelGuid);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var code = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, code ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get command settings by model: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Returns every locally-cached command_settings row that the server's
        /// <c>/by-model/{modelGuid}</c> response is expected to include — i.e. all rows
        /// that apply to the current model context across the three scopes:
        /// <list type="bullet">
        ///   <item>Model-level row tied to <paramref name="currentModelGuid"/></item>
        ///   <item>Company-level rows (model_guid IS NULL AND project_id IS NULL) for the current company</item>
        ///   <item>Project-level rows (model_guid IS NULL AND project_id matches) for the
        ///     project that <paramref name="currentModelGuid"/> belongs to, derived from
        ///     <c>registered_models.zemanage_project_id</c></item>
        /// </list>
        /// Used by <c>FetchByModelGuidFromApiAsync</c>'s reconciliation block to find
        /// orphans — rows in this set whose <c>command_code</c> isn't in the server
        /// response should be deleted locally. The older
        /// <see cref="GetCommandSettingIdsByModelGuidAsync"/> only covered the first
        /// bullet, which is why company- and project-scoped rows stayed stale forever
        /// after a web-side delete.
        /// </summary>
        public async Task<List<(string Id, string CommandCode)>> GetCommandSettingIdsForFetchScopeAsync(string currentModelGuid, string? companyId)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = OpenConnection();
                // Single query covers all three scopes (model / company / project) and
                // pins them to the user's company so other-tenant rows can't get caught
                // in the sweep. Project_id for the current model is resolved via a
                // subquery against registered_models.zemanage_project_id — if the model
                // isn't registered locally that subquery returns NULL and the
                // project-level branch silently no-ops (correct: we have no project to
                // match against, so don't touch any project-level row).
                const string sql = @"
                    SELECT id, command_code FROM command_settings
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

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var code = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, code ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get command settings for fetch scope: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Updates the id of an existing command_settings row identified by command_code.
        /// Used after POST to API returns a server-assigned id that differs from the client-generated one.
        /// </summary>
        public Task<bool> UpdateCommandIdByCodeAsync(string commandCode, string newId)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(commandCode) || string.IsNullOrWhiteSpace(newId))
                        return false;

                    using var connection = OpenConnection();
                    using var cmd = new SQLiteCommand(
                        "UPDATE command_settings SET id = :newId WHERE command_code = :code", connection);
                    cmd.Parameters.AddWithValue(":newId", newId);
                    cmd.Parameters.AddWithValue(":code", commandCode);
                    var rows = cmd.ExecuteNonQuery();
                    _logger?.LogInfo($"Updated local command_settings id for code '{commandCode}' to '{newId}' ({rows} row(s))");
                    return rows > 0;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to update command id for code '{commandCode}': {ex.Message}", ex);
                    return false;
                }
            });
        }

        /// <summary>
        /// Deletes local command_settings rows whose command_code is NOT in the given
        /// keep list. Used after a successful server fetch to remove orphans that
        /// were deleted on the server but still exist locally.
        /// </summary>
        public Task<int> DeleteCommandSettingsNotInCodesAsync(IReadOnlyCollection<string> keepCodes)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var connection = OpenConnection();

                    if (keepCodes == null || keepCodes.Count == 0)
                    {
                        using var cmdAll = new SQLiteCommand("DELETE FROM command_settings", connection);
                        var rowsAll = cmdAll.ExecuteNonQuery();
                        if (rowsAll > 0) _logger?.LogInfo($"Deleted {rowsAll} orphan command_settings row(s) (server returned none)");
                        return rowsAll;
                    }

                    var placeholders = string.Join(",", Enumerable.Range(0, keepCodes.Count).Select(i => $"@c{i}"));
                    using var cmd = new SQLiteCommand(
                        $"DELETE FROM command_settings WHERE command_code NOT IN ({placeholders})", connection);
                    int i2 = 0;
                    foreach (var code in keepCodes)
                    {
                        cmd.Parameters.AddWithValue($"@c{i2}", code ?? string.Empty);
                        i2++;
                    }
                    var rows = cmd.ExecuteNonQuery();
                    if (rows > 0) _logger?.LogInfo($"Deleted {rows} orphan command_settings row(s) not in server response");
                    return rows;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to delete orphan command_settings: {ex.Message}", ex);
                    return 0;
                }
            });
        }

        /// <summary>
        /// Deletes all command_settings rows that match the given command_code.
        /// Used as a safety net when the client/server id drifted.
        /// </summary>
        public Task<int> DeleteCommandSettingsByCodeAsync(string commandCode)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(commandCode)) return 0;
                    using var connection = OpenConnection();
                    using var cmd = new SQLiteCommand(
                        "DELETE FROM command_settings WHERE command_code = :code", connection);
                    cmd.Parameters.AddWithValue(":code", commandCode);
                    var rows = cmd.ExecuteNonQuery();
                    if (rows > 0)
                        _logger?.LogInfo($"Deleted {rows} local command_settings row(s) for code '{commandCode}'");
                    return rows;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to delete command_settings by code '{commandCode}': {ex.Message}", ex);
                    return 0;
                }
            });
        }

        /// <summary>
        /// Deletes every locally-cached command setting whose <c>company_id</c> is set and
        /// does not match the currently signed-in user's company. Used by
        /// CommandProtectionSyncService at fetch time to evict rows left over from a
        /// previous session (e.g. signed in to a different company on the same device).
        /// Rows with <c>company_id IS NULL</c> are preserved — they are pre-tenancy
        /// records that haven't been classified yet. Returns the number of rows deleted.
        /// </summary>
        public Task<int> DeleteCommandSettingsByMismatchedCompanyAsync(string currentCompanyId)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(currentCompanyId)) return 0;
                try
                {
                    using var connection = OpenConnection();
                    const string sql = "DELETE FROM command_settings WHERE company_id IS NOT NULL AND company_id != :companyId COLLATE NOCASE";
                    using var cmd = new SQLiteCommand(sql, connection);
                    cmd.Parameters.AddWithValue(":companyId", currentCompanyId);
                    return cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"DeleteCommandSettingsByMismatchedCompanyAsync failed: {ex.Message}");
                    return 0;
                }
            });
        }

        /// <summary>
        /// Delete command setting by ID
        /// </summary>
        public Task<bool> DeleteCommandSettingAsync(string commandId)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var connection = OpenConnection();

                    var sql = "DELETE FROM command_settings WHERE id = :id";

                    using var cmd = new SQLiteCommand(sql, connection);
                    cmd.Parameters.AddWithValue(":id", commandId);

                    var rows = cmd.ExecuteNonQuery();
                    _logger?.LogInfo($"Deleted command setting id: {commandId}");
                    return rows > 0;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to delete command setting: {ex.Message}", ex);
                    return false;
                }
            });
        }

        /// <summary>
        /// Helper to convert command code to display name
        /// </summary>
        private string GetCommandNameFromCode(string commandCode)
        {
            // Step 1: Look up friendly name from command_cache table
            try
            {
                using var conn = OpenConnection();
                using var cmd = new SQLiteCommand(
                    "SELECT command_name FROM command_cache WHERE member_name_24 = @code OR revit_command_id = @code LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@code", commandCode);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    var name = result.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch { /* Fall back to hardcoded mapping */ }

            // Step 2: Hardcoded mapping for common commands
            return commandCode switch
            {
                "ID_EDIT_DELETE" => "Delete",
                "ID_EDIT_MOVE" => "Move",
                "ID_EDIT_COPY" => "Copy",
                "ID_EDIT_ROTATE" => "Rotate",
                "ID_EDIT_MIRROR" => "Mirror",
                "ID_EDIT_ARRAY" => "Array",
                "ID_EDIT_OFFSET" => "Offset",
                "ID_EDIT_PIN" => "Pin",
                "ID_EDIT_UNPIN" => "Unpin",
                "ID_OBJECTS_WALL" => "Wall",
                "ID_OBJECTS_DOOR" => "Door",
                "ID_OBJECTS_WINDOW" => "Window",
                "ID_OBJECTS_FLOOR" => "Floor",
                "ID_OBJECTS_ROOF" => "Roof",
                "ID_OBJECTS_CEILING" => "Ceiling",
                "ID_OBJECTS_STAIR" => "Stair",
                "ID_OBJECTS_RAMP" => "Ramp",
                "ID_OBJECTS_COLUMN" => "Column",
                "ID_FILE_SAVE" => "Save",
                "ID_FILE_SAVE_AS" => "Save As",
                "ID_FILE_EXPORT" => "Export",
                "ID_FILE_PRINT" => "Print",
                "ID_RVTLINK_LINK" => "Link Revit",
                "ID_RVTLINK_IMPORT_CAD" => "Import CAD",
                "ID_RVTLINK_IMPORT_IMAGE" => "Import Image",
                "ID_VIEW_NEW_SHEET" => "New Sheet",
                "ID_VIEW_NEW_SCHEDULE" => "New Schedule",
                "ID_VIEW_NEW_3D" => "3D View",
                "ID_VIEW_NEW_DRAFTING" => "Drafting View",
                "ID_GROUP_CREATE" => "Create Group",
                "ID_GROUP_UNGROUP" => "Ungroup",
                "ID_WORKSET_CREATE" => "Create Workset",
                "ID_DESIGN_OPTION" => "Design Options",
                _ => commandCode.Replace("ID_", "").Replace("_", " ")
            };
        }

        private bool ColumnExists(SQLiteConnection connection, string tableName, string columnName)
        {
            return GetTableColumns(connection, tableName).Contains(columnName);
        }

        /// <summary>
        /// Get set of column names for a table (for schema compatibility checks)
        /// </summary>
        private HashSet<string> GetTableColumns(SQLiteConnection connection, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SQLiteCommand($"PRAGMA table_info({tableName})", connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1)); // Column name is at index 1
                }
            }
            return columns;
        }

        /// <summary>
        /// Computes and stores HMAC for a command_settings row.
        /// Core columns: command_code, is_enabled, intervention_mode, custom_message, is_company_level.
        /// Extended columns added when present.
        /// </summary>
        private void UpdateCommandSettingHmac(SQLiteConnection connection, string id, CommandSettingViewModel command, bool hasExtendedColumns)
        {
            if (_integrityService == null || !_integrityService.IsIntegrityAvailable) return;

            try
            {
                var hmacValues = new List<string?>
                {
                    command.CommandCode,
                    command.IsEnabled ? "1" : "0",
                    ((int)command.Mode).ToString(),
                    command.CustomMessage,
                    command.IsCompanyLevel ? "1" : "0"
                };

                if (hasExtendedColumns)
                {
                    hmacValues.Add(command.CaptureBeforeScreenshot ? "1" : "0");
                    hmacValues.Add(command.CaptureAfterScreenshot ? "1" : "0");
                    hmacValues.Add(command.RequireComment ? "1" : "0");
                    hmacValues.Add(command.AllowAdminOverride ? "1" : "0");
                }

                var hmac = _integrityService.ComputeHmac("command_settings", id, hmacValues.ToArray());
                if (!string.IsNullOrEmpty(hmac))
                {
                    using var hmacCmd = new SQLiteCommand(
                        "UPDATE command_settings SET row_hmac = :hmac WHERE id = :id", connection);
                    hmacCmd.Parameters.AddWithValue(":hmac", hmac);
                    hmacCmd.Parameters.AddWithValue(":id", id);
                    hmacCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update HMAC for command_settings.{id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Verifies HMAC for a command setting. Returns false if tampered.
        /// </summary>
        private bool VerifyCommandSettingHmac(string id, CommandSettingViewModel command, string? storedHmac, bool hasExtendedColumns)
        {
            if (_integrityService == null) return true;

            var hmacValues = new List<string?>
            {
                command.CommandCode,
                command.IsEnabled ? "1" : "0",
                ((int)command.Mode).ToString(),
                command.CustomMessage,
                command.IsCompanyLevel ? "1" : "0"
            };

            if (hasExtendedColumns)
            {
                hmacValues.Add(command.CaptureBeforeScreenshot ? "1" : "0");
                hmacValues.Add(command.CaptureAfterScreenshot ? "1" : "0");
                hmacValues.Add(command.RequireComment ? "1" : "0");
                hmacValues.Add(command.AllowAdminOverride ? "1" : "0");
            }

            return _integrityService.VerifyHmac("command_settings", id, storedHmac, hmacValues.ToArray());
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
            connection.Open();
            return connection;
        }
    }
}
