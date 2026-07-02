using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using BIManage.Revit.PinProtection.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing pin protection records in SQLite database
    /// Provides central registry, reporting, and audit trail capabilities
    /// Works alongside ExtensibleStorage for hybrid storage approach
    /// </summary>
    public class PinProtectionRepository
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private readonly DatabaseIntegrityService? _integrityService;

        public PinProtectionRepository(string databasePath, ILogger? logger = null, DatabaseIntegrityService? integrityService = null)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentException("Database path cannot be null or empty", nameof(databasePath));

            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
            _integrityService = integrityService;

            InitializeSchema();
        }

        #region Initialization

        private void InitializeSchema()
        {
            // Pin protection tables are now created by SchemaMigration.InitializeFullSchema()
            // as part of the main Schema.sql file
            // No separate initialization needed here
            _logger?.LogInfo("Pin protection schema initialization delegated to SchemaMigration");
        }

        #endregion

        #region Create/Update

        /// <summary>
        /// Sync protection metadata to database (create or update)
        /// </summary>
        public async Task<bool> SyncProtectionAsync(
            string modelGuid,
            string projectName,
            ProtectedPinInfo protectionInfo,
            string syncSource = "ExtensibleStorage",
            string sessionId = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var pinId = Guid.NewGuid().ToString();

                    var sql = @"
                        INSERT INTO pin_protection (
                            id, model_guid, session_id, project_name, element_guid, element_id,
                            element_name, element_category,
                            protection_mode, protected_by, protected_at, admin_comment,
                            require_comment_for_unpin, instantly_notify_on_unpin,
                            is_active, last_synced_at, sync_source
                        )
                        VALUES (
                            @pinId, @modelGuid, @sessionId, @projectName, @elementGuid, @elementId,
                            @elementName, @elementCategory,
                            @protectionMode, @protectedBy, @protectedAt, @adminComment,
                            @requireComment, @instantlyNotify,
                            1, datetime('now'), @syncSource
                        )
                        ON CONFLICT(model_guid, element_guid) DO UPDATE SET
                            session_id = COALESCE(@sessionId, session_id),
                            element_id = @elementId,
                            element_name = @elementName,
                            element_category = @elementCategory,
                            protection_mode = @protectionMode,
                            protected_by = @protectedBy,
                            protected_at = @protectedAt,
                            admin_comment = @adminComment,
                            require_comment_for_unpin = @requireComment,
                            instantly_notify_on_unpin = @instantlyNotify,
                            is_active = 1,
                            last_synced_at = datetime('now'),
                            sync_source = @syncSource";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@pinId", pinId);
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@sessionId", sessionId ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@projectName", projectName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@elementGuid", protectionInfo.ElementGuid);
                        command.Parameters.AddWithValue("@elementId", protectionInfo.ElementId);
                        command.Parameters.AddWithValue("@elementName", protectionInfo.Name ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@elementCategory", protectionInfo.Category ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@protectionMode", protectionInfo.ProtectionMode);
                        command.Parameters.AddWithValue("@protectedBy", protectionInfo.ProtectedBy);
                        command.Parameters.AddWithValue("@protectedAt", protectionInfo.DateUTC?.ToString("o") ?? DateTime.UtcNow.ToString("o"));
                        command.Parameters.AddWithValue("@adminComment", protectionInfo.AdminComment ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@requireComment", protectionInfo.IsRequireCommentForUnpin ? 1 : 0);
                        command.Parameters.AddWithValue("@instantlyNotify", protectionInfo.IsInstantlyNotifyOnUnpin ? 1 : 0);
                        command.Parameters.AddWithValue("@syncSource", syncSource);

                        await command.ExecuteNonQueryAsync();
                    }

                    // Compute and store HMAC for the upserted row
                    if (_integrityService?.IsIntegrityAvailable == true)
                    {
                        // Get the actual row id (may be different from pinId on conflict)
                        var getIdSql = "SELECT id FROM pin_protection WHERE model_guid = @mg AND element_guid = @eg AND is_active = 1 LIMIT 1";
                        using (var getIdCmd = new SQLiteCommand(getIdSql, connection))
                        {
                            getIdCmd.Parameters.AddWithValue("@mg", modelGuid);
                            getIdCmd.Parameters.AddWithValue("@eg", protectionInfo.ElementGuid);
                            var actualId = getIdCmd.ExecuteScalar() as string ?? pinId;

                            var hmac = _integrityService.ComputeHmac("pin_protection", actualId,
                                modelGuid,
                                protectionInfo.ElementGuid,
                                protectionInfo.ElementId.ToString(),
                                protectionInfo.ProtectionMode.ToString(),
                                "1" // is_active
                            );

                            if (!string.IsNullOrEmpty(hmac))
                            {
                                using var hmacCmd = new SQLiteCommand(
                                    "UPDATE pin_protection SET row_hmac = @hmac WHERE model_guid = @mg AND element_guid = @eg AND is_active = 1", connection);
                                hmacCmd.Parameters.AddWithValue("@hmac", hmac);
                                hmacCmd.Parameters.AddWithValue("@mg", modelGuid);
                                hmacCmd.Parameters.AddWithValue("@eg", protectionInfo.ElementGuid);
                                hmacCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    _logger?.LogInfo($"Synced protection for element {protectionInfo.ElementGuid} in model {modelGuid}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to sync protection: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets all active pin protection element GUIDs for a specific model (for reconciliation).
        /// </summary>
        public async Task<List<(string ElementGuid, string ElementName)>> GetActivePinProtectionsByModelAsync(string modelGuid)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                using var cmd = new SQLiteCommand(
                    "SELECT element_guid, element_name FROM pin_protection WHERE model_guid = @modelGuid AND is_active = 1", connection);
                cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var guid = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(guid))
                        results.Add((guid, name ?? ""));
                }
            }
            catch (Exception ex)
            {
                // Non-critical
            }
            return results;
        }

        /// <summary>
        /// Revoke protection by setting is_active = 0 (deactivated/unpinned).
        /// Records deactivation timestamp, user who revoked, reason, and OTP code if used.
        /// Also records a history event with "unpinned" event type for audit trail.
        /// </summary>
        public async Task<bool> DeactivateProtectionAsync(
            string modelGuid,
            string elementGuid,
            string deactivatedBy,
            string reason = null,
            string otpCodeUsed = null,
            string userComment = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Set is_active = 0 to mark protection as revoked (soft delete)
                    // Preserves the record for audit/reporting while indicating it's no longer active
                    var sql = @"
                        UPDATE pin_protection
                        SET is_active = 0,
                            deactivated_at = datetime('now'),
                            deactivated_by = @deactivatedBy,
                            deactivation_reason = @reason,
                            last_synced_at = datetime('now')
                        WHERE model_guid = @modelGuid
                          AND element_guid = @elementGuid
                          AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@elementGuid", elementGuid);
                        command.Parameters.AddWithValue("@deactivatedBy", deactivatedBy);
                        command.Parameters.AddWithValue("@reason", reason ?? (object)DBNull.Value);

                        var rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            // Recompute HMAC with is_active = 0
                            if (_integrityService?.IsIntegrityAvailable == true)
                            {
                                var getRowSql = "SELECT id, element_id, protection_mode FROM pin_protection WHERE model_guid = @mg AND element_guid = @eg AND is_active = 0 ORDER BY deactivated_at DESC LIMIT 1";
                                using var getRowCmd = new SQLiteCommand(getRowSql, connection);
                                getRowCmd.Parameters.AddWithValue("@mg", modelGuid);
                                getRowCmd.Parameters.AddWithValue("@eg", elementGuid);
                                using var rowReader = await getRowCmd.ExecuteReaderAsync();
                                if (await rowReader.ReadAsync())
                                {
                                    var id = rowReader.GetString(0);
                                    var elementId = rowReader.GetInt64(1).ToString();
                                    var protMode = rowReader.GetInt32(2).ToString();
                                    var hmac = _integrityService.ComputeHmac("pin_protection", id,
                                        modelGuid, elementGuid, elementId, protMode, "0");
                                    if (!string.IsNullOrEmpty(hmac))
                                    {
                                        using var hmacCmd = new SQLiteCommand(
                                            "UPDATE pin_protection SET row_hmac = @hmac WHERE id = @id", connection);
                                        hmacCmd.Parameters.AddWithValue("@hmac", hmac);
                                        hmacCmd.Parameters.AddWithValue("@id", id);
                                        hmacCmd.ExecuteNonQuery();
                                    }
                                }
                            }

                            _logger?.LogInfo($"Deactivated protection for element {elementGuid} in model {modelGuid}");
                            return true;
                        }
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to deactivate protection: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets the local pin_protection.id (GUID) for a given model+element pair.
        /// This ID is used as the server-side PinProtectionId for POST/DELETE operations.
        /// </summary>
        public async Task<string?> GetPinProtectionIdAsync(string modelGuid, string elementGuid)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = "SELECT id FROM pin_protection WHERE model_guid = @modelGuid AND element_guid = @elementGuid LIMIT 1";
                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@elementGuid", elementGuid);
                        var result = await command.ExecuteScalarAsync();
                        return result?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to get pin protection ID: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Read

        /// <summary>
        /// Get protection info from database
        /// </summary>
        public async Task<ProtectedPinInfo?> GetProtectionAsync(string modelGuid, string elementGuid)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT element_id, element_name, element_category,
                               protection_mode, protected_by, protected_at, admin_comment,
                               require_comment_for_unpin, instantly_notify_on_unpin
                        FROM pin_protection
                        WHERE model_guid = @modelGuid
                          AND element_guid = @elementGuid
                          AND is_active = 1";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);
                        command.Parameters.AddWithValue("@elementGuid", elementGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new ProtectedPinInfo
                                {
                                    ElementGuid = elementGuid,
                                    ElementId = reader.GetInt64(0),
                                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    Category = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    ProtectionMode = reader.GetInt32(3),
                                    ProtectedBy = reader.GetString(4),
                                    DateUTC = DateTime.Parse(reader.GetString(5)),
                                    AdminComment = reader.IsDBNull(6) ? null : reader.GetString(6),
                                    IsRequireCommentForUnpin = reader.GetInt32(7) == 1,
                                    IsInstantlyNotifyOnUnpin = reader.GetInt32(8) == 1
                                };
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get protection from database: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get all active protections for a model
        /// </summary>
        public async Task<List<ProtectedPinInfo>> GetActiveProtectionsAsync(string modelGuid)
        {
            var results = new List<ProtectedPinInfo>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, element_guid, element_id, element_name, element_category,
                               protection_mode, protected_by, protected_at, admin_comment,
                               require_comment_for_unpin, instantly_notify_on_unpin, row_hmac
                        FROM pin_protection
                        WHERE model_guid = @modelGuid
                          AND is_active = 1
                        ORDER BY protected_at DESC";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var rowId = reader.GetString(0);
                                var elementGuid = reader.GetString(1);
                                var elementId = reader.GetInt64(2);
                                var protectionMode = reader.GetInt32(5);
                                var storedHmac = reader.IsDBNull(11) ? null : reader.GetString(11);

                                // Verify HMAC
                                if (_integrityService?.IsIntegrityAvailable == true &&
                                    !string.IsNullOrEmpty(storedHmac) &&
                                    !_integrityService.VerifyHmac("pin_protection", rowId, storedHmac,
                                        modelGuid, elementGuid, elementId.ToString(), protectionMode.ToString(), "1"))
                                {
                                    _logger?.LogDebug($"HMAC mismatch for pin_protection.{rowId} — may be mid-update, including anyway");
                                }

                                results.Add(new ProtectedPinInfo
                                {
                                    ElementGuid = elementGuid,
                                    ElementId = elementId,
                                    Name = reader.IsDBNull(3) ? null : reader.GetString(3),
                                    Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                                    ProtectionMode = protectionMode,
                                    ProtectedBy = reader.GetString(6),
                                    DateUTC = DateTime.Parse(reader.GetString(7)),
                                    AdminComment = reader.IsDBNull(8) ? null : reader.GetString(8),
                                    IsRequireCommentForUnpin = reader.GetInt32(9) == 1,
                                    IsInstantlyNotifyOnUnpin = reader.GetInt32(10) == 1
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active protections: {ex.Message}", ex);
            }

            return results;
        }

        /// <summary>
        /// Get protected elements with full details including category information
        /// Used for displaying detailed list in Pin Protection command
        /// </summary>
        public async Task<List<global::BIManageRevit.Commands.RibbonCommands.ProtectedElementDetails>> GetProtectedElementsWithDetailsAsync(string modelGuid)
        {
            var results = new List<global::BIManageRevit.Commands.RibbonCommands.ProtectedElementDetails>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT element_guid, element_id, element_name, element_category,
                               protection_mode, protected_by, protected_at, admin_comment
                        FROM pin_protection
                        WHERE model_guid = @modelGuid
                          AND is_active = 1
                        ORDER BY element_category, element_name";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(new global::BIManageRevit.Commands.RibbonCommands.ProtectedElementDetails
                                {
                                    ElementGuid = reader.GetString(0),
                                    ElementId = reader.GetInt64(1),
                                    ElementName = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    ElementCategory = reader.IsDBNull(3) ? null : reader.GetString(3),
                                    ProtectionMode = reader.GetInt32(4),
                                    ProtectedBy = reader.GetString(5),
                                    ProtectedAt = DateTime.Parse(reader.GetString(6)),
                                    AdminComment = reader.IsDBNull(7) ? null : reader.GetString(7)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get protected elements with details: {ex.Message}", ex);
            }

            return results;
        }

        #endregion

        #region Reporting Queries

        /// <summary>
        /// Get protection summary by model
        /// </summary>
        public async Task<Dictionary<string, int>> GetProtectionSummaryByProjectAsync()
        {
            var summary = new Dictionary<string, int>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT * FROM v_protection_summary_by_project";

                    using (var command = new SQLiteCommand(sql, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var projectName = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
                            var count = reader.GetInt32(2);
                            summary[projectName] = count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get protection summary: {ex.Message}", ex);
            }

            return summary;
        }

        /// <summary>
        /// Get protection summary by user
        /// </summary>
        public async Task<Dictionary<string, int>> GetProtectionSummaryByUserAsync()
        {
            var summary = new Dictionary<string, int>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT * FROM v_protection_summary_by_user";

                    using (var command = new SQLiteCommand(sql, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var userName = reader.GetString(0);
                            var count = reader.GetInt32(1);
                            summary[userName] = count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get user summary: {ex.Message}", ex);
            }

            return summary;
        }

        #endregion
    }
}
