using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing registered models (master authorization table)
    /// Only registered models receive data collection, protection, and evidence capture
    /// </summary>
    public class RegisteredModelsRepository
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public RegisteredModelsRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        /// <summary>
        /// Check if a model is registered and active
        /// </summary>
        public async Task<bool> IsModelRegisteredAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return false;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT COUNT(*)
                        FROM registered_models
                        WHERE model_guid = @modelGuid
                          AND is_active = 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check model registration: {ex.Message}", ex);
                return false; // Fail closed - if error, treat as not registered
            }
        }

        /// <summary>
        /// Check if a model is registered by its central model path
        /// Used as secondary check to prevent duplicates with different GUIDs
        /// </summary>
        public async Task<bool> IsModelRegisteredByPathAsync(string centralModelPath)
        {
            if (string.IsNullOrEmpty(centralModelPath))
                return false;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT COUNT(*)
                        FROM registered_models
                        WHERE central_model_path = @path
                          AND is_active = 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@path", centralModelPath);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check model registration by path: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get model registration status (exists + active status)
        /// Returns: (exists, isActive) tuple
        /// - (false, false) = not in database
        /// - (true, false) = exists but deactivated
        /// - (true, true) = exists and active
        /// </summary>
        public async Task<(bool Exists, bool IsActive)> GetModelRegistrationStatusAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return (false, false);

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT is_active
                        FROM registered_models
                        WHERE model_guid = @modelGuid";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                        var result = await cmd.ExecuteScalarAsync();

                        if (result == null)
                            return (false, false); // Not in database

                        var isActive = Convert.ToInt32(result) == 1;
                        return (true, isActive);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check registration status: {ex.Message}", ex);
                return (false, false);
            }
        }

        /// <summary>
        /// Get model registration details
        /// </summary>
        public async Task<RegisteredModel> GetModelAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return null;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, model_guid, model_name, central_model_path, project_name,
                               cloud_project_id, zemanage_project_id, is_active, registered_at, registered_by,
                               is_local_copy, is_workshared, is_family, is_cloudmodel,
                               model_type, notes, last_opened_at, last_opened_by
                        FROM registered_models
                        WHERE model_guid = @modelGuid";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new RegisteredModel
                                {
                                    Id = reader.GetInt32(0),
                                    ModelGuid = reader.GetString(1),
                                    ModelName = reader.GetString(2),
                                    CentralModelPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                                    ProjectName = reader.IsDBNull(4) ? null : reader.GetString(4),
                                    CloudProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
                                    ZemanageProjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
                                    IsActive = reader.GetInt32(7) == 1,
                                    RegisteredAt = DateTime.Parse(reader.GetString(8)),
                                    RegisteredBy = reader.GetString(9),
                                    IsLocalCopy = reader.GetInt32(10) == 1,
                                    IsWorkshared = reader.GetInt32(11) == 1,
                                    IsFamily = reader.GetInt32(12) == 1,
                                    IsCloudModel = reader.GetInt32(13) == 1,
                                    ModelType = reader.IsDBNull(14) ? null : reader.GetString(14),
                                    Notes = reader.IsDBNull(15) ? null : reader.GetString(15),
                                    LastOpenedAt = reader.IsDBNull(16) ? (DateTime?)null : DateTime.Parse(reader.GetString(16)),
                                    LastOpenedBy = reader.IsDBNull(17) ? null : reader.GetString(17)
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get registered model: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Register a new model
        /// </summary>
        public async Task<bool> RegisterModelAsync(RegisteredModel model)
        {
            if (model == null || string.IsNullOrEmpty(model.ModelGuid))
                return false;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO registered_models (
                            model_guid, model_name, central_model_path, project_name, cloud_project_id, zemanage_project_id,
                            registered_by, model_type, is_active,
                            is_local_copy, is_workshared, is_family, is_cloudmodel, notes
                        ) VALUES (
                            @modelGuid, @modelName, @centralPath, @projectName, @cloudProjectId, @zemanageProjectId,
                            @registeredBy, @modelType, 1,
                            @isLocalCopy, @isWorkshared, @isFamily, @isCloudModel, @notes
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", model.ModelGuid);
                        cmd.Parameters.AddWithValue("@modelName", model.ModelName ?? "Unknown Model");
                        cmd.Parameters.AddWithValue("@centralPath", model.CentralModelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@projectName", model.ProjectName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@cloudProjectId", model.CloudProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@zemanageProjectId", model.ZemanageProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@registeredBy", model.RegisteredBy ?? "System");
                        cmd.Parameters.AddWithValue("@modelType", model.ModelType ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@isLocalCopy", model.IsLocalCopy ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isWorkshared", model.IsWorkshared ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isFamily", model.IsFamily ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isCloudModel", model.IsCloudModel ? 1 : 0);
                        cmd.Parameters.AddWithValue("@notes", model.Notes ?? (object)DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Registered model: {model.ModelName} ({model.ModelGuid})");
                        return true;
                    }
                }
            }
            catch (SQLiteException ex) when (ex.Message.Contains("UNIQUE constraint"))
            {
                _logger?.LogWarning($"Model already registered: {model.ModelGuid}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register model: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Register or update a model (upsert pattern)
        /// Handles duplicates gracefully by updating existing records
        /// </summary>
        public async Task<bool> RegisterOrUpdateModelAsync(RegisteredModel model)
        {
            if (model == null || string.IsNullOrEmpty(model.ModelGuid))
                return false;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO registered_models (
                            model_guid, model_name, central_model_path, project_name,
                            cloud_project_id, zemanage_project_id,
                            registered_by, model_type, is_active,
                            is_local_copy, is_workshared, is_family, is_cloudmodel, notes,
                            last_opened_at, last_opened_by
                        ) VALUES (
                            @modelGuid, @modelName, @centralPath, @projectName,
                            @cloudProjectId, @zemanageProjectId,
                            @registeredBy, @modelType, 1,
                            @isLocalCopy, @isWorkshared, @isFamily, @isCloudModel, @notes,
                            datetime('now'), @registeredBy
                        )
                        ON CONFLICT(model_guid) DO UPDATE SET
                            model_name = COALESCE(excluded.model_name, model_name),
                            central_model_path = COALESCE(excluded.central_model_path, central_model_path),
                            project_name = COALESCE(excluded.project_name, project_name),
                            cloud_project_id = COALESCE(excluded.cloud_project_id, cloud_project_id),
                            zemanage_project_id = COALESCE(excluded.zemanage_project_id, zemanage_project_id),
                            last_opened_at = datetime('now'),
                            last_opened_by = excluded.last_opened_by,
                            updated_at = datetime('now')";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", model.ModelGuid);
                        cmd.Parameters.AddWithValue("@modelName", model.ModelName ?? "Unknown Model");
                        cmd.Parameters.AddWithValue("@centralPath", model.CentralModelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@projectName", model.ProjectName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@cloudProjectId", model.CloudProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@zemanageProjectId", model.ZemanageProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@registeredBy", model.RegisteredBy ?? "System");
                        cmd.Parameters.AddWithValue("@modelType", model.ModelType ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@isLocalCopy", model.IsLocalCopy ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isWorkshared", model.IsWorkshared ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isFamily", model.IsFamily ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isCloudModel", model.IsCloudModel ? 1 : 0);
                        cmd.Parameters.AddWithValue("@notes", model.Notes ?? (object)DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Registered/updated model: {model.ModelName} ({model.ModelGuid})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register/update model: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Find existing model by name and central path (for duplicate prevention)
        /// Returns the existing model_guid if found, null otherwise
        /// </summary>
        public async Task<string> FindExistingModelGuidAsync(string modelName, string centralModelPath)
        {
            if (string.IsNullOrEmpty(modelName))
                return null;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Strategy 1: Match by central_model_path if available (most reliable for workshared)
                    if (!string.IsNullOrEmpty(centralModelPath))
                    {
                        var sqlByPath = @"
                            SELECT model_guid
                            FROM registered_models
                            WHERE central_model_path = @centralPath
                              AND is_active = 1
                            LIMIT 1";

                        using (var cmd = new SQLiteCommand(sqlByPath, connection))
                        {
                            cmd.Parameters.AddWithValue("@centralPath", centralModelPath);
                            var result = await cmd.ExecuteScalarAsync();
                            if (result != null)
                            {
                                var existingGuid = result.ToString();
                                _logger?.LogInfo($"Found existing model by central path: {existingGuid}");
                                return existingGuid;
                            }
                        }
                    }

                    // Strategy 2: Match by model name for workshared models in same folder
                    // This handles the case where central_model_path was null on first registration
                    var sqlByName = @"
                        SELECT model_guid, central_model_path
                        FROM registered_models
                        WHERE model_name = @modelName
                          AND is_workshared = 1
                          AND is_active = 1
                        ORDER BY registered_at DESC
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(sqlByName, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var existingGuid = reader.GetString(0);
                                var existingPath = reader.IsDBNull(1) ? null : reader.GetString(1);

                                // If existing record has no central path but current has one, update it
                                if (string.IsNullOrEmpty(existingPath) && !string.IsNullOrEmpty(centralModelPath))
                                {
                                    await UpdateCentralModelPathAsync(existingGuid, centralModelPath);
                                }

                                _logger?.LogInfo($"Found existing model by name '{modelName}': {existingGuid}");
                                return existingGuid;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to find existing model: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Update central model path for an existing registration
        /// Used when first registration had empty path (detached) but subsequent open has path
        /// </summary>
        public async Task UpdateCentralModelPathAsync(string modelGuid, string centralModelPath)
        {
            if (string.IsNullOrEmpty(modelGuid) || string.IsNullOrEmpty(centralModelPath))
                return;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE registered_models
                        SET central_model_path = @centralPath,
                            updated_at = datetime('now')
                        WHERE model_guid = @modelGuid
                          AND (central_model_path IS NULL OR central_model_path = '')";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                        cmd.Parameters.AddWithValue("@centralPath", centralModelPath);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        if (rows > 0)
                        {
                            _logger?.LogInfo($"Updated central_model_path for {modelGuid}: {centralModelPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update central model path: {ex.Message}");
            }
        }

        /// <summary>
        /// Update last opened timestamp
        /// </summary>
        public async Task UpdateLastOpenedAsync(string modelGuid, string openedBy)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE registered_models
                        SET last_opened_at = @openedAt,
                            last_opened_by = @openedBy,
                            updated_at = datetime('now')
                        WHERE model_guid = @modelGuid";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                        cmd.Parameters.AddWithValue("@openedAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@openedBy", openedBy ?? "Unknown");

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update last opened: {ex.Message}");
            }
        }

        /// <summary>
        /// Update an existing registered model by model_guid
        /// </summary>
        public async Task<bool> UpdateModelAsync(RegisteredModel model)
        {
            if (model == null || string.IsNullOrEmpty(model.ModelGuid))
                return false;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        UPDATE registered_models SET
                            model_name = @modelName,
                            central_model_path = @centralPath,
                            project_name = @projectName,
                            zemanage_project_id = @zemanageProjectId,
                            is_active = @isActive,
                            registered_by = @registeredBy,
                            is_workshared = @isWorkshared,
                            is_family = @isFamily,
                            is_cloudmodel = @isCloudModel,
                            notes = @notes,
                            last_opened_at = @lastOpenedAt,
                            last_opened_by = @lastOpenedBy
                        WHERE model_guid = @modelGuid";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@modelGuid", model.ModelGuid);
                        cmd.Parameters.AddWithValue("@modelName", model.ModelName ?? "Unknown Model");
                        cmd.Parameters.AddWithValue("@centralPath", model.CentralModelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@projectName", model.ProjectName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@zemanageProjectId", model.ZemanageProjectId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@isActive", model.IsActive ? 1 : 0);
                        cmd.Parameters.AddWithValue("@registeredBy", model.RegisteredBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@isWorkshared", model.IsWorkshared ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isFamily", model.IsFamily ? 1 : 0);
                        cmd.Parameters.AddWithValue("@isCloudModel", model.IsCloudModel ? 1 : 0);
                        cmd.Parameters.AddWithValue("@notes", model.Notes ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@lastOpenedAt", model.LastOpenedAt.HasValue ? (object)model.LastOpenedAt.Value.ToString("o") : DBNull.Value);
                        cmd.Parameters.AddWithValue("@lastOpenedBy", model.LastOpenedBy ?? (object)DBNull.Value);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        if (rows > 0)
                        {
                            _logger?.LogInfo($"Updated model: {model.ModelName} ({model.ModelGuid})");
                            return true;
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update model {model.ModelGuid}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get all active registered models
        /// </summary>
        public async Task<List<RegisteredModel>> GetActiveModelsAsync()
        {
            var models = new List<RegisteredModel>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT id, model_guid, model_name, central_model_path, project_name,
                               cloud_project_id, zemanage_project_id, is_active, registered_at, registered_by,
                               is_local_copy, is_workshared, is_family, is_cloudmodel,
                               model_type, notes, last_opened_at, last_opened_by
                        FROM registered_models
                        WHERE is_active = 1
                        ORDER BY model_name";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            models.Add(new RegisteredModel
                            {
                                Id = reader.GetInt32(0),
                                ModelGuid = reader.GetString(1),
                                ModelName = reader.GetString(2),
                                CentralModelPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                                ProjectName = reader.IsDBNull(4) ? null : reader.GetString(4),
                                CloudProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
                                ZemanageProjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
                                IsActive = reader.GetInt32(7) == 1,
                                RegisteredAt = DateTime.Parse(reader.GetString(8)),
                                RegisteredBy = reader.GetString(9),
                                IsLocalCopy = reader.GetInt32(10) == 1,
                                IsWorkshared = reader.GetInt32(11) == 1,
                                IsFamily = reader.GetInt32(12) == 1,
                                IsCloudModel = reader.GetInt32(13) == 1,
                                ModelType = reader.IsDBNull(14) ? null : reader.GetString(14),
                                Notes = reader.IsDBNull(15) ? null : reader.GetString(15),
                                LastOpenedAt = reader.IsDBNull(16) ? (DateTime?)null : DateTime.Parse(reader.GetString(16)),
                                LastOpenedBy = reader.IsDBNull(17) ? null : reader.GetString(17)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active models: {ex.Message}", ex);
            }

            return models;
        }

        /// <summary>
        /// Get auto-registration setting from app_settings
        /// </summary>
        public async Task<bool> GetAutoRegisterSettingAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = "SELECT auto_register_models FROM app_settings WHERE id = 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        return result != null && Convert.ToInt32(result) == 1;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get auto-register setting: {ex.Message}", ex);
                return true; // Default to true for backward compatibility
            }
        }

        /// <summary>
        /// Look up model GUID by central model path. Used for pre-open duplicate session check.
        /// Returns null if model is not registered on this machine.
        /// </summary>
        public async Task<string?> GetModelGuidByPathAsync(string centralModelPath)
        {
            if (string.IsNullOrEmpty(centralModelPath)) return null;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"SELECT model_guid FROM registered_models
                            WHERE (central_model_path = @path OR central_model_path = @pathAlt)
                              AND is_active = 1
                            LIMIT 1";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@path", centralModelPath);
                cmd.Parameters.AddWithValue("@pathAlt", centralModelPath.Replace("/", "\\"));

                var result = await cmd.ExecuteScalarAsync();
                return result == DBNull.Value || result == null ? null : result.ToString();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get model GUID by path: {ex.Message}", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Model registration data class
    /// </summary>
    public class RegisteredModel
    {
        public int Id { get; set; }
        public string ModelGuid { get; set; }
        public string ModelName { get; set; }
        public string CentralModelPath { get; set; }
        public string ProjectName { get; set; }
        public string CloudProjectId { get; set; }
        public string ZemanageProjectId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public string RegisteredBy { get; set; }

        // Model characteristics
        public bool IsLocalCopy { get; set; } = false;
        public bool IsWorkshared { get; set; } = false;
        public bool IsFamily { get; set; } = false;
        public bool IsCloudModel { get; set; } = false;

        public string ModelType { get; set; }
        public string Notes { get; set; }
        public DateTime? LastOpenedAt { get; set; }
        public string LastOpenedBy { get; set; }
    }
}
