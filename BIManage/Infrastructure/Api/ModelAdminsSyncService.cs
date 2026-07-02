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
    /// Syncs model admin relations to the backend.
    /// POST /api/v1/Revit/model-admins
    /// Supports SignalR primary, HTTP fallback, and offline queue.
    /// </summary>
    public class ModelAdminsSyncService
    {
        private readonly ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;

        private const string Endpoint = "/api/v1/Revit/project-model-admins/model";
        private const string HubMethod = "SendModelAdminData";

        public ModelAdminsSyncService(
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null)
        {
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _logger?.LogInfo($"ModelAdminsSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Fetches the list of project admins from
        /// GET /api/v1/Revit/project-model-admins/admins/{projectId}
        /// Returns an empty list if the call fails. Handles multiple server response shapes
        /// (wrapped in {"data":[]}, {"$values":[]}, or plain array) and multiple name-field variants.
        /// </summary>
        public async Task<List<ProjectAdminInfo>> GetProjectAdminsAsync(string projectId)
        {
            var result = new List<ProjectAdminInfo>();
            if (string.IsNullOrWhiteSpace(projectId) || _httpClient == null || !_httpClient.IsAuthenticated)
                return result;

            try
            {
                var url = $"/api/v1/Revit/project-model-admins/admins/{projectId}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning($"GetProjectAdminsAsync: {response.StatusCode} for project {projectId}");
                    return result;
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"GetProjectAdminsAsync raw response for project {projectId}:\n{json}");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Unwrap {"data":[...]} or {"$values":[...]} envelopes
                JsonElement arr = root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("data", out var dataEl)) arr = dataEl;
                    else if (root.TryGetProperty("$values", out var valuesEl)) arr = valuesEl;

                    // One more level for { data: { $values: [] } }
                    if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("$values", out var nestedValuesEl))
                        arr = nestedValuesEl;
                }

                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var info = ParseAdminElement(el);
                        if (info != null) result.Add(info);
                    }
                }
                else if (arr.ValueKind == JsonValueKind.Object)
                {
                    // Single-object response
                    var info = ParseAdminElement(arr);
                    if (info != null) result.Add(info);
                }

                _logger?.LogInfo($"Fetched {result.Count} project admin(s) for project {projectId}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"GetProjectAdminsAsync failed for project {projectId}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Robustly parses one admin JSON object. Accepts many possible field names for
        /// user identity: userId / id / adminUserId / userGuid, and for display:
        /// userName / username / displayName / fullName / name / firstName+lastName.
        /// </summary>
        private static ProjectAdminInfo? ParseAdminElement(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;

            string? GetStr(params string[] names)
            {
                foreach (var n in names)
                {
                    if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
                return null;
            }
            bool? GetBool(params string[] names)
            {
                foreach (var n in names)
                {
                    if (el.TryGetProperty(n, out var v))
                    {
                        if (v.ValueKind == JsonValueKind.True) return true;
                        if (v.ValueKind == JsonValueKind.False) return false;
                    }
                }
                return null;
            }

            var first = GetStr("firstName", "givenName");
            var last = GetStr("lastName", "familyName", "surname");
            var combined = (first != null || last != null) ? $"{first} {last}".Trim() : null;

            return new ProjectAdminInfo
            {
                UserId = GetStr("userId", "id", "adminUserId", "userGuid", "profileId"),
                UserName = GetStr("userName", "username", "user_name"),
                DisplayName = GetStr("displayName", "display_name", "fullName", "full_name", "name") ?? combined,
                Email = GetStr("email", "emailAddress", "email_address"),
                Role = GetStr("role", "roleName", "userRole"),
                IsActive = GetBool("isActive", "active", "enabled")
            };
        }

        /// <summary>
        /// Sync a model admin relation to the backend.
        /// Primary: SignalR two-way communication.
        /// Fallback: HTTP POST to /api/v1/Revit/model-admins.
        /// </summary>
        public async Task<bool> SyncModelAdminAsync(ModelAdminApiRequest request)
        {
            try
            {
                _logger?.LogInfo($"Syncing model admin: {request.ModelGuid} -> {request.AdminUserId}");

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
                    _logger?.LogInfo("Queued model admin for later sync");
                    return true;
                }

                _logger?.LogWarning("No sync transport available for model admin");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model admin sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends a SignalR notification for real-time updates (fire-and-forget, non-critical).
        /// This does NOT persist data — HTTP POST handles persistence.
        /// </summary>
        private async Task NotifyViaSignalRAsync(ModelAdminApiRequest request)
        {
            if (_signalRService == null || !_signalRService.IsConnected) return;
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.ModelRegistered, request);
                message.SenderSessionId = request.ModelGuid;
                message.SenderUsername = request.AdminUserId;
                await _signalRService.SendAsync(HubMethod, message);
                _logger?.LogDebug($"SignalR notification sent for model admin: {request.ModelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR notification failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        /// Sends model admin data via HTTP POST (fallback).
        /// If HTTP fails, queues to offline_queue for later retry.
        /// </summary>
        private async Task<bool> SyncViaHttpAsync(ModelAdminApiRequest request)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogDebug($"Model admin sync request payload:\n{jsonPayload}");

                var response = await _httpClient!.PostAsync(Endpoint, request);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Model admin synced via HTTP: {request.ModelGuid} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP model admin sync failed: (Status: {response.StatusCode}, Body: {responseBody})");

                    // Queue ALL failed responses for retry
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
                _logger?.LogError($"Model admin sync HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Model admin sync timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request);
                    return true;
                }
                return false;
            }
        }

        private async Task QueueForOfflineSyncAsync(ModelAdminApiRequest request)
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
                    sessionId: request.ModelGuid ?? Guid.NewGuid().ToString(),
                    operationType: OfflineApiWrapper.OperationTypes.ModelAdmins,
                    operationData: jsonData,
                    priority: 3); // Medium-high priority

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: model_admins for {request.ModelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline model admin operation: {ex.Message}", ex);
            }
        }
    }

    #region Model Admin API Request DTO

    /// <summary>
    /// Request body for POST /api/v1/Revit/project-model-admins/model
    /// API only requires modelGuid — admin is determined from the authenticated user's token.
    /// Extra fields kept for local context/logging only (ignored in serialization via WhenWritingNull).
    /// </summary>
    public class ModelAdminApiRequest
    {
        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        // --- Local context fields (not sent to API, used for logging/display) ---
        [JsonIgnore]
        public string? ModelName { get; set; }

        [JsonIgnore]
        public string? AdminUserId { get; set; }

        [JsonIgnore]
        public string? AdminUsername { get; set; }

        [JsonIgnore]
        public string? AdminEmail { get; set; }

        [JsonIgnore]
        public string? Role { get; set; }

        [JsonIgnore]
        public DateTime AssignedAt { get; set; }

        [JsonIgnore]
        public string? AssignedBy { get; set; }

        [JsonIgnore]
        public bool IsActive { get; set; }

        [JsonIgnore]
        public string? Permissions { get; set; }

        [JsonIgnore]
        public string? Notes { get; set; }
    }

    #endregion

    /// <summary>
    /// Lightweight DTO for GET /api/v1/Revit/project-model-admins/admins/{projectId}.
    /// Only fields we need in the UI are captured; others are ignored.
    /// </summary>
    public class ProjectAdminInfo
    {
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; }
        public bool? IsActive { get; set; }

        /// <summary>Best display label — falls back through available name fields.</summary>
        public string BestDisplayName =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
            : !string.IsNullOrWhiteSpace(UserName) ? UserName!
            : !string.IsNullOrWhiteSpace(Email) ? Email!
            : (UserId ?? "(unknown admin)");
    }
}
