using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Metrics;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs local metrics data to the backend.
    /// Supports three metric types: Manual, Periodic, and SyncSave.
    /// Primary: SignalR two-way communication.
    /// Fallback: HTTP POST to /api/v1/Revit/metrics/*.
    /// Queues to offline_queue if API is unavailable.
    /// </summary>
    public class MetricsSyncService
    {
        private readonly ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ModelFileMetricsRepository? _metricsRepository;
        private readonly ILogger? _logger;
        private SessionSyncService? _sessionSyncService;

        // API endpoints
        private const string ManualEndpoint = "/api/v1/Revit/metrics/manual";
        private const string PeriodicEndpoint = "/api/v1/Revit/metrics/periodic";
        private const string SyncSaveEndpoint = "/api/v1/Revit/metrics/syncsave";
        private const string ModelSyncEndpoint = "/api/v1/Revit/model-syncs";

        // Latest-by-model endpoints (returns single latest record for a specific model)
        private const string SyncSaveLatestByModelEndpoint = "/api/v1/Revit/metrics/syncsave/latest";
        private const string PeriodicLatestByModelEndpoint = "/api/v1/Revit/metrics/periodic/latest";
        private const string ManualLatestByModelEndpoint = "/api/v1/Revit/metrics/manual/latest";

        // SignalR hub methods
        private const string ManualHubMethod = "SendManualMetrics";
        private const string PeriodicHubMethod = "SendPeriodicMetrics";
        private const string SyncSaveHubMethod = "SendSyncSaveMetrics";

        public MetricsSyncService(
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null,
            ModelFileMetricsRepository? metricsRepository = null)
        {
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _metricsRepository = metricsRepository;
            _logger = logger;
            _logger?.LogInfo($"MetricsSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Sets the session sync service for FK validation before posting metrics.
        /// Called after SessionSyncService is registered (created after MetricsSyncService).
        /// </summary>
        public void SetSessionSyncService(SessionSyncService sessionSyncService)
        {
            _sessionSyncService = sessionSyncService;
        }

        /// <summary>
        /// Sync manual metrics capture to backend.
        /// POST /api/v1/Revit/metrics/manual
        /// </summary>
        public async Task<bool> SyncManualMetricsAsync(ManualMetricsApiRequest request)
        {
            return await SyncMetricsAsync(
                ManualEndpoint,
                ManualHubMethod,
                request,
                OfflineApiWrapper.OperationTypes.MetricsManual,
                "manual",
                request.SessionId);
        }

        /// <summary>
        /// Sync periodic metrics snapshot to backend.
        /// POST /api/v1/Revit/metrics/periodic
        /// </summary>
        public async Task<bool> SyncPeriodicMetricsAsync(PeriodicMetricsApiRequest request)
        {
            return await SyncMetricsAsync(
                PeriodicEndpoint,
                PeriodicHubMethod,
                request,
                OfflineApiWrapper.OperationTypes.MetricsPeriodic,
                "periodic",
                request.SessionId);
        }

        /// <summary>
        /// Sync sync/save metrics to backend.
        /// POST /api/v1/Revit/metrics/syncsave
        /// </summary>
        public async Task<bool> SyncSyncSaveMetricsAsync(SyncSaveMetricsApiRequest request)
        {
            return await SyncMetricsAsync(
                SyncSaveEndpoint,
                SyncSaveHubMethod,
                request,
                OfflineApiWrapper.OperationTypes.MetricsSyncSave,
                "syncsave",
                request.SessionId);
        }

        /// <summary>
        /// Sync model sync record to backend.
        /// POST /api/v1/Revit/model-syncs
        /// </summary>
        public async Task<bool> SyncModelSyncAsync(ModelSyncApiRequest request)
        {
            return await SyncMetricsAsync(
                ModelSyncEndpoint,
                null,
                request,
                OfflineApiWrapper.OperationTypes.ModelSync,
                "model-sync",
                request.SessionId);
        }

        private async Task<bool> SyncMetricsAsync(
            string endpoint,
            string? hubMethod,
            object request,
            string operationType,
            string metricsType,
            string? sessionId)
        {
            try
            {
                // Gate on confirmed session — if session hasn't been confirmed on server,
                // posting metrics with that SessionId will cause FK violation (500)
                if (!string.IsNullOrEmpty(sessionId) && _sessionSyncService != null
                    && !_sessionSyncService.IsSessionConfirmedOnServer(sessionId))
                {
                    _logger?.LogWarning($"Skipping {metricsType} sync — session {sessionId} not confirmed on server (would cause FK 500)");
                    return false;
                }

                _logger?.LogInfo($"Syncing {metricsType} metrics");

                // HTTP POST first for guaranteed server-side persistence
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var httpResult = await SyncViaHttpAsync(endpoint, request, operationType, metricsType, sessionId);
                    if (httpResult)
                    {
                        // Also notify via SignalR for real-time updates (fire-and-forget, non-critical)
                        await NotifyViaSignalRAsync(hubMethod, request, metricsType, sessionId);
                        return true;
                    }
                    // HTTP failed — SyncViaHttpAsync already queued if possible, but fall through as safety net
                }

                if (_httpClient != null && !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning($"HTTP skipped - no auth tokens available (register device first)");
                }

                // Queue for later if no authenticated client or HTTP failed
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId ?? Guid.NewGuid().ToString(), endpoint, request, operationType);
                    _logger?.LogInfo($"Queued {metricsType} metrics for later sync");
                    return true;
                }

                _logger?.LogWarning($"No sync transport available for {metricsType} metrics");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Metrics sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends a SignalR notification for real-time updates (fire-and-forget, non-critical).
        /// This does NOT persist data — HTTP POST handles persistence.
        /// </summary>
        private async Task NotifyViaSignalRAsync(string hubMethod, object request, string metricsType, string? sessionId)
        {
            if (string.IsNullOrEmpty(hubMethod)) return;
            if (_signalRService == null || !_signalRService.IsConnected) return;
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.MetricsRequest, request);
                message.SenderSessionId = sessionId;
                await _signalRService.SendAsync(hubMethod, message);
                _logger?.LogDebug($"SignalR notification sent: {metricsType}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR notification failed (non-critical): {ex.Message}");
            }
        }

        private async Task<bool> SyncViaHttpAsync(string endpoint, object request, string operationType, string metricsType, string? sessionId)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogDebug($"Metrics sync request payload:\n{jsonPayload}");

                var response = await _httpClient!.PostAsync(endpoint, request);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Metrics synced via HTTP: {metricsType} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP metrics sync failed: {metricsType} (Status: {response.StatusCode}, Body: {responseBody})");

                    // Queue ALL failed responses for retry (including 4xx like 401 Unauthorized)
                    // The OfflineSyncProcessor can re-authenticate and replay later
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(sessionId ?? Guid.NewGuid().ToString(), endpoint, request, operationType);
                        return true;
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Metrics sync HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId ?? Guid.NewGuid().ToString(), endpoint, request, operationType);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Metrics sync timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId ?? Guid.NewGuid().ToString(), endpoint, request, operationType);
                    return true;
                }
                return false;
            }
        }

        private async Task QueueForOfflineSyncAsync(string sessionId, string endpoint, object request, string operationType)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = "POST",
                    Payload = request,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: sessionId,
                    operationType: operationType,
                    operationData: jsonData,
                    priority: 5); // Medium priority for metrics

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: {operationType}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline metrics operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fetches all manual metrics from the API (GET /api/v1/Revit/metrics/manual).
        /// </summary>
        public async Task<List<ManualMetricsApiRequest>> FetchManualMetricsFromApiAsync()
        {
            return await FetchMetricsFromApiAsync<ManualMetricsApiRequest>(ManualEndpoint, "manual");
        }

        /// <summary>
        /// Fetches all periodic metrics from the API (GET /api/v1/Revit/metrics/periodic).
        /// </summary>
        public async Task<List<PeriodicMetricsApiRequest>> FetchPeriodicMetricsFromApiAsync()
        {
            return await FetchMetricsFromApiAsync<PeriodicMetricsApiRequest>(PeriodicEndpoint, "periodic");
        }

        /// <summary>
        /// Fetches all sync/save metrics from the API (GET /api/v1/Revit/metrics/syncsave).
        /// </summary>
        public async Task<List<SyncSaveMetricsApiRequest>> FetchSyncSaveMetricsFromApiAsync()
        {
            return await FetchMetricsFromApiAsync<SyncSaveMetricsApiRequest>(SyncSaveEndpoint, "syncsave");
        }

        /// <summary>
        /// Generic helper to fetch metrics from an API endpoint.
        /// </summary>
        private async Task<List<T>> FetchMetricsFromApiAsync<T>(string endpoint, string metricsType)
        {
            try
            {
                _logger?.LogInfo($"Fetching {metricsType} metrics from API...");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning($"HTTP client not available or not authenticated for fetching {metricsType} metrics");
                    return new List<T>();
                }

                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch {metricsType} metrics failed: {response.StatusCode} - {responseBody}");
                    return new List<T>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch {metricsType} metrics response length: {json.Length} chars");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<T>? metrics = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        metrics = JsonSerializer.Deserialize<List<T>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        metrics = JsonSerializer.Deserialize<List<T>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        metrics = JsonSerializer.Deserialize<List<T>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<T>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            metrics = new List<T> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize {metricsType} metrics API response: {ex.Message}", ex);
                    return new List<T>();
                }

                if (metrics == null || metrics.Count == 0)
                {
                    _logger?.LogWarning($"No {metricsType} metrics returned from API");
                    return new List<T>();
                }

                _logger?.LogInfo($"Fetched {metrics.Count} {metricsType} metrics from API");
                return metrics;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch {metricsType} metrics from API: {ex.Message}", ex);
                return new List<T>();
            }
        }

        /// <summary>
        /// Fetches latest metrics for a specific model from the API (all 3 types)
        /// and stores new entries into local DB.
        /// Used by Model Health dashboard to ensure data is available for models
        /// opened locally (no sync/save event fired yet).
        /// GET /api/v1/Revit/metrics/{type}/latest/{modelGuid}
        /// </summary>
        public async Task<int> FetchAndStoreLatestMetricsForModelAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid))
            {
                _logger?.LogDebug("No modelGuid provided - skipping latest metrics fetch");
                return 0;
            }

            if (_metricsRepository == null)
            {
                _logger?.LogWarning("MetricsRepository not available - cannot store latest API metrics locally");
                return 0;
            }

            if (_httpClient == null || !_httpClient.IsAuthenticated)
            {
                _logger?.LogDebug("HTTP client not available or not authenticated - skipping latest metrics fetch");
                return 0;
            }

            var totalInserted = 0;

            // 1. Fetch and store latest sync/save metrics for this model
            try
            {
                var endpoint = $"{SyncSaveLatestByModelEndpoint}/{modelGuid}";
                var metrics = await FetchMetricsFromApiAsync<SyncSaveMetricsApiRequest>(endpoint, "syncsave-latest");
                foreach (var m in metrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_sync_save");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertSyncSaveMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CaptureType, m.SyncGuid,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.FileSizeBytes, m.LevelsCount, m.GridsCount, m.DesignOptionsCount,
                        m.LinkedDwgCount, m.ImportedDwgCount, m.LinkedRevitCount, m.RasterImagesCount,
                        m.WarningsCount, m.DuplicateElementsCount, m.ModelGroupsCount, m.DetailGroupsCount,
                        m.TotalViewsCount, m.SheetsCount, m.TotalFamiliesCount, m.TotalWorksetsCount ?? 0,
                        m.NonNativeObjectStylesCount, m.ViewTemplatesCount,
                        m.SharedCoordNs, m.SharedCoordEw, m.SharedCoordElevation,
                        m.CreatedAt.ToString("o"));
                    if (inserted) totalInserted++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store latest sync/save metrics for model {modelGuid}: {ex.Message}", ex);
            }

            // 2. Fetch and store latest periodic metrics for this model
            try
            {
                var endpoint = $"{PeriodicLatestByModelEndpoint}/{modelGuid}";
                var metrics = await FetchMetricsFromApiAsync<PeriodicMetricsApiRequest>(endpoint, "periodic-latest");
                foreach (var m in metrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_periodic");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertPeriodicMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.CaptureIntervalHours, m.IsManualTrigger,
                        m.TotalElementsCount, m.ModelElementsCount, m.AnnotativeElementsCount,
                        m.InplaceFamiliesCount, m.UnplacedRoomsCount, m.ViewsNotOnSheetsCount,
                        m.UnenclosedRoomsCount, m.WallsNotConnectedCount, m.PipesNotConnectedCount,
                        m.DuctsNotConnectedCount,
                        m.CreatedAt.ToString("o"));
                    if (inserted) totalInserted++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store latest periodic metrics for model {modelGuid}: {ex.Message}", ex);
            }

            // 3. Fetch and store latest manual metrics for this model
            try
            {
                var endpoint = $"{ManualLatestByModelEndpoint}/{modelGuid}";
                var metrics = await FetchMetricsFromApiAsync<ManualMetricsApiRequest>(endpoint, "manual-latest");
                foreach (var m in metrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_manual");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertManualMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.CommandSource, m.CaptureReason,
                        m.FamiliesOver5mbCount, m.PurgeableElementsCount,
                        m.CreatedAt.ToString("o"));
                    if (inserted) totalInserted++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store latest manual metrics for model {modelGuid}: {ex.Message}", ex);
            }

            _logger?.LogInfo($"Latest metrics fetch for model {modelGuid}: {totalInserted} new records stored from API");
            return totalInserted;
        }

        /// <summary>
        /// Fetches all 3 metrics types from API and stores new entries into local DB.
        /// Returns total number of new records inserted.
        /// </summary>
        public async Task<int> FetchAndStoreAllMetricsFromApiAsync()
        {
            if (_metricsRepository == null)
            {
                _logger?.LogWarning("MetricsRepository not available - cannot store API metrics locally");
                return 0;
            }

            var totalInserted = 0;

            // Fetch and store sync/save metrics
            try
            {
                var syncSaveMetrics = await FetchSyncSaveMetricsFromApiAsync();
                foreach (var m in syncSaveMetrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_sync_save");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertSyncSaveMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CaptureType, m.SyncGuid,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.FileSizeBytes, m.LevelsCount, m.GridsCount, m.DesignOptionsCount,
                        m.LinkedDwgCount, m.ImportedDwgCount, m.LinkedRevitCount, m.RasterImagesCount,
                        m.WarningsCount, m.DuplicateElementsCount, m.ModelGroupsCount, m.DetailGroupsCount,
                        m.TotalViewsCount, m.SheetsCount, m.TotalFamiliesCount, m.TotalWorksetsCount ?? 0,
                        m.NonNativeObjectStylesCount, m.ViewTemplatesCount,
                        m.SharedCoordNs, m.SharedCoordEw, m.SharedCoordElevation,
                        m.CreatedAt.ToString("o"));
                    if (inserted) totalInserted++;
                }
                _logger?.LogInfo($"SyncSave metrics: {totalInserted} new records stored from API");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store sync/save metrics: {ex.Message}", ex);
            }

            // Fetch and store periodic metrics
            var periodicInserted = 0;
            try
            {
                var periodicMetrics = await FetchPeriodicMetricsFromApiAsync();
                foreach (var m in periodicMetrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_periodic");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertPeriodicMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.CaptureIntervalHours, m.IsManualTrigger,
                        m.TotalElementsCount, m.ModelElementsCount, m.AnnotativeElementsCount,
                        m.InplaceFamiliesCount, m.UnplacedRoomsCount, m.ViewsNotOnSheetsCount,
                        m.UnenclosedRoomsCount, m.WallsNotConnectedCount, m.PipesNotConnectedCount,
                        m.DuctsNotConnectedCount,
                        m.CreatedAt.ToString("o"));
                    if (inserted) periodicInserted++;
                }
                totalInserted += periodicInserted;
                _logger?.LogInfo($"Periodic metrics: {periodicInserted} new records stored from API");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store periodic metrics: {ex.Message}", ex);
            }

            // Fetch and store manual metrics
            var manualInserted = 0;
            try
            {
                var manualMetrics = await FetchManualMetricsFromApiAsync();
                foreach (var m in manualMetrics)
                {
                    if (string.IsNullOrEmpty(m.CaptureId)) continue;
                    var exists = await _metricsRepository.CaptureExistsAsync(m.CaptureId, "model_file_metrics_manual");
                    if (exists) continue;

                    var inserted = await _metricsRepository.InsertManualMetricsFromApiAsync(
                        m.CaptureId, m.SessionId, m.DocumentId,
                        m.ModelGuid, m.ModelPath, m.ModelName,
                        m.CapturedAt.ToString("o"), m.CapturedBy,
                        m.CommandSource, m.CaptureReason,
                        m.FamiliesOver5mbCount, m.PurgeableElementsCount,
                        m.CreatedAt.ToString("o"));
                    if (inserted) manualInserted++;
                }
                totalInserted += manualInserted;
                _logger?.LogInfo($"Manual metrics: {manualInserted} new records stored from API");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store manual metrics: {ex.Message}", ex);
            }

            _logger?.LogInfo($"Metrics fetch complete: {totalInserted} total new records stored from API");
            return totalInserted;
        }
    }

    #region Manual Metrics API Request DTO

    /// <summary>
    /// Request body for POST /api/v1/Revit/metrics/manual
    /// Expensive metrics (families over 5MB, purgeable elements)
    /// Matches Swagger schema exactly.
    /// </summary>
    public class ManualMetricsApiRequest
    {
        [JsonPropertyName("captureId")]
        public string? CaptureId { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("documentId")]
        public string? DocumentId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("modelPath")]
        public string? ModelPath { get; set; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; set; }

        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; }

        [JsonPropertyName("capturedBy")]
        public string? CapturedBy { get; set; }

        [JsonPropertyName("commandSource")]
        public string? CommandSource { get; set; }

        [JsonPropertyName("captureReason")]
        public string? CaptureReason { get; set; }

        [JsonPropertyName("familiesOver5mbCount")]
        public int FamiliesOver5mbCount { get; set; }

        [JsonPropertyName("purgeableElementsCount")]
        public int PurgeableElementsCount { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    #endregion

    #region Periodic Metrics API Request DTO

    /// <summary>
    /// Request body for POST /api/v1/Revit/metrics/periodic
    /// Medium-cost metrics (element counts, room status, disconnections)
    /// Matches Swagger schema exactly.
    /// </summary>
    public class PeriodicMetricsApiRequest
    {
        [JsonPropertyName("captureId")]
        public string? CaptureId { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("documentId")]
        public string? DocumentId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("modelPath")]
        public string? ModelPath { get; set; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; set; }

        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; }

        [JsonPropertyName("capturedBy")]
        public string? CapturedBy { get; set; }

        [JsonPropertyName("captureIntervalHours")]
        public int CaptureIntervalHours { get; set; }

        [JsonPropertyName("isManualTrigger")]
        public bool IsManualTrigger { get; set; }

        [JsonPropertyName("totalElementsCount")]
        public int TotalElementsCount { get; set; }

        [JsonPropertyName("modelElementsCount")]
        public int ModelElementsCount { get; set; }

        [JsonPropertyName("annotativeElementsCount")]
        public int AnnotativeElementsCount { get; set; }

        [JsonPropertyName("inplaceFamiliesCount")]
        public int InplaceFamiliesCount { get; set; }

        [JsonPropertyName("unplacedRoomsCount")]
        public int UnplacedRoomsCount { get; set; }

        [JsonPropertyName("viewsNotOnSheetsCount")]
        public int ViewsNotOnSheetsCount { get; set; }

        [JsonPropertyName("unenclosedRoomsCount")]
        public int UnenclosedRoomsCount { get; set; }

        [JsonPropertyName("wallsNotConnectedCount")]
        public int WallsNotConnectedCount { get; set; }

        [JsonPropertyName("pipesNotConnectedCount")]
        public int PipesNotConnectedCount { get; set; }

        [JsonPropertyName("ductsNotConnectedCount")]
        public int DuctsNotConnectedCount { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    #endregion

    #region SyncSave Metrics API Request DTO

    /// <summary>
    /// Request body for POST /api/v1/Revit/metrics/syncsave
    /// Fast metrics captured on sync/save operations.
    /// Matches Swagger schema exactly.
    /// </summary>
    public class SyncSaveMetricsApiRequest
    {
        [JsonPropertyName("captureId")]
        public string? CaptureId { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("documentId")]
        public string? DocumentId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("modelPath")]
        public string? ModelPath { get; set; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; set; }

        [JsonPropertyName("captureType")]
        public string? CaptureType { get; set; } // "sync" or "save"

        [JsonPropertyName("syncGuid")]
        public string? SyncGuid { get; set; }

        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; }

        [JsonPropertyName("capturedBy")]
        public string? CapturedBy { get; set; }

        [JsonPropertyName("fileSizeBytes")]
        public long FileSizeBytes { get; set; }

        [JsonPropertyName("levelsCount")]
        public int LevelsCount { get; set; }

        [JsonPropertyName("gridsCount")]
        public int GridsCount { get; set; }

        [JsonPropertyName("designOptionsCount")]
        public int DesignOptionsCount { get; set; }

        [JsonPropertyName("linkedDwgCount")]
        public int LinkedDwgCount { get; set; }

        [JsonPropertyName("importedDwgCount")]
        public int ImportedDwgCount { get; set; }

        [JsonPropertyName("linkedRevitCount")]
        public int LinkedRevitCount { get; set; }

        [JsonPropertyName("rasterImagesCount")]
        public int RasterImagesCount { get; set; }

        [JsonPropertyName("warningsCount")]
        public int WarningsCount { get; set; }

        [JsonPropertyName("duplicateElementsCount")]
        public int DuplicateElementsCount { get; set; }

        [JsonPropertyName("modelGroupsCount")]
        public int ModelGroupsCount { get; set; }

        [JsonPropertyName("detailGroupsCount")]
        public int DetailGroupsCount { get; set; }

        [JsonPropertyName("totalViewsCount")]
        public int TotalViewsCount { get; set; }

        [JsonPropertyName("sheetCount")]
        public int SheetsCount { get; set; }

        [JsonPropertyName("totalFamiliesCount")]
        public int TotalFamiliesCount { get; set; }

        [JsonPropertyName("totalWorksetsCount")]
        public int? TotalWorksetsCount { get; set; }

        [JsonPropertyName("nonNativeObjectStylesCount")]
        public int? NonNativeObjectStylesCount { get; set; }

        [JsonPropertyName("viewTemplatesCount")]
        public int? ViewTemplatesCount { get; set; }

        [JsonPropertyName("sharedCoordNs")]
        [JsonConverter(typeof(DoubleWithoutScientificNotationConverter))]
        public double? SharedCoordNs { get; set; }

        [JsonPropertyName("sharedCoordEw")]
        [JsonConverter(typeof(DoubleWithoutScientificNotationConverter))]
        public double? SharedCoordEw { get; set; }

        [JsonPropertyName("sharedCoordElevation")]
        [JsonConverter(typeof(DoubleWithoutScientificNotationConverter))]
        public double? SharedCoordElevation { get; set; }

        // The unit label (mm / m / cm / ft) the SharedCoord* numbers above are
        // expressed in. The plugin converts from Revit's internal feet to the
        // project's display unit before sending; without this field the web
        // portal can't tell the unit and was hardcoding "ft" even when the
        // values were in millimetres.
        [JsonPropertyName("sharedCoordUnit")]
        public string? SharedCoordUnit { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    #endregion

    #region Custom JSON Converters

    /// <summary>
    /// Custom JsonConverter that serializes double values without scientific notation.
    /// Prevents validation errors on backend APIs that don't accept scientific notation.
    /// </summary>
    public class DoubleWithoutScientificNotationConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetDouble();

            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                    return null;

                if (double.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                    return result;
            }

            throw new JsonException($"Unable to convert '{reader.GetString()}' to double.");
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
                return;
            }

            var doubleValue = value.Value;

            // If value is effectively zero (within floating-point precision), write 0
            if (Math.Abs(doubleValue) < 1e-15)
            {
                writer.WriteNumberValue(0);
                return;
            }

            // Format as decimal string without scientific notation
            var formattedValue = doubleValue.ToString("F15", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

            // Write directly as raw JSON number to avoid WriteNumberValue re-introducing scientific notation
            writer.WriteRawValue(formattedValue);
        }
    }

    #endregion

    #region Model Sync API Request DTO

    /// <summary>
    /// Request body for POST /api/v1/Revit/model-syncs
    /// Sync-to-central metadata matching Swagger schema.
    /// </summary>
    public class ModelSyncApiRequest
    {
        [JsonPropertyName("syncGuid")]
        public string? SyncGuid { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("modelPath")]
        public string? ModelPath { get; set; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; set; }

        [JsonPropertyName("syncedBy")]
        public string? SyncedBy { get; set; }

        [JsonPropertyName("syncStartedAt")]
        public DateTime SyncStartedAt { get; set; }

        [JsonPropertyName("syncEndedAt")]
        public DateTime? SyncEndedAt { get; set; }

        [JsonPropertyName("syncDurationSeconds")]
        public double SyncDurationSeconds { get; set; }

        [JsonPropertyName("isLocalSaved")]
        public bool IsLocalSaved { get; set; }

        [JsonPropertyName("isSucceeded")]
        public bool IsSucceeded { get; set; }

        [JsonPropertyName("isRelinquished")]
        public bool IsRelinquished { get; set; }

        [JsonPropertyName("totalWorksetsCount")]
        public int? TotalWorksetsCount { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("modifiedAt")]
        public DateTime ModifiedAt { get; set; }
    }

    #endregion
}
