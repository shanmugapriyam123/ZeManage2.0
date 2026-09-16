using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs model session data (document open/close events) to the backend.
    /// POST /api/v1/Revit/models/model-sessions
    /// Primary: SignalR two-way communication.
    /// Fallback: HTTP POST.
    /// Queues to offline_queue if API is unavailable.
    /// </summary>
    public class ModelSessionSyncService
    {
        private readonly ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly SessionRepository? _sessionRepository;
        private SessionSyncService? _sessionSyncService;
        private readonly ILogger? _logger;

        private const string Endpoint = "/api/v1/Revit/models/model-sessions";
        private const string HubMethod = "SendModelSessionData";

        public ModelSessionSyncService(
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null,
            SessionRepository? sessionRepository = null)
        {
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _sessionRepository = sessionRepository;
            _logger = logger;
            _logger?.LogInfo($"ModelSessionSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Wires the SessionSyncService for parent-session confirmation lookups.
        /// Resolved late to break the circular dependency between SessionSyncService
        /// and ModelSessionSyncService when both are registered in the DI container.
        /// </summary>
        public void SetSessionSyncService(SessionSyncService sessionSyncService)
        {
            _sessionSyncService = sessionSyncService;
        }

        /// <summary>
        /// Sync a model session (document open event) to the backend.
        /// Primary: SignalR two-way communication.
        /// Fallback: HTTP POST to /api/v1/Revit/models/model-sessions.
        /// </summary>
        /// <summary>
        /// PATCH 
        /// .
        /// Sends the latest accumulated post-open Worksets-dialog total (in seconds) for
        /// the (sessionId, modelGuid) document_session row. Idempotent: server overwrites
        /// the column with the supplied total. Falls back to the offline queue when offline
        /// or unauthenticated; replays continue to be safe because the payload is a total,
        /// not a delta.
        /// </summary>
        public Task<bool> SyncWorksetOpeningDurationAsync(string sessionId, string modelGuid, double totalSeconds, string modifiedBy = "")
        {
            return SyncDurationAsync(
                sessionId, modelGuid, totalSeconds,
                endpoint: $"{Endpoint}/workset-opening-duration",
                operationType: OfflineApiWrapper.OperationTypes.WorksetOpeningDuration,
                kind: "workset-opening-duration",
                buildPayload: (sid, mg, total) => new WorksetOpeningDurationRequest
                {
                    SessionId = sid,
                    ModelGuid = mg,
                    WorksetOpeningDurationSeconds = total
                });
        }

        /// <summary>
        /// PATCH /api/v1/Revit/models/model-sessions/link-loading-duration.
        /// Sends the latest accumulated post-open Manage-Links-dialog total (in seconds) for
        /// the (sessionId, modelGuid) document_session row. Idempotent on replay (total, not
        /// delta). Endpoint path proposed for server-team symmetry with workset-opening-duration.
        /// </summary>
        public Task<bool> SyncLinkLoadingDurationAsync(string sessionId, string modelGuid, double totalSeconds, string modifiedBy = "")
        {
            return SyncDurationAsync(
                sessionId, modelGuid, totalSeconds,
                endpoint: $"{Endpoint}/link-loading-duration",
                operationType: OfflineApiWrapper.OperationTypes.LinkLoadingDuration,
                kind: "link-loading-duration",
                buildPayload: (sid, mg, total) => new LinkLoadingDurationRequest
                {
                    SessionId = sid,
                    ModelGuid = mg,
                    LinkLoadingDurationSeconds = total
                });
        }

        private async Task<bool> SyncDurationAsync<T>(
            string sessionId, string modelGuid, double totalSeconds,
            string endpoint, string operationType, string kind,
            Func<string, string, double, T> buildPayload)
            where T : class
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(modelGuid))
                {
                    _logger?.LogDebug($"[{kind}] skipped: missing sessionId or modelGuid");
                    return false;
                }

                var payload = buildPayload(sessionId, modelGuid, totalSeconds);

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    try
                    {
                        // Server route is PATCH (not POST) — see swagger contract. Using POST
                        // returns 405 which the 4xx branch below silently treats as success,
                        // so the value never lands in the production column.
                        var response = await _httpClient.PatchAsync(endpoint, payload);
                        if (response.IsSuccessStatusCode)
                        {
                            _logger?.LogInfo($"[{kind}] sent: total={totalSeconds:F2}s session={sessionId} model={modelGuid} (Status: {response.StatusCode})");
                            return true;
                        }

                        var statusCode = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync();
                        _logger?.LogWarning($"[{kind}] HTTP {response.StatusCode} body={body}");

                        // 4xx is a client error; queueing won't help (server will reject the same payload).
                        if (statusCode >= 400 && statusCode < 500)
                        {
                            return true;
                        }

                        // 5xx — fall through to queue
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger?.LogWarning($"[{kind}] HTTP error: {ex.Message}");
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger?.LogWarning($"[{kind}] HTTP timeout: {ex.Message}");
                    }
                }
                else if (_httpClient != null)
                {
                    _logger?.LogDebug($"[{kind}] HTTP client not authenticated — queuing");
                }

                if (_offlineQueue != null)
                {
                    await QueueDurationForOfflineSyncAsync(endpoint, operationType, kind, sessionId, payload);
                    return true;
                }

                _logger?.LogWarning($"[{kind}] no transport available — drop");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[{kind}] failed: {ex.Message}", ex);
                return false;
            }
        }

        private async Task QueueDurationForOfflineSyncAsync<T>(string endpoint, string operationType, string kind, string sessionId, T payload) where T : class
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = "PATCH",
                    Payload = payload,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString() : sessionId,
                    operationType: operationType,
                    operationData: jsonData,
                    priority: 3); // Medium-high priority — same as model_session

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"[{kind}] queued for offline replay");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[{kind}] offline queue enqueue failed: {ex.Message}");
            }
        }

        public async Task<bool> SyncModelSessionAsync(ModelSessionApiRequest request)
        {
            try
            {
                _logger?.LogInfo($"Syncing model session: {request.DocumentTitle} (Session: {request.SessionId})");

                // Guard: parent session must be confirmed on server before posting model_session.
                // Reproduces when a model is opened directly from File Explorer: Revit fires
                // DocumentOpening before our session POST has roundtripped, so the model_session
                // POST hits the server with a SessionId that doesn't exist yet → server raises
                // FK violation (PostgreSQL 23503 → 500) or "not found" (400). The 400 path
                // would be silently dropped; the 500 path retries via offline queue. Avoid both
                // by queuing immediately when the parent session isn't confirmed — the offline
                // processor will retry after SessionSyncService.ConfirmSessionOnServer fires.
                if (!string.IsNullOrEmpty(request.SessionId) &&
                    _sessionSyncService != null &&
                    !_sessionSyncService.IsSessionConfirmedOnServer(request.SessionId))
                {
                    _logger?.LogInfo($"Model session sync deferred — parent session {request.SessionId} not yet confirmed on server. Queuing for offline retry.");
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(request);
                        return true;
                    }
                    return false;
                }

                // HTTP POST first for guaranteed server-side persistence
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var httpResult = await SyncViaHttpAsync(request);
                    if (httpResult)
                    {
                        // Also notify via SignalR for real-time updates (fire-and-forget, non-critical)
                        await NotifyViaSignalRAsync(request);
                        return true;
                    }
                    // HTTP failed — SyncViaHttpAsync already queued if possible, but fall through as safety net
                }

                if (_httpClient != null && !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP skipped - no auth tokens available (register device first)");
                }

                // Queue for later if no authenticated client or HTTP failed
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request);
                    _logger?.LogInfo("Queued model session for later sync");
                    return true;
                }

                _logger?.LogWarning("No sync transport available for model session");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model session sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends a SignalR notification for real-time updates (fire-and-forget, non-critical).
        /// This does NOT persist data — HTTP POST handles persistence.
        /// </summary>
        private async Task NotifyViaSignalRAsync(ModelSessionApiRequest request)
        {
            if (_signalRService == null || !_signalRService.IsConnected) return;
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.ModelRegistered, request);
                message.SenderSessionId = request.SessionId;
                await _signalRService.SendAsync(HubMethod, message);
                _logger?.LogDebug($"SignalR notification sent for model session: {request.DocumentTitle}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR notification failed (non-critical): {ex.Message}");
            }
        }

        private async Task<bool> SyncViaHttpAsync(ModelSessionApiRequest request)
        {
            try
            {
                // Log actual payload being sent for debugging
                var payloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogInfo($"Model session sync request payload:\n{payloadJson}");

                var response = await _httpClient!.PostAsync(Endpoint, request);

                if (response.IsSuccessStatusCode)
                {
                    // Log the response body so we can see whether the server actually persisted
                    // the row, what identifier it returned (e.g. modelSessionId vs sessionId),
                    // and whether the body says success=true or hides a failure inside a 200.
                    // Without this, a "200 OK with no row written" looks identical to a real
                    // persist in the log, which is what made the "model shows Offline despite
                    // being open" bug invisible.
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogInfo($"Model session synced via HTTP: {request.DocumentTitle} (Status: {response.StatusCode})");
                    _logger?.LogInfo($"Model session POST response body:\n{responseBody}");

                    // Probe the server's view of the model's active users right after the POST
                    // so we can prove whether the row we just created actually shows up in the
                    // dashboard's source-of-truth query. Fire-and-forget; log-only — no behaviour
                    // change. Distinct from the pre-open dup-check (which runs before this POST
                    // and is looking for OTHER users); this call is purely "did MY open land?".
                    if (!string.IsNullOrEmpty(request.ModelGuid))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var activeUsers = await GetModelUsersAsync(request.ModelGuid);
                                _logger?.LogInfo($"[GetModelUsers post-POST] model={request.ModelGuid} returned {activeUsers.Count} active user(s) — payload: {JsonSerializer.Serialize(activeUsers)}");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug($"[GetModelUsers post-POST] failed (non-critical): {ex.Message}");
                            }
                        });
                    }

                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP model session sync failed: (Status: {response.StatusCode}, Body: {responseBody})");

                    // Don't queue client errors (400, 403, 409) — they're permanent failures
                    var statusCode = (int)response.StatusCode;
                    if (statusCode >= 400 && statusCode < 500)
                    {
                        _logger?.LogWarning($"Model session client error {response.StatusCode} — not queuing");
                        return true;
                    }

                    // Queue server errors (5xx) for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(request);
                        return true;
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Model session sync HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Model session sync timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// GET /api/v1/Revit/models/users/{modelGuid}
        /// Returns active users for the model from the server. Fail-open on any API error.
        /// </summary>
        public async Task<List<ModelActiveUserApiResult>> GetModelUsersAsync(string modelGuid)
        {
            if (_httpClient == null || string.IsNullOrEmpty(modelGuid))
                return new List<ModelActiveUserApiResult>();
            try
            {
                var response = await _httpClient.GetAsync($"/api/v1/Revit/models/users/{modelGuid}");
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning($"GetModelUsersAsync: {response.StatusCode} for model {modelGuid}");
                    return new List<ModelActiveUserApiResult>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogInfo($"[GetModelUsers] Raw API response: {json}");
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                List<ModelActiveUserApiResult>? users = null;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl))
                    users = JsonSerializer.Deserialize<List<ModelActiveUserApiResult>>(dataEl.GetRawText(), jsonOptions);
                else if (root.ValueKind == JsonValueKind.Array)
                    users = JsonSerializer.Deserialize<List<ModelActiveUserApiResult>>(json, jsonOptions);

                return users ?? new List<ModelActiveUserApiResult>();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"GetModelUsersAsync failed (fail-open): {ex.Message}");
                return new List<ModelActiveUserApiResult>();
            }
        }

        /// <summary>
        /// Fetches all model sessions from the API (GET /api/v1/Revit/models/model-sessions).
        /// </summary>
        public async Task<List<ModelSessionApiRequest>> FetchModelSessionsFromApiAsync()
        {
            try
            {
                _logger?.LogInfo("Fetching model sessions from API...");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching model sessions");
                    return new List<ModelSessionApiRequest>();
                }

                var response = await _httpClient.GetAsync(Endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch model sessions failed: {response.StatusCode} - {responseBody}");
                    return new List<ModelSessionApiRequest>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch model sessions response length: {json.Length} chars");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<ModelSessionApiRequest>? apiSessions = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<ModelSessionApiRequest>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            apiSessions = new List<ModelSessionApiRequest> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize model sessions API response: {ex.Message}", ex);
                    return new List<ModelSessionApiRequest>();
                }

                if (apiSessions == null || apiSessions.Count == 0)
                {
                    _logger?.LogWarning("No model sessions returned from API");
                    return new List<ModelSessionApiRequest>();
                }

                _logger?.LogInfo($"Fetched {apiSessions.Count} model sessions from API");
                return apiSessions;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch model sessions from API: {ex.Message}", ex);
                return new List<ModelSessionApiRequest>();
            }
        }

        /// <summary>
        /// Fetches model sessions for a specific model (GET /api/v1/Revit/models/{modelGuid}/sessions).
        /// </summary>
        public async Task<List<ModelSessionApiRequest>> FetchModelSessionsByModelGuidAsync(string modelGuid)
        {
            try
            {
                _logger?.LogInfo($"Fetching model sessions from API for model: {modelGuid}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching model sessions");
                    return new List<ModelSessionApiRequest>();
                }

                var endpoint = $"/api/v1/Revit/models/{modelGuid}/sessions";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch model sessions by GUID failed: {response.StatusCode} - {responseBody}");
                    return new List<ModelSessionApiRequest>();
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<ModelSessionApiRequest>? apiSessions = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<ModelSessionApiRequest>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<ModelSessionApiRequest>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            apiSessions = new List<ModelSessionApiRequest> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize model sessions API response: {ex.Message}", ex);
                    return new List<ModelSessionApiRequest>();
                }

                if (apiSessions == null || apiSessions.Count == 0)
                {
                    _logger?.LogWarning($"No model sessions returned from API for model: {modelGuid}");
                    return new List<ModelSessionApiRequest>();
                }

                _logger?.LogInfo($"Fetched {apiSessions.Count} model sessions from API for model: {modelGuid}");
                return apiSessions;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch model sessions from API: {ex.Message}", ex);
                return new List<ModelSessionApiRequest>();
            }
        }

        /// <summary>
        /// Fetches model sessions from API and stores new entries into local DB (document_sessions table).
        /// Returns count of new model sessions inserted.
        /// </summary>
        public async Task<int> FetchAndStoreModelSessionsFromApiAsync()
        {
            if (_sessionRepository == null)
            {
                _logger?.LogWarning("SessionRepository not available - cannot store API model sessions locally");
                return 0;
            }

            var insertedCount = 0;
            try
            {
                var apiSessions = await FetchModelSessionsFromApiAsync();

                foreach (var m in apiSessions)
                {
                    if (string.IsNullOrEmpty(m.SessionId)) continue;

                    var exists = await _sessionRepository.DocumentSessionExistsAsync(m.SessionId, m.ModelGuid);
                    if (exists) continue;

                    var inserted = await _sessionRepository.InsertDocumentSessionFromApiAsync(
                        m.SessionId, m.ModelGuid, m.CentralModelPath,
                        m.CentralModelName, m.LocalModelLocation, m.DocumentTitle,
                        m.LocalProjectName, m.CloudProjectName,
                        m.OpenedAt.ToString("o"), m.OpeningStartedAt.ToString("o"),
                        m.ModelOpeningDuration, m.OpenedWorksetsCount,
                        m.ClosedAt?.ToString("o"), m.Status == "Active",
                        m.TotalModifications, m.LastSavedAt?.ToString("o"),
                        m.CreatedBy, m.ModifiedBy);

                    if (inserted) insertedCount++;
                }

                _logger?.LogInfo($"Model sessions fetch complete: {insertedCount} new record(s) stored from API");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store model sessions from API: {ex.Message}", ex);
            }
            return insertedCount;
        }

        /// <summary>
        /// Updates a model session status via PATCH /api/v1/Revit/models/model-sessions
        /// (bare route — server identifies the row from sessionId+modelGuid in the body).
        /// Used for session close ("Closed") and crash detection ("Crashed").
        /// </summary>
        public async Task<bool> UpdateModelSessionStatusAsync(string sessionId, string modelGuid, string status, DateTime? closedAt = null, int totalModifications = 0, string modifiedBy = null)
        {
            try
            {
                _logger?.LogInfo($"Updating model session status: {sessionId} (ModelGuid: {modelGuid}, Status: {status})");

                var updateRequest = new ModelSessionStatusUpdateRequest
                {
                    SessionId = sessionId,
                    ModelGuid = modelGuid,
                    Status = status,
                    ClosedAt = closedAt ?? DateTime.UtcNow
                };

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await PatchViaHttpAsync(sessionId, updateRequest);
                }

                if (_httpClient != null && !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("Model session status update skipped - no auth tokens available");
                }

                if (_offlineQueue != null)
                {
                    await QueueStatusUpdateForOfflineSyncAsync(sessionId, updateRequest);
                    return true;
                }

                _logger?.LogWarning("No transport available for model session status update");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model session status update failed: {ex.Message}", ex);
                return false;
            }
        }

        private async Task<bool> PatchViaHttpAsync(string sessionId, ModelSessionStatusUpdateRequest updateRequest)
        {
            try
            {
                // Server route is a bare PATCH "model-sessions" ([HttpPatch("model-sessions")],
                // RevitModelSessionController.PatchModelSession) that identifies the row from
                // SessionId + ModelGuid in the body — there is no {sessionId} route segment.
                // Appending it here produced a URL matching no server route, so this call 404'd
                // on every single invocation and, since 4xx is treated as a permanent failure
                // below, silently dropped every close-status update forever.
                var endpoint = Endpoint;

                var payloadJson = JsonSerializer.Serialize(updateRequest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogInfo($"Model session status update payload:\n{payloadJson}");

                var response = await _httpClient!.PatchAsync(endpoint, updateRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Model session status updated via HTTP: {sessionId} → {updateRequest.Status} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP model session status update failed: {sessionId} (Status: {response.StatusCode}, Body: {responseBody})");

                var statusCode = (int)response.StatusCode;
                if (statusCode >= 400 && statusCode < 500)
                {
                    _logger?.LogWarning($"Model session status update client error {response.StatusCode} — not queuing");
                    return true;
                }

                if (_offlineQueue != null)
                {
                    await QueueStatusUpdateForOfflineSyncAsync(sessionId, updateRequest);
                    return true;
                }
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Model session status update HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueStatusUpdateForOfflineSyncAsync(sessionId, updateRequest);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Model session status update timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueStatusUpdateForOfflineSyncAsync(sessionId, updateRequest);
                    return true;
                }
                return false;
            }
        }

        private async Task QueueStatusUpdateForOfflineSyncAsync(string sessionId, ModelSessionStatusUpdateRequest updateRequest)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = $"{Endpoint}/{sessionId}",
                    HttpMethod = "PATCH",
                    Payload = updateRequest,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: sessionId,
                    operationType: OfflineApiWrapper.OperationTypes.ModelSession,
                    operationData: jsonData,
                    priority: 2); // High priority for status updates

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: model_session status update ({updateRequest.Status}) for {sessionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue model session status update: {ex.Message}", ex);
            }
        }

        private async Task QueueForOfflineSyncAsync(ModelSessionApiRequest request)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = Endpoint,
                    HttpMethod = "POST",
                    Payload = request,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: string.IsNullOrEmpty(request.SessionId) ? Guid.NewGuid().ToString() : request.SessionId,
                    operationType: OfflineApiWrapper.OperationTypes.ModelSession,
                    operationData: jsonData,
                    priority: 3); // Medium-high priority

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: model_session for {request.DocumentTitle}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline model session operation: {ex.Message}", ex);
            }
        }
    }

    #region Model Session API Request DTOs

    /// <summary>
    /// Request body for PATCH /api/v1/Revit/models/model-sessions (bare route — row is
    /// identified from the body, not a URL segment). Used for status updates (Close, Crash).
    /// Matches API spec: PatchRevitModelSessionRequest.
    /// Required fields: sessionId, modelGuid.
    /// </summary>
    public class ModelSessionStatusUpdateRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("modelGuid")]
        public string ModelGuid { get; set; } = "";

        /// <summary>
        /// Model session status as string.
        /// Values: "Closed" (0), "Active" (1), "Crashed" (2), "Inactive" (3), "Unknown" (4).
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("closedAt")]
        public DateTime? ClosedAt { get; set; }
    }

    /// <summary>
    /// Request body for POST /api/v1/Revit/models/model-sessions
    /// Matches Swagger schema exactly.
    /// </summary>
    public class ModelSessionApiRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("modelGuid")]
        public string ModelGuid { get; set; } = "";

        [JsonPropertyName("centralModelPath")]
        public string CentralModelPath { get; set; } = "";

        [JsonPropertyName("centralModelName")]
        public string CentralModelName { get; set; } = "";

        [JsonPropertyName("localModelLocation")]
        public string LocalModelLocation { get; set; } = "";

        [JsonPropertyName("documentTitle")]
        public string DocumentTitle { get; set; } = "";

        [JsonPropertyName("localProjectName")]
        public string LocalProjectName { get; set; } = "";

        [JsonPropertyName("cloudProjectName")]
        public string CloudProjectName { get; set; } = "";

        [JsonPropertyName("openedAt")]
        public DateTime OpenedAt { get; set; }

        [JsonPropertyName("openingStartedAt")]
        public DateTime OpeningStartedAt { get; set; }

        [JsonPropertyName("modelOpeningDuration")]
        public double ModelOpeningDuration { get; set; }

        [JsonPropertyName("openedWorksetsCount")]
        public int OpenedWorksetsCount { get; set; }

        [JsonPropertyName("closedAt")]
        public DateTime? ClosedAt { get; set; }

        /// <summary>
        /// Model session status as string.
        /// Values: "Closed" (0), "Active" (1), "Crashed" (2), "Inactive" (3), "Unknown" (4).
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("totalModifications")]
        public int TotalModifications { get; set; }

        [JsonPropertyName("lastSavedAt")]
        public DateTime? LastSavedAt { get; set; }

        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; } = "";

        [JsonPropertyName("modifiedBy")]
        public string ModifiedBy { get; set; } = "";

        [JsonPropertyName("revitUsername")]
        public string RevitUsername { get; set; } = "";
    }

    /// <summary>
    /// Request body for POST /api/v1/Revit/models/model-sessions/workset-opening-duration.
    /// Sent every time the post-open Worksets dialog closes —
    /// WorksetOpeningDurationSeconds is the new accumulated total (idempotent on replay;
    /// server overwrites the column).
    /// </summary>
    public class WorksetOpeningDurationRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("modelGuid")]
        public string ModelGuid { get; set; } = "";

        [JsonPropertyName("worksetOpeningDurationSeconds")]
        public double WorksetOpeningDurationSeconds { get; set; }
    }

    /// <summary>
    /// Request body for POST /api/v1/Revit/models/model-sessions/link-loading-duration.
    /// Sent every time the post-open Manage Links dialog closes —
    /// LinkLoadingDurationSeconds is the new accumulated total (idempotent on replay;
    /// server overwrites the column). Endpoint + field name proposed for server-team
    /// symmetry with workset-opening-duration.
    /// </summary>
    public class LinkLoadingDurationRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("modelGuid")]
        public string ModelGuid { get; set; } = "";

        [JsonPropertyName("linkLoadingDurationSeconds")]
        public double LinkLoadingDurationSeconds { get; set; }
    }

    #endregion

    // ── Duplicate-session API models ──────────────────────────────────────────

    public class ModelActiveUserApiResult
    {
        [JsonPropertyName("revitUsername")]
        public string? RevitUsername { get; set; }

        [JsonPropertyName("computerName")]
        public string? ComputerName { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
