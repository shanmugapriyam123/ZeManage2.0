using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Revit.PinProtection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs pin protection data to/from the backend API.
    /// GET    → /api/v1/Revit/pin-protections/by-model/{modelGuid}
    /// POST   → /api/v1/Revit/pin-protections
    /// DELETE → /api/v1/Revit/pin-protections/{pinProtectionId}
    /// GET    → /api/v1/Revit/pin-protections/{pinProtectionId}
    /// Fallback: offline queue when HTTP is unavailable.
    /// </summary>
    public class PinProtectionSyncService
    {
        private readonly PinProtectionRepository _pinProtectionRepository;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;
        private const string Endpoint = "/api/v1/Revit/pin-protections";

        public PinProtectionSyncService(
            PinProtectionRepository pinProtectionRepository,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null)
        {
            _pinProtectionRepository = pinProtectionRepository ?? throw new ArgumentNullException(nameof(pinProtectionRepository));
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _logger?.LogInfo($"PinProtectionSyncService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        #region GET by-model

        /// <summary>
        /// Fetches pin protections for a model via GET /api/v1/Revit/pin-protections/by-model/{modelGuid}.
        /// Persists fetched records to local database.
        /// </summary>
        public async Task<List<ProtectedPinInfo>> FetchPinProtectionsByModelAsync(string modelGuid, string projectName = null)
        {
            try
            {
                _logger?.LogInfo($"Fetching pin protections for model: {modelGuid}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching pin protections");
                    return new List<ProtectedPinInfo>();
                }

                var endpoint = $"{Endpoint}/by-model/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch pin protections failed: {response.StatusCode} - {responseBody}");
                    return new List<ProtectedPinInfo>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch pin protections response:\n{json}");

                var apiItems = ParseApiResponse<List<PinProtectionApiResponse>>(json);

                if (apiItems == null || apiItems.Count == 0)
                {
                    _logger?.LogInfo("No pin protections returned from API for this model");
                    return new List<ProtectedPinInfo>();
                }

                var results = new List<ProtectedPinInfo>();
                var savedCount = 0;

                foreach (var apiItem in apiItems)
                {
                    var info = MapFromApiResponse(apiItem);
                    results.Add(info);

                    // Persist to local database — pass sessionId from API response so it's preserved locally
                    try
                    {
                        var saved = await _pinProtectionRepository.SyncProtectionAsync(
                            modelGuid, projectName ?? string.Empty, info, "ApiSync",
                            sessionId: apiItem.SessionId);
                        if (saved) savedCount++;
                    }
                    catch (Exception saveEx)
                    {
                        _logger?.LogWarning($"Failed to save pin protection '{apiItem.PinProtectionId}' to local DB: {saveEx.Message}");
                    }
                }

                _logger?.LogInfo($"Fetched {results.Count} pin protections for model {modelGuid}, saved {savedCount} to local DB");

                // Reconcile: deactivate local pin protections not returned by server
                try
                {
                    var serverElementGuids = results
                        .Where(r => !string.IsNullOrEmpty(r.ElementGuid))
                        .Select(r => r.ElementGuid)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var localRecords = await _pinProtectionRepository.GetActivePinProtectionsByModelAsync(modelGuid);
                    var orphanCount = 0;
                    foreach (var (localGuid, localName) in localRecords)
                    {
                        if (!serverElementGuids.Contains(localGuid))
                        {
                            await _pinProtectionRepository.DeactivateProtectionAsync(
                                modelGuid, localGuid, "system", reason: "Removed on server (reconciliation)");
                            orphanCount++;
                            _logger?.LogInfo($"Reconciled: deactivated orphaned pin protection '{localName}' ({localGuid})");
                        }
                    }
                    if (orphanCount > 0)
                        _logger?.LogInfo($"Pin protection reconciliation: deactivated {orphanCount} orphaned record(s) for model {modelGuid}");
                }
                catch (Exception reconcileEx)
                {
                    _logger?.LogDebug($"Pin protection reconciliation failed: {reconcileEx.Message}");
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch pin protections: {ex.Message}", ex);
                return new List<ProtectedPinInfo>();
            }
        }

        #endregion

        #region GET by id

        /// <summary>
        /// Fetches a single pin protection via GET /api/v1/Revit/pin-protections/{pinProtectionId}.
        /// </summary>
        public async Task<ProtectedPinInfo?> FetchPinProtectionByIdAsync(string pinProtectionId)
        {
            try
            {
                _logger?.LogInfo($"Fetching pin protection: {pinProtectionId}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching pin protection");
                    return null;
                }

                var endpoint = $"{Endpoint}/{pinProtectionId}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch pin protection failed: {response.StatusCode} - {responseBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch pin protection response:\n{json}");

                var apiItem = ParseApiResponse<PinProtectionApiResponse>(json);
                if (apiItem == null)
                {
                    _logger?.LogWarning("No pin protection data in API response");
                    return null;
                }

                var result = MapFromApiResponse(apiItem);
                _logger?.LogInfo($"Fetched pin protection: {pinProtectionId} (element: {result.ElementGuid})");
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch pin protection by id: {ex.Message}", ex);
                return null;
            }
        }

        #endregion

        #region POST

        /// <summary>
        /// Syncs pin protections via POST /api/v1/Revit/pin-protections.
        /// Posts each protection individually (API expects a single object, not an array).
        /// </summary>
        public async Task<bool> SyncPinProtectionsToServerAsync(
            string modelGuid,
            List<ProtectedPinInfo> protections,
            string? sessionId = null,
            string? projectName = null,
            string? syncSource = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing {protections.Count} pin protections for model: {modelGuid}");

                var allSucceeded = true;
                foreach (var info in protections)
                {
                    // Use the local DB id as the server PinProtectionId so DELETE can find it later
                    var localId = await _pinProtectionRepository.GetPinProtectionIdAsync(modelGuid, info.ElementGuid);
                    var postItem = MapToPostRequest(info, modelGuid, sessionId, projectName, syncSource, localId);

                    if (_httpClient != null && _httpClient.IsAuthenticated)
                    {
                        var success = await PostSingleViaHttpAsync(postItem);
                        if (!success) allSucceeded = false;
                    }
                    else if (_offlineQueue != null)
                    {
                        await QueueForOfflinePostAsync(postItem);
                    }
                    else
                    {
                        _logger?.LogWarning("HTTP client not available and no offline queue for pin protection sync");
                        allSucceeded = false;
                    }
                }

                return allSucceeded;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Pin protection sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs a single pin protection to the server via POST.
        /// Convenience overload for single-element operations.
        /// </summary>
        public async Task<bool> SyncPinProtectionToServerAsync(
            string modelGuid,
            ProtectedPinInfo protection,
            string? sessionId = null,
            string? projectName = null,
            string? syncSource = null)
        {
            return await SyncPinProtectionsToServerAsync(
                modelGuid,
                new List<ProtectedPinInfo> { protection },
                sessionId,
                projectName,
                syncSource);
        }

        private async Task<bool> PostSingleViaHttpAsync(PinProtectionPostRequest postItem)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(postItem, GetJsonOptions());
                _logger?.LogInfo($"Pin protection POST to {Endpoint}:\n{jsonPayload}");

                var response = await _httpClient!.PostAsync(Endpoint, postItem);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Pin protection synced via HTTP POST: {postItem.ElementGuid} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Pin protection POST failed: {response.StatusCode} - {responseBody}");

                if (_offlineQueue != null)
                {
                    await QueueForOfflinePostAsync(postItem);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"HTTP POST pin protection failed: {ex.Message}", ex);

                if (_offlineQueue != null)
                {
                    await QueueForOfflinePostAsync(postItem);
                    return true;
                }

                return false;
            }
        }

        #endregion

        #region DELETE

        /// <summary>
        /// Deletes a pin protection via DELETE /api/v1/Revit/pin-protections/{pinProtectionId}.
        /// Looks up the local DB id (used as server PinProtectionId) from modelGuid + elementGuid.
        /// </summary>
        public async Task<bool> DeletePinProtectionAsync(string modelGuid, string elementGuid)
        {
            try
            {
                // Look up the local DB id which was used as the server's PinProtectionId during POST
                var pinProtectionId = await _pinProtectionRepository.GetPinProtectionIdAsync(modelGuid, elementGuid);
                if (string.IsNullOrEmpty(pinProtectionId))
                {
                    _logger?.LogWarning($"No local pin protection ID found for element {elementGuid} in model {modelGuid}, cannot delete from server");
                    return false;
                }

                _logger?.LogInfo($"Deleting pin protection via API: {pinProtectionId} (element: {elementGuid})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await DeleteViaHttpAsync(pinProtectionId);
                }

                _logger?.LogWarning("HTTP client not available or not authenticated for pin protection delete");

                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(pinProtectionId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Pin protection delete failed: {ex.Message}", ex);
                return false;
            }
        }

        private async Task<bool> DeleteViaHttpAsync(string pinProtectionId)
        {
            try
            {
                var endpoint = $"{Endpoint}/{pinProtectionId}";
                _logger?.LogInfo($"Pin protection DELETE request to {endpoint}");

                var response = await _httpClient!.DeleteAsync(endpoint);

                if (response.IsSuccessStatusCode || (int)response.StatusCode == 404)
                {
                    _logger?.LogInfo($"Pin protection deleted via HTTP DELETE: {pinProtectionId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Pin protection DELETE failed: {response.StatusCode} - {responseBody}");

                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(pinProtectionId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"HTTP DELETE pin protection failed: {ex.Message}", ex);

                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(pinProtectionId);
                    return true;
                }

                return false;
            }
        }

        #endregion

        #region Offline Queue

        private async Task QueueForOfflinePostAsync(PinProtectionPostRequest postItem)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = Endpoint,
                    HttpMethod = "POST",
                    Payload = postItem,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var opSessionId = postItem.SessionId ?? postItem.ModelGuid ?? "unknown";
                var operation = OfflineOperation.Create(
                    sessionId: opSessionId,
                    operationType: OfflineApiWrapper.OperationTypes.PinProtectionSync,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline POST for pin protection: {postItem.ElementGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline POST: {ex.Message}", ex);
            }
        }

        private async Task QueueForOfflineDeleteAsync(string pinProtectionId)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = $"{Endpoint}/{pinProtectionId}",
                    HttpMethod = "DELETE",
                    Payload = null,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: pinProtectionId,
                    operationType: OfflineApiWrapper.OperationTypes.PinProtectionDelete,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline DELETE for pin protection: {pinProtectionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline DELETE: {ex.Message}", ex);
            }
        }

        #endregion

        #region Mapping

        private ProtectedPinInfo MapFromApiResponse(PinProtectionApiResponse apiResponse)
        {
            var info = new ProtectedPinInfo
            {
                ElementGuid = apiResponse.ElementGuid ?? string.Empty,
                ElementId = apiResponse.ElementId,
                Name = apiResponse.ElementName ?? string.Empty,
                Category = apiResponse.ElementCategory,
                ProtectedBy = apiResponse.ProtectedBy ?? string.Empty,
                AdminComment = apiResponse.AdminComment ?? string.Empty,
                ProtectionMode = apiResponse.ProtectionMode,
                IsRequireCommentForUnpin = apiResponse.RequireCommentForUnpin,
                IsInstantlyNotifyOnUnpin = apiResponse.InstantlyNotifyOnUnpin
            };

            if (DateTime.TryParse(apiResponse.ProtectedAt, out var protectedAt))
                info.DateUTC = protectedAt;

            // Restore position if available
            if (apiResponse.OriginalPositionX.HasValue)
            {
                info.OriginalPositionX = apiResponse.OriginalPositionX;
                info.OriginalPositionY = apiResponse.OriginalPositionY;
                info.OriginalPositionZ = apiResponse.OriginalPositionZ;
            }

            return info;
        }

        private PinProtectionPostRequest MapToPostRequest(
            ProtectedPinInfo info,
            string modelGuid,
            string? sessionId = null,
            string? projectName = null,
            string? syncSource = null,
            string? pinProtectionId = null)
        {
            return new PinProtectionPostRequest
            {
                PinProtectionId = pinProtectionId ?? Guid.NewGuid().ToString(),
                ModelGuid = modelGuid,
                SessionId = sessionId,
                ProjectName = projectName,
                ElementGuid = info.ElementGuid,
                ElementId = (int)info.ElementId,
                ElementName = info.Name,
                ElementCategory = info.Category,
                ElementType = info.RevitElement?.GetType()?.Name,
                ProtectionMode = info.ProtectionMode,
                AdminComment = info.AdminComment,
                RequireCommentForUnpin = info.IsRequireCommentForUnpin,
                InstantlyNotifyOnUnpin = info.IsInstantlyNotifyOnUnpin,
                ProtectedBy = info.ProtectedBy,
                IsActive = true,
                SyncSource = syncSource ?? "RevitAddin",
                ProtectedAt = info.DateUTC?.ToString("o") ?? DateTime.UtcNow.ToString("o")
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Parses API response handling both { success, data: T } wrapper and raw T formats.
        /// </summary>
        private T? ParseApiResponse<T>(string json) where T : class
        {
            try
            {
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Standard wrapper: { "success": true, "data": ... }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement))
                {
                    return JsonSerializer.Deserialize<T>(dataElement.GetRawText(), jsonOptions);
                }

                // Raw response
                return JsonSerializer.Deserialize<T>(json, jsonOptions);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to parse API response: {ex.Message}", ex);
                return null;
            }
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };
        }

        #endregion
    }

    #region Pin Protection API DTOs

    /// <summary>
    /// DTO for GET /api/v1/Revit/pin-protections responses.
    /// Matches the backend response schema.
    /// </summary>
    public class PinProtectionApiResponse
    {
        [JsonPropertyName("pinProtectionId")]
        public string? PinProtectionId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("elementGuid")]
        public string? ElementGuid { get; set; }

        [JsonPropertyName("elementId")]
        public long ElementId { get; set; }

        [JsonPropertyName("elementName")]
        public string? ElementName { get; set; }

        [JsonPropertyName("elementCategory")]
        public string? ElementCategory { get; set; }

        [JsonPropertyName("protectionMode")]
        public int ProtectionMode { get; set; }

        [JsonPropertyName("protectedBy")]
        public string? ProtectedBy { get; set; }

        [JsonPropertyName("protectedAt")]
        public string? ProtectedAt { get; set; }

        [JsonPropertyName("adminComment")]
        public string? AdminComment { get; set; }

        [JsonPropertyName("requireCommentForUnpin")]
        public bool RequireCommentForUnpin { get; set; }

        [JsonPropertyName("instantlyNotifyOnUnpin")]
        public bool InstantlyNotifyOnUnpin { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("originalPositionX")]
        public double? OriginalPositionX { get; set; }

        [JsonPropertyName("originalPositionY")]
        public double? OriginalPositionY { get; set; }

        [JsonPropertyName("originalPositionZ")]
        public double? OriginalPositionZ { get; set; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("updatedBy")]
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// DTO for POST /api/v1/Revit/pin-protections.
    /// Matches the server API schema exactly.
    /// </summary>
    public class PinProtectionPostRequest
    {
        [JsonPropertyName("pinProtectionId")]
        public string? PinProtectionId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("projectName")]
        public string? ProjectName { get; set; }

        [JsonPropertyName("elementGuid")]
        public string? ElementGuid { get; set; }

        [JsonPropertyName("elementId")]
        public int ElementId { get; set; }

        [JsonPropertyName("elementName")]
        public string? ElementName { get; set; }

        [JsonPropertyName("elementCategory")]
        public string? ElementCategory { get; set; }

        [JsonPropertyName("elementType")]
        public string? ElementType { get; set; }

        [JsonPropertyName("protectionMode")]
        public int ProtectionMode { get; set; }

        [JsonPropertyName("adminComment")]
        public string? AdminComment { get; set; }

        [JsonPropertyName("requireCommentForUnpin")]
        public bool RequireCommentForUnpin { get; set; }

        [JsonPropertyName("instantlyNotifyOnUnpin")]
        public bool InstantlyNotifyOnUnpin { get; set; }

        [JsonPropertyName("protectedBy")]
        public string? ProtectedBy { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("syncSource")]
        public string? SyncSource { get; set; }

        [JsonPropertyName("protectedAt")]
        public string? ProtectedAt { get; set; }
    }

    #endregion
}
