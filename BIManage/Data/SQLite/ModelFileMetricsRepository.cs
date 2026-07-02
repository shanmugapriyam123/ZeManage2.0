using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Core.Metrics;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for model file metrics tracking (columnar format)
    /// Supports three performance-tiered capture types:
    /// 1. Sync/Save - Fast metrics (17 columns) captured automatically
    /// 2. Periodic - Medium metrics (10 columns) captured daily or manually
    /// 3. Manual - Expensive metrics (2 columns) triggered by user
    /// </summary>
    public class ModelFileMetricsRepository
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;

        public ModelFileMetricsRepository(string databasePath, ILogger? logger = null)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;

            // Validate database connection
            try
            {
                // Bootstrap kicks SchemaMigration on a background thread and constructs
                // this repo on the main thread in the same call (RegisterPersistence).
                // On a fresh install the model_file_metrics_* tables haven't been created
                // yet at this moment, so the table-existence check below would throw
                // "schema may not be initialized to v11" and abort the add-in.
                // WaitForReady blocks until the background migration finishes (or no-ops
                // if it already did) — bounded by a 30s timeout so a hung migration
                // doesn't permanently stall Revit startup.
                SchemaMigration.WaitForReady(30000);

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();

                    // Verify at least one metrics table exists
                    var checkTable = @"
                        SELECT COUNT(*) FROM sqlite_master
                        WHERE type='table' AND name LIKE 'model_file_metrics_%'";

                    using (var cmd = new SQLiteCommand(checkTable, connection))
                    {
                        var tableCount = Convert.ToInt32(cmd.ExecuteScalar());
                        if (tableCount == 0)
                        {
                            throw new InvalidOperationException(
                                "Model file metrics tables not found - database schema may not be initialized to v3");
                        }
                    }

                    _logger?.LogInfo("ModelFileMetricsRepository initialized successfully");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initialize ModelFileMetricsRepository: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Capture types for metric collection
        /// </summary>
        public enum CaptureType
        {
            SyncSave,  // Fast metrics on sync/save
            Periodic,  // Daily snapshots
            Manual     // User-triggered expensive metrics
        }

        /// <summary>
        /// Insert fast metrics (sync/save capture)
        /// </summary>
        public async Task<string> InsertFastMetricsAsync(
            string sessionId,
            string documentId,
            string modelGuid,
            string modelPath,
            string modelName,
            string capturedBy,
            FastMetrics metrics,
            string captureTypeValue = "save",  // 'sync' or 'save'
            string syncGuid = null)            // Link to model_sync table if sync operation
        {
            try
            {
                var captureId = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow.ToString("o");

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO model_file_metrics_sync_save (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            capture_type, sync_guid, captured_at, captured_by,
                            file_size_bytes, levels_count, grids_count, design_options_count,
                            linked_dwg_count, imported_dwg_count, linked_revit_count, raster_images_count,
                            warnings_count, duplicate_elements_count, model_groups_count, detail_groups_count,
                            total_views_count, sheets_count, total_families_count, total_worksets_count,
                            non_native_object_styles_count, view_templates_count,
                            shared_coord_ns, shared_coord_ew, shared_coord_elevation, shared_coord_unit,
                            created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @captureType, @syncGuid, @capturedAt, @capturedBy,
                            @fileSizeBytes, @levelsCount, @gridsCount, @designOptionsCount,
                            @linkedDwgCount, @importedDwgCount, @linkedRevitCount, @rasterImagesCount,
                            @warningsCount, @duplicateElementsCount, @modelGroupsCount, @detailGroupsCount,
                            @totalViewsCount, @sheetsCount, @totalFamiliesCount, @totalWorksetsCount,
                            @nonNativeObjectStylesCount, @viewTemplatesCount,
                            @sharedCoordNs, @sharedCoordEw, @sharedCoordElevation, @sharedCoordUnit,
                            @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@documentId", documentId);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@captureType", captureTypeValue);
                        cmd.Parameters.AddWithValue("@syncGuid", syncGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedAt", now);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy);
                        cmd.Parameters.AddWithValue("@fileSizeBytes", metrics.FileSizeBytes ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@levelsCount", metrics.LevelsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@gridsCount", metrics.GridsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@designOptionsCount", metrics.DesignOptionsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@linkedDwgCount", metrics.LinkedDwgCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@importedDwgCount", metrics.ImportedDwgCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@linkedRevitCount", metrics.LinkedRevitCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@rasterImagesCount", metrics.RasterImagesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@warningsCount", metrics.WarningsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@duplicateElementsCount", metrics.DuplicateElementsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelGroupsCount", metrics.ModelGroupsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@detailGroupsCount", metrics.DetailGroupsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@totalViewsCount", metrics.TotalViewsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sheetsCount", metrics.SheetsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@totalFamiliesCount", metrics.TotalFamiliesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@totalWorksetsCount", metrics.TotalWorksetsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@nonNativeObjectStylesCount", metrics.NonNativeObjectStylesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@viewTemplatesCount", metrics.ViewTemplatesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordNs", metrics.SharedCoordNs ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordEw", metrics.SharedCoordEw ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordElevation", metrics.SharedCoordElevation ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordUnit", metrics.SharedCoordUnit ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdAt", now);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Captured fast metrics for {modelName} (capture_id: {captureId}, type: {captureTypeValue})");
                return captureId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert fast metrics: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Insert medium metrics (periodic capture)
        /// </summary>
        public async Task<string> InsertMediumMetricsAsync(
            string sessionId,
            string documentId,
            string modelGuid,
            string modelPath,
            string modelName,
            string capturedBy,
            MediumMetrics metrics,
            bool isManualTrigger = false,
            int captureIntervalHours = 24)
        {
            try
            {
                var captureId = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow.ToString("o");

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO model_file_metrics_periodic (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            captured_at, captured_by, capture_interval_hours, is_manual_trigger,
                            total_elements_count, model_elements_count, annotative_elements_count,
                            inplace_families_count, unplaced_rooms_count, views_not_on_sheets_count,
                            unenclosed_rooms_count, walls_not_connected_count, pipes_not_connected_count,
                            ducts_not_connected_count,
                            created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @capturedAt, @capturedBy, @captureIntervalHours, @isManualTrigger,
                            @totalElementsCount, @modelElementsCount, @annotativeElementsCount,
                            @inplaceFamiliesCount, @unplacedRoomsCount, @viewsNotOnSheetsCount,
                            @unenclosedRoomsCount, @wallsNotConnectedCount, @pipesNotConnectedCount,
                            @ductsNotConnectedCount,
                            @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@documentId", documentId);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@capturedAt", now);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy);
                        cmd.Parameters.AddWithValue("@captureIntervalHours", captureIntervalHours);
                        cmd.Parameters.AddWithValue("@isManualTrigger", isManualTrigger ? 1 : 0);
                        cmd.Parameters.AddWithValue("@totalElementsCount", metrics.TotalElementsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelElementsCount", metrics.ModelElementsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@annotativeElementsCount", metrics.AnnotativeElementsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@inplaceFamiliesCount", metrics.InplaceFamiliesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@unplacedRoomsCount", metrics.UnplacedRoomsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@viewsNotOnSheetsCount", metrics.ViewsNotOnSheetsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@unenclosedRoomsCount", metrics.UnenclosedRoomsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@wallsNotConnectedCount", metrics.WallsNotConnectedCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@pipesNotConnectedCount", metrics.PipesNotConnectedCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@ductsNotConnectedCount", metrics.DuctsNotConnectedCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdAt", now);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Captured medium metrics for {modelName} (capture_id: {captureId}, manual: {isManualTrigger})");
                return captureId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert medium metrics: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Insert expensive metrics (manual capture)
        /// </summary>
        public async Task<string> InsertExpensiveMetricsAsync(
            string sessionId,
            string documentId,
            string modelGuid,
            string modelPath,
            string modelName,
            string capturedBy,
            ExpensiveMetrics metrics,
            string commandSource = "ribbon",
            string captureReason = null)
        {
            try
            {
                var captureId = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow.ToString("o");

                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        INSERT INTO model_file_metrics_manual (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            captured_at, captured_by, command_source, capture_reason,
                            families_over_5mb_count, purgeable_elements_count,
                            created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @capturedAt, @capturedBy, @commandSource, @captureReason,
                            @familiesOver5MbCount, @purgeableElementsCount,
                            @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId);
                        cmd.Parameters.AddWithValue("@documentId", documentId);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@capturedAt", now);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy);
                        cmd.Parameters.AddWithValue("@commandSource", commandSource ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@captureReason", captureReason ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@familiesOver5MbCount", metrics.FamiliesOver5MbCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@purgeableElementsCount", metrics.PurgeableElementsCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdAt", now);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo($"Captured expensive metrics for {modelName} (capture_id: {captureId})");
                return captureId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert expensive metrics: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Updates the families_over_5mb_count for an existing expensive metrics record.
        /// Called after the optional family size scan completes.
        /// </summary>
        public async Task UpdateExpensiveMetricsFamilyCountAsync(string captureId, int familyCount)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"UPDATE model_file_metrics_manual
                                SET families_over_5mb_count = @count
                                WHERE capture_id = @captureId";
                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@count", familyCount);
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update family count for {captureId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get latest sync/save metrics for a model
        /// </summary>
        public async Task<SyncSaveMetricsRecord?> GetLatestSyncSaveMetricsAsync(string modelGuid = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var whereClause = string.IsNullOrEmpty(modelGuid) ? "" : "WHERE model_guid = @modelGuid";
                    var sql = $@"
                        SELECT * FROM model_file_metrics_sync_save
                        {whereClause}
                        ORDER BY captured_at DESC
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        if (!string.IsNullOrEmpty(modelGuid))
                            cmd.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new SyncSaveMetricsRecord
                                {
                                    CaptureId = reader["capture_id"]?.ToString(),
                                    ModelName = reader["model_name"]?.ToString(),
                                    ModelGuid = reader["model_guid"]?.ToString(),
                                    ModelPath = reader["model_path"]?.ToString(),
                                    CaptureType = reader["capture_type"]?.ToString(),
                                    CapturedAt = reader["captured_at"]?.ToString(),
                                    CapturedBy = reader["captured_by"]?.ToString(),
                                    FileSizeBytes = reader["file_size_bytes"] != DBNull.Value ? Convert.ToInt64(reader["file_size_bytes"]) : null,
                                    LevelsCount = reader["levels_count"] != DBNull.Value ? Convert.ToInt32(reader["levels_count"]) : null,
                                    GridsCount = reader["grids_count"] != DBNull.Value ? Convert.ToInt32(reader["grids_count"]) : null,
                                    DesignOptionsCount = reader["design_options_count"] != DBNull.Value ? Convert.ToInt32(reader["design_options_count"]) : null,
                                    LinkedDwgCount = reader["linked_dwg_count"] != DBNull.Value ? Convert.ToInt32(reader["linked_dwg_count"]) : null,
                                    ImportedDwgCount = reader["imported_dwg_count"] != DBNull.Value ? Convert.ToInt32(reader["imported_dwg_count"]) : null,
                                    LinkedRevitCount = reader["linked_revit_count"] != DBNull.Value ? Convert.ToInt32(reader["linked_revit_count"]) : null,
                                    RasterImagesCount = reader["raster_images_count"] != DBNull.Value ? Convert.ToInt32(reader["raster_images_count"]) : null,
                                    WarningsCount = reader["warnings_count"] != DBNull.Value ? Convert.ToInt32(reader["warnings_count"]) : null,
                                    DuplicateElementsCount = reader["duplicate_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["duplicate_elements_count"]) : null,
                                    ModelGroupsCount = reader["model_groups_count"] != DBNull.Value ? Convert.ToInt32(reader["model_groups_count"]) : null,
                                    DetailGroupsCount = reader["detail_groups_count"] != DBNull.Value ? Convert.ToInt32(reader["detail_groups_count"]) : null,
                                    TotalViewsCount = reader["total_views_count"] != DBNull.Value ? Convert.ToInt32(reader["total_views_count"]) : null,
                                    SheetsCount = reader["sheets_count"] != DBNull.Value ? Convert.ToInt32(reader["sheets_count"]) : null,
                                    TotalFamiliesCount = reader["total_families_count"] != DBNull.Value ? Convert.ToInt32(reader["total_families_count"]) : null,
                                    TotalWorksetsCount = reader["total_worksets_count"] != DBNull.Value ? Convert.ToInt32(reader["total_worksets_count"]) : null,
                                    NonNativeObjectStylesCount = reader["non_native_object_styles_count"] != DBNull.Value ? Convert.ToInt32(reader["non_native_object_styles_count"]) : null,
                                    ViewTemplatesCount = reader["view_templates_count"] != DBNull.Value ? Convert.ToInt32(reader["view_templates_count"]) : null
                                };
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get latest sync/save metrics: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get latest periodic metrics for a model
        /// </summary>
        public async Task<PeriodicMetricsRecord?> GetLatestPeriodicMetricsAsync(string modelGuid = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var whereClause = string.IsNullOrEmpty(modelGuid) ? "" : "WHERE model_guid = @modelGuid";
                    var sql = $@"
                        SELECT * FROM model_file_metrics_periodic
                        {whereClause}
                        ORDER BY captured_at DESC
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        if (!string.IsNullOrEmpty(modelGuid))
                            cmd.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new PeriodicMetricsRecord
                                {
                                    CaptureId = reader["capture_id"]?.ToString(),
                                    ModelName = reader["model_name"]?.ToString(),
                                    ModelGuid = reader["model_guid"]?.ToString(),
                                    ModelPath = reader["model_path"]?.ToString(),
                                    CapturedAt = reader["captured_at"]?.ToString(),
                                    CapturedBy = reader["captured_by"]?.ToString(),
                                    TotalElementsCount = reader["total_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["total_elements_count"]) : null,
                                    ModelElementsCount = reader["model_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["model_elements_count"]) : null,
                                    AnnotativeElementsCount = reader["annotative_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["annotative_elements_count"]) : null,
                                    InplaceFamiliesCount = reader["inplace_families_count"] != DBNull.Value ? Convert.ToInt32(reader["inplace_families_count"]) : null,
                                    UnplacedRoomsCount = reader["unplaced_rooms_count"] != DBNull.Value ? Convert.ToInt32(reader["unplaced_rooms_count"]) : null,
                                    ViewsNotOnSheetsCount = reader["views_not_on_sheets_count"] != DBNull.Value ? Convert.ToInt32(reader["views_not_on_sheets_count"]) : null,
                                    UnenclosedRoomsCount = reader["unenclosed_rooms_count"] != DBNull.Value ? Convert.ToInt32(reader["unenclosed_rooms_count"]) : null,
                                    WallsNotConnectedCount = reader["walls_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["walls_not_connected_count"]) : null,
                                    PipesNotConnectedCount = reader["pipes_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["pipes_not_connected_count"]) : null,
                                    DuctsNotConnectedCount = reader["ducts_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["ducts_not_connected_count"]) : null
                                };
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get latest periodic metrics: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get latest manual metrics for a model
        /// </summary>
        public async Task<ManualMetricsRecord?> GetLatestManualMetricsAsync(string modelGuid = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var whereClause = string.IsNullOrEmpty(modelGuid) ? "" : "WHERE model_guid = @modelGuid";
                    var sql = $@"
                        SELECT * FROM model_file_metrics_manual
                        {whereClause}
                        ORDER BY captured_at DESC
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        if (!string.IsNullOrEmpty(modelGuid))
                            cmd.Parameters.AddWithValue("@modelGuid", modelGuid);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return new ManualMetricsRecord
                                {
                                    CaptureId = reader["capture_id"]?.ToString(),
                                    ModelName = reader["model_name"]?.ToString(),
                                    ModelGuid = reader["model_guid"]?.ToString(),
                                    ModelPath = reader["model_path"]?.ToString(),
                                    CapturedAt = reader["captured_at"]?.ToString(),
                                    CapturedBy = reader["captured_by"]?.ToString(),
                                    FamiliesOver5MbCount = reader["families_over_5mb_count"] != DBNull.Value ? Convert.ToInt32(reader["families_over_5mb_count"]) : null,
                                    PurgeableElementsCount = reader["purgeable_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["purgeable_elements_count"]) : null
                                };
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get latest manual metrics: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get metrics count for dashboard summary
        /// </summary>
        public async Task<MetricsSummary> GetMetricsSummaryAsync()
        {
            var summary = new MetricsSummary();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Get counts from each table
                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM model_file_metrics_sync_save", connection))
                        summary.SyncSaveCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM model_file_metrics_periodic", connection))
                        summary.PeriodicCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM model_file_metrics_manual", connection))
                        summary.ManualCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                    // Get unique models count
                    using (var cmd = new SQLiteCommand(@"
                        SELECT COUNT(DISTINCT model_guid) FROM (
                            SELECT model_guid FROM model_file_metrics_sync_save WHERE model_guid IS NOT NULL
                            UNION
                            SELECT model_guid FROM model_file_metrics_periodic WHERE model_guid IS NOT NULL
                            UNION
                            SELECT model_guid FROM model_file_metrics_manual WHERE model_guid IS NOT NULL
                        )", connection))
                        summary.UniqueModelsCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get metrics summary: {ex.Message}", ex);
            }
            return summary;
        }

        /// <summary>
        /// Get latest sync/save metrics grouped by model (one record per unique model)
        /// </summary>
        public async Task<List<SyncSaveMetricsRecord>> GetAllModelsLatestSyncSaveAsync()
        {
            var results = new List<SyncSaveMetricsRecord>();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT s.* FROM model_file_metrics_sync_save s
                        INNER JOIN (
                            SELECT model_guid, MAX(captured_at) as max_captured_at
                            FROM model_file_metrics_sync_save
                            WHERE model_guid IS NOT NULL AND model_guid != ''
                            GROUP BY model_guid
                        ) latest ON s.model_guid = latest.model_guid AND s.captured_at = latest.max_captured_at
                        ORDER BY s.model_name";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(new SyncSaveMetricsRecord
                            {
                                CaptureId = reader["capture_id"]?.ToString(),
                                ModelName = reader["model_name"]?.ToString(),
                                ModelGuid = reader["model_guid"]?.ToString(),
                                ModelPath = reader["model_path"]?.ToString(),
                                CaptureType = reader["capture_type"]?.ToString(),
                                CapturedAt = reader["captured_at"]?.ToString(),
                                CapturedBy = reader["captured_by"]?.ToString(),
                                FileSizeBytes = reader["file_size_bytes"] != DBNull.Value ? Convert.ToInt64(reader["file_size_bytes"]) : null,
                                LevelsCount = reader["levels_count"] != DBNull.Value ? Convert.ToInt32(reader["levels_count"]) : null,
                                GridsCount = reader["grids_count"] != DBNull.Value ? Convert.ToInt32(reader["grids_count"]) : null,
                                DesignOptionsCount = reader["design_options_count"] != DBNull.Value ? Convert.ToInt32(reader["design_options_count"]) : null,
                                LinkedDwgCount = reader["linked_dwg_count"] != DBNull.Value ? Convert.ToInt32(reader["linked_dwg_count"]) : null,
                                ImportedDwgCount = reader["imported_dwg_count"] != DBNull.Value ? Convert.ToInt32(reader["imported_dwg_count"]) : null,
                                LinkedRevitCount = reader["linked_revit_count"] != DBNull.Value ? Convert.ToInt32(reader["linked_revit_count"]) : null,
                                RasterImagesCount = reader["raster_images_count"] != DBNull.Value ? Convert.ToInt32(reader["raster_images_count"]) : null,
                                WarningsCount = reader["warnings_count"] != DBNull.Value ? Convert.ToInt32(reader["warnings_count"]) : null,
                                DuplicateElementsCount = reader["duplicate_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["duplicate_elements_count"]) : null,
                                ModelGroupsCount = reader["model_groups_count"] != DBNull.Value ? Convert.ToInt32(reader["model_groups_count"]) : null,
                                DetailGroupsCount = reader["detail_groups_count"] != DBNull.Value ? Convert.ToInt32(reader["detail_groups_count"]) : null,
                                TotalViewsCount = reader["total_views_count"] != DBNull.Value ? Convert.ToInt32(reader["total_views_count"]) : null,
                                SheetsCount = reader["sheets_count"] != DBNull.Value ? Convert.ToInt32(reader["sheets_count"]) : null,
                                TotalFamiliesCount = reader["total_families_count"] != DBNull.Value ? Convert.ToInt32(reader["total_families_count"]) : null,
                                TotalWorksetsCount = reader["total_worksets_count"] != DBNull.Value ? Convert.ToInt32(reader["total_worksets_count"]) : null,
                                NonNativeObjectStylesCount = reader["non_native_object_styles_count"] != DBNull.Value ? Convert.ToInt32(reader["non_native_object_styles_count"]) : null,
                                ViewTemplatesCount = reader["view_templates_count"] != DBNull.Value ? Convert.ToInt32(reader["view_templates_count"]) : null,
                                SharedCoordNs = reader["shared_coord_ns"] != DBNull.Value ? Convert.ToDouble(reader["shared_coord_ns"]) : null,
                                SharedCoordEw = reader["shared_coord_ew"] != DBNull.Value ? Convert.ToDouble(reader["shared_coord_ew"]) : null,
                                SharedCoordElevation = reader["shared_coord_elevation"] != DBNull.Value ? Convert.ToDouble(reader["shared_coord_elevation"]) : null,
                                SharedCoordUnit = reader["shared_coord_unit"] != DBNull.Value ? reader["shared_coord_unit"].ToString() : "ft"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get all models latest sync/save: {ex.Message}", ex);
            }
            return results;
        }

        /// <summary>
        /// Get latest periodic metrics grouped by model (one record per unique model)
        /// </summary>
        public async Task<List<PeriodicMetricsRecord>> GetAllModelsLatestPeriodicAsync()
        {
            var results = new List<PeriodicMetricsRecord>();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT p.* FROM model_file_metrics_periodic p
                        INNER JOIN (
                            SELECT model_guid, MAX(captured_at) as max_captured_at
                            FROM model_file_metrics_periodic
                            WHERE model_guid IS NOT NULL AND model_guid != ''
                            GROUP BY model_guid
                        ) latest ON p.model_guid = latest.model_guid AND p.captured_at = latest.max_captured_at
                        ORDER BY p.model_name";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(new PeriodicMetricsRecord
                            {
                                CaptureId = reader["capture_id"]?.ToString(),
                                ModelName = reader["model_name"]?.ToString(),
                                ModelGuid = reader["model_guid"]?.ToString(),
                                ModelPath = reader["model_path"]?.ToString(),
                                CapturedAt = reader["captured_at"]?.ToString(),
                                CapturedBy = reader["captured_by"]?.ToString(),
                                TotalElementsCount = reader["total_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["total_elements_count"]) : null,
                                ModelElementsCount = reader["model_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["model_elements_count"]) : null,
                                AnnotativeElementsCount = reader["annotative_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["annotative_elements_count"]) : null,
                                InplaceFamiliesCount = reader["inplace_families_count"] != DBNull.Value ? Convert.ToInt32(reader["inplace_families_count"]) : null,
                                UnplacedRoomsCount = reader["unplaced_rooms_count"] != DBNull.Value ? Convert.ToInt32(reader["unplaced_rooms_count"]) : null,
                                ViewsNotOnSheetsCount = reader["views_not_on_sheets_count"] != DBNull.Value ? Convert.ToInt32(reader["views_not_on_sheets_count"]) : null,
                                UnenclosedRoomsCount = reader["unenclosed_rooms_count"] != DBNull.Value ? Convert.ToInt32(reader["unenclosed_rooms_count"]) : null,
                                WallsNotConnectedCount = reader["walls_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["walls_not_connected_count"]) : null,
                                PipesNotConnectedCount = reader["pipes_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["pipes_not_connected_count"]) : null,
                                DuctsNotConnectedCount = reader["ducts_not_connected_count"] != DBNull.Value ? Convert.ToInt32(reader["ducts_not_connected_count"]) : null
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get all models latest periodic: {ex.Message}", ex);
            }
            return results;
        }

        /// <summary>
        /// Get latest manual metrics grouped by model (one record per unique model)
        /// </summary>
        public async Task<List<ManualMetricsRecord>> GetAllModelsLatestManualAsync()
        {
            var results = new List<ManualMetricsRecord>();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var sql = @"
                        SELECT m.* FROM model_file_metrics_manual m
                        INNER JOIN (
                            SELECT model_guid, MAX(captured_at) as max_captured_at
                            FROM model_file_metrics_manual
                            WHERE model_guid IS NOT NULL AND model_guid != ''
                            GROUP BY model_guid
                        ) latest ON m.model_guid = latest.model_guid AND m.captured_at = latest.max_captured_at
                        ORDER BY m.model_name";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            results.Add(new ManualMetricsRecord
                            {
                                CaptureId = reader["capture_id"]?.ToString(),
                                ModelName = reader["model_name"]?.ToString(),
                                ModelGuid = reader["model_guid"]?.ToString(),
                                ModelPath = reader["model_path"]?.ToString(),
                                CapturedAt = reader["captured_at"]?.ToString(),
                                CapturedBy = reader["captured_by"]?.ToString(),
                                FamiliesOver5MbCount = reader["families_over_5mb_count"] != DBNull.Value ? Convert.ToInt32(reader["families_over_5mb_count"]) : null,
                                PurgeableElementsCount = reader["purgeable_elements_count"] != DBNull.Value ? Convert.ToInt32(reader["purgeable_elements_count"]) : null
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get all models latest manual: {ex.Message}", ex);
            }
            return results;
        }

        /// <summary>
        /// Checks if a capture already exists in the specified metrics table by capture_id.
        /// </summary>
        public async Task<bool> CaptureExistsAsync(string captureId, string tableName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = $"SELECT COUNT(1) FROM {tableName} WHERE capture_id = @captureId";
                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to check capture existence: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Insert sync/save metrics from API response (uses pre-existing capture_id).
        /// </summary>
        public async Task<bool> InsertSyncSaveMetricsFromApiAsync(
            string captureId, string sessionId, string documentId,
            string modelGuid, string modelPath, string modelName,
            string captureType, string syncGuid, string capturedAt, string capturedBy,
            long fileSizeBytes, int levelsCount, int gridsCount, int designOptionsCount,
            int linkedDwgCount, int importedDwgCount, int linkedRevitCount, int rasterImagesCount,
            int warningsCount, int duplicateElementsCount, int modelGroupsCount, int detailGroupsCount,
            int totalViewsCount, int sheetsCount, int totalFamiliesCount, int totalWorksetsCount,
            int? nonNativeObjectStylesCount, int? viewTemplatesCount,
            double? sharedCoordNs, double? sharedCoordEw, double? sharedCoordElevation,
            string createdAt)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        INSERT OR IGNORE INTO model_file_metrics_sync_save (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            capture_type, sync_guid, captured_at, captured_by,
                            file_size_bytes, levels_count, grids_count, design_options_count,
                            linked_dwg_count, imported_dwg_count, linked_revit_count, raster_images_count,
                            warnings_count, duplicate_elements_count, model_groups_count, detail_groups_count,
                            total_views_count, sheets_count, total_families_count, total_worksets_count,
                            non_native_object_styles_count, view_templates_count,
                            shared_coord_ns, shared_coord_ew, shared_coord_elevation, shared_coord_unit,
                            created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @captureType, @syncGuid, @capturedAt, @capturedBy,
                            @fileSizeBytes, @levelsCount, @gridsCount, @designOptionsCount,
                            @linkedDwgCount, @importedDwgCount, @linkedRevitCount, @rasterImagesCount,
                            @warningsCount, @duplicateElementsCount, @modelGroupsCount, @detailGroupsCount,
                            @totalViewsCount, @sheetsCount, @totalFamiliesCount, @totalWorksetsCount,
                            @nonNativeObjectStylesCount, @viewTemplatesCount,
                            @sharedCoordNs, @sharedCoordEw, @sharedCoordElevation, @sharedCoordUnit,
                            @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@documentId", documentId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@captureType", captureType ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@syncGuid", syncGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedAt", capturedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@fileSizeBytes", fileSizeBytes);
                        cmd.Parameters.AddWithValue("@levelsCount", levelsCount);
                        cmd.Parameters.AddWithValue("@gridsCount", gridsCount);
                        cmd.Parameters.AddWithValue("@designOptionsCount", designOptionsCount);
                        cmd.Parameters.AddWithValue("@linkedDwgCount", linkedDwgCount);
                        cmd.Parameters.AddWithValue("@importedDwgCount", importedDwgCount);
                        cmd.Parameters.AddWithValue("@linkedRevitCount", linkedRevitCount);
                        cmd.Parameters.AddWithValue("@rasterImagesCount", rasterImagesCount);
                        cmd.Parameters.AddWithValue("@warningsCount", warningsCount);
                        cmd.Parameters.AddWithValue("@duplicateElementsCount", duplicateElementsCount);
                        cmd.Parameters.AddWithValue("@modelGroupsCount", modelGroupsCount);
                        cmd.Parameters.AddWithValue("@detailGroupsCount", detailGroupsCount);
                        cmd.Parameters.AddWithValue("@totalViewsCount", totalViewsCount);
                        cmd.Parameters.AddWithValue("@sheetsCount", sheetsCount);
                        cmd.Parameters.AddWithValue("@totalFamiliesCount", totalFamiliesCount);
                        cmd.Parameters.AddWithValue("@totalWorksetsCount", totalWorksetsCount);
                        cmd.Parameters.AddWithValue("@nonNativeObjectStylesCount", nonNativeObjectStylesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@viewTemplatesCount", viewTemplatesCount ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordNs", sharedCoordNs ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordEw", sharedCoordEw ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordElevation", sharedCoordElevation ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@sharedCoordUnit", (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdAt", createdAt ?? (object)DBNull.Value);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        return rows > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert sync/save metrics from API: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Insert periodic metrics from API response (uses pre-existing capture_id).
        /// </summary>
        public async Task<bool> InsertPeriodicMetricsFromApiAsync(
            string captureId, string sessionId, string documentId,
            string modelGuid, string modelPath, string modelName,
            string capturedAt, string capturedBy,
            int captureIntervalHours, bool isManualTrigger,
            int totalElementsCount, int modelElementsCount, int annotativeElementsCount,
            int inplaceFamiliesCount, int unplacedRoomsCount, int viewsNotOnSheetsCount,
            int unenclosedRoomsCount, int wallsNotConnectedCount, int pipesNotConnectedCount,
            int ductsNotConnectedCount, string createdAt)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        INSERT OR IGNORE INTO model_file_metrics_periodic (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            captured_at, captured_by, capture_interval_hours, is_manual_trigger,
                            total_elements_count, model_elements_count, annotative_elements_count,
                            inplace_families_count, unplaced_rooms_count, views_not_on_sheets_count,
                            unenclosed_rooms_count, walls_not_connected_count, pipes_not_connected_count,
                            ducts_not_connected_count, created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @capturedAt, @capturedBy, @captureIntervalHours, @isManualTrigger,
                            @totalElementsCount, @modelElementsCount, @annotativeElementsCount,
                            @inplaceFamiliesCount, @unplacedRoomsCount, @viewsNotOnSheetsCount,
                            @unenclosedRoomsCount, @wallsNotConnectedCount, @pipesNotConnectedCount,
                            @ductsNotConnectedCount, @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@documentId", documentId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedAt", capturedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@captureIntervalHours", captureIntervalHours);
                        cmd.Parameters.AddWithValue("@isManualTrigger", isManualTrigger ? 1 : 0);
                        cmd.Parameters.AddWithValue("@totalElementsCount", totalElementsCount);
                        cmd.Parameters.AddWithValue("@modelElementsCount", modelElementsCount);
                        cmd.Parameters.AddWithValue("@annotativeElementsCount", annotativeElementsCount);
                        cmd.Parameters.AddWithValue("@inplaceFamiliesCount", inplaceFamiliesCount);
                        cmd.Parameters.AddWithValue("@unplacedRoomsCount", unplacedRoomsCount);
                        cmd.Parameters.AddWithValue("@viewsNotOnSheetsCount", viewsNotOnSheetsCount);
                        cmd.Parameters.AddWithValue("@unenclosedRoomsCount", unenclosedRoomsCount);
                        cmd.Parameters.AddWithValue("@wallsNotConnectedCount", wallsNotConnectedCount);
                        cmd.Parameters.AddWithValue("@pipesNotConnectedCount", pipesNotConnectedCount);
                        cmd.Parameters.AddWithValue("@ductsNotConnectedCount", ductsNotConnectedCount);
                        cmd.Parameters.AddWithValue("@createdAt", createdAt ?? (object)DBNull.Value);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        return rows > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert periodic metrics from API: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Insert manual metrics from API response (uses pre-existing capture_id).
        /// </summary>
        public async Task<bool> InsertManualMetricsFromApiAsync(
            string captureId, string sessionId, string documentId,
            string modelGuid, string modelPath, string modelName,
            string capturedAt, string capturedBy,
            string commandSource, string captureReason,
            int familiesOver5mbCount, int purgeableElementsCount,
            string createdAt)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var sql = @"
                        INSERT OR IGNORE INTO model_file_metrics_manual (
                            capture_id, session_id, document_id, model_guid, model_path, model_name,
                            captured_at, captured_by, command_source, capture_reason,
                            families_over_5mb_count, purgeable_elements_count, created_at
                        ) VALUES (
                            @captureId, @sessionId, @documentId, @modelGuid, @modelPath, @modelName,
                            @capturedAt, @capturedBy, @commandSource, @captureReason,
                            @familiesOver5mbCount, @purgeableElementsCount, @createdAt
                        )";

                    using (var cmd = new SQLiteCommand(sql, connection))
                    {
                        cmd.Parameters.AddWithValue("@captureId", captureId);
                        cmd.Parameters.AddWithValue("@sessionId", sessionId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@documentId", documentId ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelGuid", modelGuid ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelPath", modelPath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@modelName", modelName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedAt", capturedAt ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@capturedBy", capturedBy ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@commandSource", commandSource ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@captureReason", captureReason ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@familiesOver5mbCount", familiesOver5mbCount);
                        cmd.Parameters.AddWithValue("@purgeableElementsCount", purgeableElementsCount);
                        cmd.Parameters.AddWithValue("@createdAt", createdAt ?? (object)DBNull.Value);

                        var rows = await cmd.ExecuteNonQueryAsync();
                        return rows > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to insert manual metrics from API: {ex.Message}", ex);
                return false;
            }
        }
    }

    #region Record Classes

    public class SyncSaveMetricsRecord
    {
        public string? CaptureId { get; set; }
        public string? ModelName { get; set; }
        public string? ModelGuid { get; set; }
        public string? ModelPath { get; set; }
        public string? CaptureType { get; set; }
        public string? CapturedAt { get; set; }
        public string? CapturedBy { get; set; }
        public long? FileSizeBytes { get; set; }
        public int? LevelsCount { get; set; }
        public int? GridsCount { get; set; }
        public int? DesignOptionsCount { get; set; }
        public int? LinkedDwgCount { get; set; }
        public int? ImportedDwgCount { get; set; }
        public int? LinkedRevitCount { get; set; }
        public int? RasterImagesCount { get; set; }
        public int? WarningsCount { get; set; }
        public int? DuplicateElementsCount { get; set; }
        public int? ModelGroupsCount { get; set; }
        public int? DetailGroupsCount { get; set; }
        public int? TotalViewsCount { get; set; }
        public int? SheetsCount { get; set; }
        public int? TotalFamiliesCount { get; set; }
        public int? TotalWorksetsCount { get; set; }
        public int? NonNativeObjectStylesCount { get; set; }
        public int? ViewTemplatesCount { get; set; }
        public double? SharedCoordNs { get; set; }
        public double? SharedCoordEw { get; set; }
        public double? SharedCoordElevation { get; set; }
        public string? SharedCoordUnit { get; set; }

        public string FileSizeFormatted => FileSizeBytes.HasValue
            ? FileSizeBytes.Value >= 1024 * 1024
                ? $"{FileSizeBytes.Value / (1024.0 * 1024.0):F1} MB"
                : $"{FileSizeBytes.Value / 1024.0:F1} KB"
            : "N/A";
    }

    public class PeriodicMetricsRecord
    {
        public string? CaptureId { get; set; }
        public string? ModelName { get; set; }
        public string? ModelGuid { get; set; }
        public string? ModelPath { get; set; }
        public string? CapturedAt { get; set; }
        public string? CapturedBy { get; set; }
        public int? TotalElementsCount { get; set; }
        public int? ModelElementsCount { get; set; }
        public int? AnnotativeElementsCount { get; set; }
        public int? InplaceFamiliesCount { get; set; }
        public int? UnplacedRoomsCount { get; set; }
        public int? ViewsNotOnSheetsCount { get; set; }
        public int? UnenclosedRoomsCount { get; set; }
        public int? WallsNotConnectedCount { get; set; }
        public int? PipesNotConnectedCount { get; set; }
        public int? DuctsNotConnectedCount { get; set; }
    }

    public class ManualMetricsRecord
    {
        public string? CaptureId { get; set; }
        public string? ModelName { get; set; }
        public string? ModelGuid { get; set; }
        public string? ModelPath { get; set; }
        public string? CapturedAt { get; set; }
        public string? CapturedBy { get; set; }
        public int? FamiliesOver5MbCount { get; set; }
        public int? PurgeableElementsCount { get; set; }
    }

    public class MetricsSummary
    {
        public int SyncSaveCount { get; set; }
        public int PeriodicCount { get; set; }
        public int ManualCount { get; set; }
        public int UniqueModelsCount { get; set; }
        public int TotalCapturesCount => SyncSaveCount + PeriodicCount + ManualCount;
    }

    #endregion
}
