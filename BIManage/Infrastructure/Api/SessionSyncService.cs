using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
    /// Syncs local session data to the backend via SignalR two-way communication.
    /// Reads from SQLite and sends via SignalR hub method "SendSessionData".
    /// Falls back to HTTP POST if SignalR is not connected.
    /// Queues to offline_queue if API is unavailable.
    /// Null values from the database are sent as null to the API.
    /// </summary>
    public class SessionSyncService
    {
        private readonly SessionRepository _sessionRepository;
        private readonly ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;
        private const string Endpoint = "/api/v1/Revit/session/Open";
        private const string UpdateEndpoint = "/api/v1/Revit/session";
        private const string HeartbeatEndpoint = "/api/v1/Revit/session/{0}/heartbeat";
        private const string HubMethod = "SendSessionData";

        /// <summary>
        /// Last heartbeat response from the server. Contains license mode and seat counts.
        /// </summary>
        public HeartbeatResponse? LastHeartbeatResponse { get; private set; }

        /// <summary>Raw JSON body from the last heartbeat response (for diagnostic logging).</summary>
        public string? LastHeartbeatResponseBody { get; private set; }

        /// <summary>
        /// Tracks session IDs that have been confirmed synced to the server (HTTP 200/201).
        /// Other services (MetricsSyncService, AuditLogSyncService) should check this
        /// before sending payloads with SessionId FK references.
        /// </summary>
        private readonly System.Collections.Generic.HashSet<string> _confirmedSessions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if the given session has been confirmed synced to the server via HTTP.
        /// </summary>
        public bool IsSessionConfirmedOnServer(string sessionId) => _confirmedSessions.Contains(sessionId);

        /// <summary>
        /// Marks a session as confirmed on the server. Called by OfflineSyncProcessor
        /// when a queued session_open or session PATCH fallback succeeds, so that downstream
        /// audit_log_sync and metrics operations unblock on the next queue pass.
        /// </summary>
        public void ConfirmSessionOnServer(string sessionId) => _confirmedSessions.Add(sessionId);

        /// <summary>
        /// Creates a SessionSyncService with SignalR and optional HTTP fallback.
        /// </summary>
        public SessionSyncService(
            SessionRepository sessionRepository,
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null)
        {
            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _logger?.LogInfo($"SessionSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Syncs the current session data to the backend.
        /// Primary: SignalR two-way communication via hub method "SendSessionData".
        /// Fallback: HTTP POST to /api/v1/Revit/sessions if SignalR is not connected.
        /// </summary>
        public async Task<bool> SyncSessionAsync(string sessionId)
        {
            try
            {
                _logger?.LogInfo($"Syncing session: {sessionId}");

                // Read full session data from SQLite
                var session = await _sessionRepository.GetSessionFullAsync(sessionId);
                if (session == null)
                {
                    _logger?.LogWarning($"Session not found in database: {sessionId}");
                    return false;
                }

                // Map to API request DTO (null values pass through)
                var apiRequest = MapToApiRequest(session);

                // HTTP POST first for guaranteed server-side persistence
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var httpResult = await SyncViaHttpAsync(sessionId, apiRequest);
                    if (httpResult)
                    {
                        // Also notify via SignalR for real-time updates (fire-and-forget, non-critical)
                        await NotifyViaSignalRAsync(sessionId, apiRequest);
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
                    await QueueForOfflineSyncAsync(sessionId, Endpoint, "POST", apiRequest, OfflineApiWrapper.OperationTypes.SessionOpen);
                    _logger?.LogInfo($"Queued session for later sync: {sessionId}");
                    return true;
                }

                _logger?.LogWarning("No sync transport available for session");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Session sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends a SignalR notification for real-time updates (fire-and-forget, non-critical).
        /// This does NOT persist data — HTTP POST handles persistence.
        /// </summary>
        private async Task NotifyViaSignalRAsync(string sessionId, SessionApiRequest apiRequest)
        {
            if (_signalRService == null || !_signalRService.IsConnected) return;
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.SessionActivity, apiRequest);
                message.SenderSessionId = sessionId;
                message.SenderUsername = apiRequest.Username;
                await _signalRService.SendAsync(HubMethod, message);
                _logger?.LogDebug($"SignalR notification sent for session: {sessionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR notification failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        /// Sends session data via HTTP POST. If POST fails with 500 (likely duplicate key),
        /// falls back to PATCH (session already exists on server).
        /// Only queues to offline_queue for network errors or timeouts.
        /// </summary>
        private async Task<bool> SyncViaHttpAsync(string sessionId, SessionApiRequest apiRequest)
        {
            try
            {
                // Log the request payload for debugging
                var jsonPayload = System.Text.Json.JsonSerializer.Serialize(apiRequest, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
                });
                _logger?.LogInfo($"Session sync request payload:\n{jsonPayload}");

                var response = await _httpClient!.PostAsync(Endpoint, apiRequest);

                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _confirmedSessions.Add(sessionId);
                    _logger?.LogInfo($"Session synced via HTTP: {sessionId} (Status: {response.StatusCode})");
                    _logger?.LogInfo($"Session POST response body:\n{responseBody}");
                    return true;
                }

                // 500 = server error, likely duplicate key (session already exists on server)
                // Server returns a generic 500, not a specific "entity changes" message
                // Fall back to PATCH update instead of endlessly retrying POST
                if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    _logger?.LogInfo($"Session POST returned 500 — falling back to PATCH: {sessionId}");
                    var patchResult = await PatchFullSessionAsync(sessionId, apiRequest);
                    if (!patchResult)
                    {
                        // Both POST (500) and PATCH failed — return false so the caller can
                        // queue for offline retry. The offline processor will attempt POST again
                        // and, if it gets 500, will retry via TryPatchSessionFromQueueAsync.
                        _logger?.LogWarning($"Session POST+PATCH both failed for {sessionId} — deferring to offline queue");
                        return false;
                    }
                    return true; // PATCH succeeded — session confirmed (PatchFullSessionAsync adds to _confirmedSessions)
                }

                _logger?.LogWarning($"HTTP session sync failed: {sessionId} (Status: {response.StatusCode}, Body: {responseBody})");

                // Queue transient errors for retry (4xx auth issues, other 5xx)
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId, Endpoint, "POST", apiRequest, OfflineApiWrapper.OperationTypes.SessionOpen);
                    return true; // Return true because it's queued
                }
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Session sync HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId, Endpoint, "POST", apiRequest, OfflineApiWrapper.OperationTypes.SessionOpen);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Session sync timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(sessionId, Endpoint, "POST", apiRequest, OfflineApiWrapper.OperationTypes.SessionOpen);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// PATCH fallback when POST fails (session already exists on server).
        /// Uses the same update endpoint but sends status/timing fields.
        /// </summary>
        private async Task<bool> PatchFullSessionAsync(string sessionId, SessionApiRequest apiRequest)
        {
            try
            {
                var endpoint = $"{UpdateEndpoint}/{sessionId}";
                var updateRequest = new SessionUpdateRequest
                {
                    EndedAt = apiRequest.EndedAt,
                    ClosedAt = apiRequest.ClosedAt,
                    Status = apiRequest.Status
                };

                var response = await _httpClient!.PatchAsync(endpoint, updateRequest);

                if (response.IsSuccessStatusCode)
                {
                    _confirmedSessions.Add(sessionId);
                    _logger?.LogInfo($"Session PATCH fallback succeeded: {sessionId} (Status: {response.StatusCode})");
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Session PATCH fallback failed: {sessionId} (Status: {response.StatusCode}, Body: {body})");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Session PATCH fallback error: {sessionId} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates an existing session via PATCH /api/v1/Revit/session/{sessionId}.
        /// Used for session close, crash detection, or status changes.
        /// </summary>
        public async Task<bool> UpdateSessionAsync(string sessionId, string status, bool isActive, bool crashDetected)
        {
            try
            {
                _logger?.LogInfo($"Updating session: {sessionId} (Status: {status}, Active: {isActive}, Crashed: {crashDetected})");

                var now = DateTime.UtcNow;
                var updateRequest = new SessionUpdateRequest
                {
                    EndedAt = now,
                    ClosedAt = now,
                    Status = MapStatusToString(crashDetected ? "Crashed" : status)
                };

                // Only use HTTP PATCH (no SignalR for updates)
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await UpdateViaHttpAsync(sessionId, updateRequest);
                }

                if (_httpClient != null && !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("Session update skipped - no auth tokens available");
                }
                else
                {
                    _logger?.LogWarning("No HTTP client available for session update");
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Session update failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends session update via HTTP PATCH.
        /// If HTTP fails, queues to offline_queue for later retry.
        /// </summary>
        private async Task<bool> UpdateViaHttpAsync(string sessionId, SessionUpdateRequest updateRequest)
        {
            try
            {
                var endpoint = $"{UpdateEndpoint}/{sessionId}";

                // Log the request payload for debugging
                var jsonPayload = JsonSerializer.Serialize(updateRequest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never
                });
                _logger?.LogInfo($"Session update request payload:\n{jsonPayload}");

                var response = await _httpClient!.PatchAsync(endpoint, updateRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Session updated via HTTP: {sessionId} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP session update failed: {sessionId} (Status: {response.StatusCode}, Body: {responseBody})");

                    // Queue ALL failed responses for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(sessionId, endpoint, "PATCH", updateRequest, OfflineApiWrapper.OperationTypes.SessionUpdate);
                        return true;
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Session update HTTP error: {ex.Message}", ex);
                // Queue for later sync
                if (_offlineQueue != null)
                {
                    var endpoint = $"{UpdateEndpoint}/{sessionId}";
                    await QueueForOfflineSyncAsync(sessionId, endpoint, "PATCH", updateRequest, OfflineApiWrapper.OperationTypes.SessionUpdate);
                    return true; // Return true because it's queued
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Session update timeout: {ex.Message}", ex);
                // Queue for later sync
                if (_offlineQueue != null)
                {
                    var endpoint = $"{UpdateEndpoint}/{sessionId}";
                    await QueueForOfflineSyncAsync(sessionId, endpoint, "PATCH", updateRequest, OfflineApiWrapper.OperationTypes.SessionUpdate);
                    return true; // Return true because it's queued
                }
                return false;
            }
        }

        /// <summary>
        /// Sends a heartbeat to keep the session alive.
        /// PATCH /api/v1/Revit/session/{sessionId}/heartbeat
        /// </summary>
        public async Task<bool> SendHeartbeatAsync(
            string sessionId,
            DateTime? lastHeartbeat = null,
            double? memoryUsageMb = null,
            double? cpuUsagePercent = null,
            double? diskUsageMb = null,
            double? graphicsUsageMb = null,
            string modelGuid = null)
        {
            try
            {
                _logger?.LogDebug($"Sending heartbeat for session: {sessionId}");

                // Skip heartbeats until the session is confirmed on the server.
                // If the initial POST returned 500 and PATCH fallback returned 404, the session
                // doesn't exist server-side yet and is queued in the offline processor for retry.
                // Sending heartbeats during this window produces a 404 every ~30s until the
                // offline queue successfully creates the session — flooding logs and wasting calls.
                // Once OfflineSyncProcessor confirms the session, heartbeats resume automatically.
                if (!IsSessionConfirmedOnServer(sessionId))
                {
                    _logger?.LogDebug($"Heartbeat skipped — session {sessionId} not yet confirmed on server (queued for offline sync)");
                    return false;
                }

                var heartbeatRequest = new HeartbeatRequest
                {
                    LastHeartbeat = lastHeartbeat ?? DateTime.UtcNow,
                    MemoryUsageMb = memoryUsageMb,
                    CpuUsagePercent = cpuUsagePercent,
                    DiskUsageMb = diskUsageMb,
                    GraphicsUsageMb = graphicsUsageMb,
                    ModelGuid = modelGuid
                };

                // Only use HTTP PATCH for heartbeats
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SendHeartbeatViaHttpAsync(sessionId, heartbeatRequest);
                }

                _logger?.LogDebug("Heartbeat skipped - no authenticated HTTP client");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Heartbeat failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends heartbeat via HTTP PATCH.
        /// Heartbeats are not queued for offline sync (they're time-sensitive).
        /// </summary>
        private async Task<bool> SendHeartbeatViaHttpAsync(string sessionId, HeartbeatRequest heartbeatRequest)
        {
            try
            {
                var endpoint = string.Format(HeartbeatEndpoint, sessionId);

                var response = await _httpClient!.PatchAsync(endpoint, heartbeatRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug($"Heartbeat sent: {sessionId} (Status: {response.StatusCode})");

                    // Parse license data from heartbeat response body
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(body))
                        {
                            var heartbeatResponse = System.Text.Json.JsonSerializer.Deserialize<HeartbeatResponse>(body,
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (heartbeatResponse != null)
                            {
                                LastHeartbeatResponse = heartbeatResponse;
                                LastHeartbeatResponseBody = body;
                                _logger?.LogDebug($"Heartbeat response: licenseMode={heartbeatResponse.LicenseMode}, sessions={heartbeatResponse.ActiveSessionCount}/{heartbeatResponse.MaxUsers}");
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger?.LogDebug($"Heartbeat response parse skipped: {parseEx.Message}");
                    }

                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Heartbeat failed: {sessionId} (Status: {response.StatusCode}, Body: {responseBody})");

                    // If the server reports the session no longer exists (e.g., DB cleanup,
                    // company-tenant mismatch), drop it from the confirmed set so subsequent
                    // heartbeats are skipped. The offline queue will retry session creation
                    // on its next tick.
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _confirmedSessions.Remove(sessionId);
                        _logger?.LogInfo($"Session {sessionId} no longer recognized by server — heartbeats paused until session is re-created via offline queue");
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Heartbeat HTTP error: {ex.Message}", ex);
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Heartbeat timeout: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Fetches all sessions from the backend API (GET /api/v1/Revit/session).
        /// Returns a list of SessionApiRequest DTOs from the API response.
        /// </summary>
        public async Task<List<SessionApiRequest>> FetchSessionsFromApiAsync()
        {
            try
            {
                _logger?.LogInfo("Fetching sessions from API...");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching sessions");
                    return new List<SessionApiRequest>();
                }

                var response = await _httpClient.GetAsync(UpdateEndpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch sessions failed: {response.StatusCode} - {responseBody}");
                    return new List<SessionApiRequest>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch sessions response length: {json.Length} chars");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<SessionApiRequest>? apiSessions = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<SessionApiRequest>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiSessions = JsonSerializer.Deserialize<List<SessionApiRequest>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiSessions = JsonSerializer.Deserialize<List<SessionApiRequest>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<SessionApiRequest>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            apiSessions = new List<SessionApiRequest> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize sessions API response: {ex.Message}", ex);
                    return new List<SessionApiRequest>();
                }

                if (apiSessions == null || apiSessions.Count == 0)
                {
                    _logger?.LogWarning("No sessions returned from API");
                    return new List<SessionApiRequest>();
                }

                _logger?.LogInfo($"Fetched {apiSessions.Count} sessions from API");
                return apiSessions;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch sessions from API: {ex.Message}", ex);
                return new List<SessionApiRequest>();
            }
        }

        /// <summary>
        /// Fetches a single session from the backend API by ID (GET /api/v1/Revit/session/{sessionId}).
        /// </summary>
        public async Task<SessionApiRequest?> FetchSessionByIdFromApiAsync(string sessionId)
        {
            try
            {
                _logger?.LogInfo($"Fetching session from API: {sessionId}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching session");
                    return null;
                }

                var endpoint = $"{UpdateEndpoint}/{sessionId}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch session failed: {response.StatusCode} - {responseBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                SessionApiRequest? apiSession = null;

                try
                {
                    apiSession = JsonSerializer.Deserialize<SessionApiRequest>(json, jsonOptions);
                }
                catch (JsonException)
                {
                    // Try wrapper object (e.g. {"success":true,"data":{...}})
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var dataElement))
                        {
                            apiSession = JsonSerializer.Deserialize<SessionApiRequest>(dataElement.GetRawText(), jsonOptions);
                        }
                    }
                    catch { }
                }

                if (apiSession == null)
                {
                    _logger?.LogWarning($"Session not found on API: {sessionId}");
                    return null;
                }

                _logger?.LogInfo($"Fetched session from API: {sessionId}");
                return apiSession;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch session from API: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Fetches sessions from API and stores new entries into local DB.
        /// Returns count of new sessions inserted.
        /// </summary>
        public async Task<int> FetchAndStoreSessionsFromApiAsync()
        {
            var insertedCount = 0;
            try
            {
                var apiSessions = await FetchSessionsFromApiAsync();

                foreach (var s in apiSessions)
                {
                    if (string.IsNullOrEmpty(s.SessionId)) continue;

                    var exists = await _sessionRepository.SessionExistsAsync(s.SessionId);
                    if (exists) continue;

                    var statusStr = s.Status ?? "Closed";
                    var isActive = statusStr.Equals("Active", StringComparison.OrdinalIgnoreCase)
                                || statusStr == "1";

                    var inserted = await _sessionRepository.InsertSessionFromApiAsync(
                        s.SessionId,
                        s.StartedAt?.ToString("o"),
                        s.EndedAt?.ToString("o"),
                        s.RevitVersion,
                        s.RevitBuild,
                        s.Username,
                        s.RevitUsername,
                        null, // userEmail — not in server response
                        s.ComputerName,
                        statusStr,
                        isActive);

                    if (inserted) insertedCount++;
                }

                _logger?.LogInfo($"Session fetch complete: {insertedCount} new session(s) stored from API");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch/store sessions from API: {ex.Message}", ex);
            }
            return insertedCount;
        }


        /// <summary>
        /// Extracts a single string claim from a JWT's payload (base64url-decoded second segment).
        /// Used purely for diagnostics — never for trust decisions. Returns null if anything fails.
        /// </summary>
        private static string? ExtractClaimFromJwt(string? jwt, string claimName)
        {
            if (string.IsNullOrWhiteSpace(jwt) || string.IsNullOrWhiteSpace(claimName)) return null;
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                var bytes = Convert.FromBase64String(payload);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(claimName, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    return v.GetString();
            }
            catch { /* diagnostic only */ }
            return null;
        }

        /// <summary>
        /// Queues a failed operation for later offline sync.
        /// </summary>
        private async Task QueueForOfflineSyncAsync(string sessionId, string endpoint, string httpMethod, object payload, string operationType)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = httpMethod,
                    Payload = payload,
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
                    priority: operationType == OfflineApiWrapper.OperationTypes.SessionOpen ? 1 : 3,
                    maxRetries: OfflineOperation.CriticalMaxRetries); // Session sync is critical — 20 retries (10 min)

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: {operationType} for session {sessionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Maps an internal integer status code to the API string representation.
        /// 0=Closed, 1=Active, 2=Crashed, 3=Inactive, 4=Unknown.
        /// </summary>
        /// <summary>
        /// Maps local DB status strings to the API's numeric status codes (as strings).
        /// API spec: status default "1". Values: 0=Closed, 1=Active, 2=Crashed, 3=Inactive, 4=Unknown.
        /// </summary>
        private static string MapStatusToString(string dbStatus)
        {
            return dbStatus switch
            {
                "Closed" => "0",
                "Active" => "1",
                "Crashed" => "2",
                "Inactive" => "3",
                "Unknown" => "4",
                _ => "1"
            };
        }

        /// <summary>
        /// Maps a RevitSession from SQLite to the API request DTO.
        /// Null database values are sent as null to the API (not empty strings).
        /// </summary>
        private static SessionApiRequest MapToApiRequest(RevitSession session)
        {
            var startedAtUtc = session.StartedAt.ToUniversalTime();

            // Match Swagger payload format exactly
            return new SessionApiRequest
            {
                SessionId = session.SessionId,
                MachineId = session.MachineId,
                ProcessId = session.ProcessId ?? 0,
                StartedAt = startedAtUtc,
                OpenedAt = (session.OpenedAt ?? session.StartedAt).ToUniversalTime(),
                OpeningDurationSeconds = session.OpeningDurationSeconds ?? 0,
                EndedAt = session.EndedAt?.ToUniversalTime(),
                ClosedAt = session.ClosedAt?.ToUniversalTime(),
                RevitVersion = session.RevitVersion ?? "2024",
                RevitBuild = session.RevitBuild ?? "Unknown",
                DesktopConnectorVersion = session.DesktopConnectorVersion ?? "Not Installed",
                BimanageVersion = session.BimanageVersion ?? "0.0.0.0",
                Username = session.Username ?? Environment.UserName,
                RevitUsername = session.RevitUsername ?? "Unknown",
                ComputerName = session.ComputerName ?? Environment.MachineName,
                AutodeskAddins = session.AutodeskAddins ?? 0,
                ExternalAddins = session.ExternalAddins ?? 0,
                LoadedPluginCount = session.LoadedPluginCount ?? 0,
                JournalFileName = session.JournalFileName ?? "unknown.txt",
                Status = MapStatusToString(session.Status ?? "Active"),
                TotalCommands = session.TotalCommands,
                TotalEvents = session.TotalEvents
            };
        }

        /// <summary>
        /// Reports detected unmonitored users to the backend API.
        /// The server endpoint accepts a single CreateUnmonitoredUserRequest per POST
        /// (see docs/API/bimanage-api.json → /api/v1/Revit/unmonitored-users), so we
        /// loop rather than send a list. A failure on one user must not drop the rest.
        ///
        /// Transient failures (transport exception, 5xx) for a given user are handed off
        /// to the offline queue so OfflineSyncProcessor will retry them on its next pass.
        /// 4xx is treated as permanent and not queued (re-trying won't make a 400 succeed).
        /// </summary>
        public async Task<bool> ReportUnmonitoredUsersAsync(List<UnmonitoredUserReport> users)
        {
            if (_httpClient == null || !_httpClient.IsAuthenticated
                || users == null || users.Count == 0)
                return false;

            const string endpoint = "/api/v1/Revit/unmonitored-users";
            int ok = 0, failedClient = 0, queued = 0;
            string lastError = null;

            foreach (var user in users)
            {
                try
                {
                    var response = await _httpClient.PostAsync(endpoint, user);
                    if (response.IsSuccessStatusCode)
                    {
                        ok++;
                        continue;
                    }

                    var statusCode = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync();
                    lastError = $"{statusCode} {response.StatusCode} — {body}";

                    if (statusCode >= 400 && statusCode < 500)
                    {
                        // Permanent client error (validation, auth, etc.) — queueing won't help.
                        failedClient++;
                    }
                    else
                    {
                        // 5xx server error — queue for retry so the detection isn't lost.
                        if (await TryQueueUnmonitoredUserAsync(endpoint, user, $"server {statusCode}"))
                            queued++;
                        else
                            failedClient++;
                    }
                }
                catch (Exception ex)
                {
                    // Walk inner exceptions so network-layer failures (SSL, DNS, socket reset,
                    // timeout) don't get hidden behind the generic "An error occurred while
                    // sending the request." top-level message. The root cause is almost always
                    // in the inner exception for HttpRequestException thrown from HttpClient.
                    lastError = BuildExceptionSummary(ex);
                    if (await TryQueueUnmonitoredUserAsync(endpoint, user, "transport exception"))
                        queued++;
                    else
                        failedClient++;
                }
            }

            var totalFailed = failedClient + queued;
            if (ok > 0)
            {
                var suffix = totalFailed > 0 ? $" ({queued} queued, {failedClient} dropped)" : string.Empty;
                _logger?.LogInfo($"Reported {ok} unmonitored user(s){suffix}");
            }
            else if (queued > 0 && failedClient == 0)
            {
                _logger?.LogInfo($"Queued {queued} unmonitored user report(s) for offline retry — {lastError}");
            }
            else if (totalFailed > 0)
            {
                _logger?.LogWarning(
                    $"Failed to report unmonitored users — {failedClient} dropped (4xx/no-queue), {queued} queued for retry. " +
                    $"Last error: {lastError}");
            }

            // Treat queued items as "handled" for the caller's bool — they will retry,
            // and the local detection row stays as the source of truth in any case.
            return failedClient == 0;
        }

        /// <summary>
        /// Hands a single failed unmonitored-user POST to the offline queue. Returns true
        /// when enqueueing succeeded (caller can count it as "handled"), false otherwise
        /// (caller should treat it as a permanent drop). The OfflineSyncProcessor will
        /// pick it up on its next pass and POST it via the same endpoint, so no dispatch
        /// changes are needed in the processor — the generic POST path handles it.
        /// </summary>
        private async Task<bool> TryQueueUnmonitoredUserAsync(
            string endpoint, UnmonitoredUserReport user, string reason)
        {
            if (_offlineQueue == null) return false;
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = "POST",
                    Payload = user,
                    CreatedAtUtc = DateTime.UtcNow
                };
                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                // SessionId field is the offline-queue's correlator slot; we have no session
                // affinity for an unmonitored-user report, so use the username as the key —
                // good enough to spot duplicates in the queue and to dedupe on inspection.
                var operation = OfflineOperation.Create(
                    sessionId: $"unmonitored:{user.Username}",
                    operationType: OfflineApiWrapper.OperationTypes.UnmonitoredUserReport,
                    operationData: jsonData,
                    priority: 5);
                await _offlineQueue.EnqueueAsync(operation);
                _logger?.LogDebug(
                    $"Queued unmonitored-user report for retry: {user.Username} on '{user.ModelName}' ({reason})");
                return true;
            }
            catch (Exception qx)
            {
                _logger?.LogWarning(
                    $"Failed to queue unmonitored-user report for {user.Username}: {qx.Message}");
                return false;
            }
        }

        /// <summary>
        /// Collapses an exception chain into a single human-readable string:
        /// "OuterType: outer.Message → InnerType: inner.Message → ...".
        /// Used to avoid silently losing the root cause when HttpClient wraps a socket
        /// or SSL failure inside a generic HttpRequestException with the unhelpful
        /// message "An error occurred while sending the request.".
        /// </summary>
        private static string BuildExceptionSummary(Exception ex)
        {
            if (ex == null) return "(null)";
            var sb = new System.Text.StringBuilder();
            var e = ex;
            int depth = 0;
            while (e != null && depth < 6)
            {
                if (depth > 0) sb.Append(" → ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }

    #region Session API Request DTO

    /// <summary>
    /// Request body for session data sync (used by both SignalR and HTTP POST).
    /// All fields nullable - null values from the database are sent as null.
    /// </summary>
    public class SessionApiRequest
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("machineId")]
        public string? MachineId { get; set; }

        [JsonPropertyName("processId")]
        public int? ProcessId { get; set; }

        [JsonPropertyName("startedAt")]
        public DateTime? StartedAt { get; set; }

        [JsonPropertyName("openedAt")]
        public DateTime? OpenedAt { get; set; }

        [JsonPropertyName("sessionOpeningDurationInSeconds")]
        public double? OpeningDurationSeconds { get; set; }

        [JsonPropertyName("revitVersion")]
        public string? RevitVersion { get; set; }

        [JsonPropertyName("revitBuild")]
        public string? RevitBuild { get; set; }

        [JsonPropertyName("desktopConnectorVersion")]
        public string? DesktopConnectorVersion { get; set; }

        [JsonPropertyName("bimanageVersion")]
        public string? BimanageVersion { get; set; }

        [JsonPropertyName("computerUserName")]
        public string? Username { get; set; }

        [JsonPropertyName("revitUsername")]
        public string? RevitUsername { get; set; }

        [JsonPropertyName("computerName")]
        public string? ComputerName { get; set; }

        [JsonPropertyName("autodeskAddins")]
        public int? AutodeskAddins { get; set; }

        [JsonPropertyName("externalAddins")]
        public int? ExternalAddins { get; set; }

        [JsonPropertyName("loadedPluginCount")]
        public int? LoadedPluginCount { get; set; }

        [JsonPropertyName("journalFileName")]
        public string? JournalFileName { get; set; }

        [JsonPropertyName("endedAt")]
        public DateTime? EndedAt { get; set; }

        [JsonPropertyName("closedAt")]
        public DateTime? ClosedAt { get; set; }

        /// <summary>
        /// Session status as string. API spec: CreateRevitSessionRequest.status.
        /// Values: "Closed" (0), "Active" (1), "Crashed" (2), "Inactive" (3), "Unknown" (4).
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "Active";

        [JsonPropertyName("totalCommands")]
        public int? TotalCommands { get; set; }

        [JsonPropertyName("totalEvents")]
        public int? TotalEvents { get; set; }
    }

    #endregion

    #region Session Update Request DTO

    /// <summary>
    /// Request body for session update via PATCH.
    /// Matches API spec: UpdateRevitSessionRequest (closedAt, endedAt, status).
    /// Used for session close, crash detection, or status changes.
    /// </summary>
    public class SessionUpdateRequest
    {
        [JsonPropertyName("closedAt")]
        public DateTime? ClosedAt { get; set; }

        [JsonPropertyName("endedAt")]
        public DateTime? EndedAt { get; set; }

        /// <summary>
        /// Session status as string. API spec: UpdateRevitSessionRequest.status.
        /// Values: "Closed" (0), "Active" (1), "Crashed" (2), "Inactive" (3), "Unknown" (4).
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    #endregion

    #region Heartbeat Request DTO

    /// <summary>
    /// Request body for session heartbeat via PATCH.
    /// PATCH /api/v1/Revit/session/{sessionId}/heartbeat
    /// Fields match the API schema exactly (bimanage-api.json HeartbeatRequest).
    /// </summary>
    public class HeartbeatRequest
    {
        [JsonPropertyName("lastHeartbeat")]
        public DateTime? LastHeartbeat { get; set; }

        /// <summary>API spec: float (nullable). Changed from int to double to match spec.</summary>
        [JsonPropertyName("memoryUsageMB")]
        public double? MemoryUsageMb { get; set; }

        [JsonPropertyName("cpuUsagePercent")]
        public double? CpuUsagePercent { get; set; }

        /// <summary>API spec: float (nullable). Changed from int to double to match spec.</summary>
        [JsonPropertyName("diskUsageMB")]
        public double? DiskUsageMb { get; set; }

        /// <summary>API spec: float (nullable). Changed from int to double to match spec.</summary>
        [JsonPropertyName("graphicsUsageMB")]
        public double? GraphicsUsageMb { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }
    }

    /// <summary>
    /// Response body from PATCH /api/v1/Revit/session/{sessionId}/heartbeat.
    /// Contains license mode and seat counts for license validation.
    /// </summary>
    public class HeartbeatResponse
    {
        [JsonPropertyName("sessionId")]
        public Guid SessionId { get; set; }

        [JsonPropertyName("lastHeartbeat")]
        public DateTime LastHeartbeat { get; set; }

        /// <summary>Session status from server: 0=Closed, 1=Active, 2=Crashed, etc.</summary>
        [JsonPropertyName("status")]
        public int Status { get; set; }

        /// <summary>Derived from Status field — true when Status == 1 (Active).</summary>
        [JsonIgnore]
        public bool IsActive => Status == 1;

        // The server actually sends this as "activeZeUserCount" in the heartbeat response
        // (verified in DeviceStatus heartbeat logs). The earlier "activeSessionCount" name
        // never matched, so the dialog always read 0.
        [JsonPropertyName("activeZeUserCount")]
        public int ActiveSessionCount { get; set; }

        [JsonPropertyName("maxUsers")]
        public int MaxUsers { get; set; }

        /// <summary>0 = RestrictionMode, 1 = ActiveMode, 2 = PassiveMode</summary>
        [JsonPropertyName("licenseMode")]
        public int LicenseMode { get; set; }

        /// <summary>Company display name (populated when server includes it in heartbeat response).</summary>
        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        /// <summary>List of enabled module IDs (populated when server includes it in heartbeat response).</summary>
        [JsonPropertyName("enabledModules")]
        public List<int>? EnabledModules { get; set; }
    }

    #endregion

    #region Unmonitored User Report DTO

    /// <summary>
    /// Report payload for a detected unmonitored user working without BIManage.
    /// </summary>
    public class UnmonitoredUserReport
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = "";

        [JsonPropertyName("modifiedBy")]
        public string ModifiedBy { get; set; } = "";
    }

    #endregion
}