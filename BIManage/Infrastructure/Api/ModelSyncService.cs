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
    /// Syncs local model registration data to the backend via HTTP POST.
    /// Reads from SQLite RegisteredModelsRepository and sends to /api/v1/Revit/models/register.
    /// Queues to offline_queue if API is unavailable.
    /// </summary>
    public class ModelSyncService
    {
        private readonly RegisteredModelsRepository _modelRepository;
        private readonly ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;
        private const string Endpoint = "/api/v1/Revit/models/register";
        private const string GetEndpoint = "/api/v1/Revit/models";
        // Project-entity endpoints — model.projectId points at one of these. The model's
        // own localProjectName / projectName fields are a SNAPSHOT taken at registration
        // time (from Revit ProjectInformation.Name) and never refresh. The project
        // entity's name field IS what the web dashboard reads — so the dialog must
        // resolve via projectId, not via the model record's stale cached strings.
        // Several candidate paths because the backend has used different conventions
        // across releases; we try each in order until one returns 200 OK.
        private static readonly string[] ProjectGetEndpointCandidates = new[]
        {
            "/api/v1/Revit/projects/{0}",
            "/api/v1/Revit/Projects/{0}",
            "/api/v1/projects/{0}",
            "/api/v1/Projects/{0}",
            "/api/v1/Revit/project/{0}",
            "/api/v1/project/{0}",
            "/api/v1/Revit/projects/details/{0}",
            "/api/v1/Revit/projects/byId/{0}",
            "/api/v1/Revit/projects/get/{0}",
        };
        private const string HubMethod = "SendModelData";

        /// <summary>
        /// Creates a ModelSyncService with SignalR and optional HTTP fallback.
        /// </summary>
        public ModelSyncService(
            RegisteredModelsRepository modelRepository,
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null)
        {
            _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _logger?.LogInfo($"ModelSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Syncs model registration data to the backend.
        /// Primary: SignalR two-way communication.
        /// Fallback: HTTP POST to /api/v1/Revit/models/register.
        /// Returns a structured outcome so callers can distinguish
        /// "server accepted" from "queued for retry" from "server rejected".
        /// The old bool-returning signature was hiding tenant-not-provisioned 400s
        /// behind a successful return value, which caused the UI to flip to
        /// "registered" state while the server had actually refused the POST.
        /// </summary>
        public async Task<ModelSyncResult> SyncModelAsync(string modelGuid)
        {
            try
            {
                _logger?.LogInfo($"Syncing model: {modelGuid}");

                // Read model data from SQLite
                var model = await _modelRepository.GetModelAsync(modelGuid);
                if (model == null)
                {
                    _logger?.LogWarning($"Model not found in database: {modelGuid}");
                    return ModelSyncResult.NoTransport("Model not found in local database");
                }

                // Map to API request DTO
                var apiRequest = MapToApiRequest(model);

                // HTTP POST first for guaranteed server-side persistence
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var httpResult = await SyncViaHttpAsync(modelGuid, apiRequest);
                    if (httpResult.ServerAccepted)
                    {
                        // Also notify via SignalR for real-time updates (fire-and-forget, non-critical)
                        await NotifyViaSignalRAsync(modelGuid, apiRequest);
                    }
                    return httpResult;
                }

                if (_httpClient != null && !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP skipped - no auth tokens available (register device first)");
                }

                // Queue for later if no authenticated client
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(modelGuid, apiRequest);
                    _logger?.LogInfo($"Queued model for later sync: {modelGuid}");
                    return ModelSyncResult.Queued();
                }

                _logger?.LogWarning("No sync transport available for model");
                return ModelSyncResult.NoTransport("No HTTP client and no offline queue");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model sync failed: {ex.Message}", ex);
                return ModelSyncResult.NoTransport(ex.Message);
            }
        }

        /// <summary>
        /// Sends a SignalR notification for real-time updates (fire-and-forget, non-critical).
        /// This does NOT persist data — HTTP POST handles persistence.
        /// </summary>
        private async Task NotifyViaSignalRAsync(string modelGuid, ModelApiRequest apiRequest)
        {
            if (_signalRService == null || !_signalRService.IsConnected) return;
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.ModelRegistered, apiRequest);
                message.SenderSessionId = modelGuid;
                message.SenderUsername = apiRequest.RegisteredBy;
                await _signalRService.SendAsync(HubMethod, message);
                _logger?.LogDebug($"SignalR notification sent for model: {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR notification failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        /// Sends model data via HTTP POST (fallback).
        /// If HTTP fails, queues to offline_queue for later retry.
        /// </summary>
        private async Task<ModelSyncResult> SyncViaHttpAsync(string modelGuid, ModelApiRequest apiRequest)
        {
            try
            {
                // Log actual payload being sent for debugging
                var payloadJson = JsonSerializer.Serialize(apiRequest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogInfo($"Model register request payload:\n{payloadJson}");

                var response = await _httpClient!.PostAsync(Endpoint, apiRequest);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Model synced via HTTP: {modelGuid} (Status: {response.StatusCode})");
                    return ModelSyncResult.ServerAcceptedResult((int)response.StatusCode);
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var statusCode = (int)response.StatusCode;
                _logger?.LogWarning($"HTTP model sync failed: {modelGuid} (Status: {response.StatusCode}, Body: {responseBody})");

                // Client errors (400, 403, 409) are permanent — don't queue, surface to caller so UI can reflect real state.
                if (statusCode >= 400 && statusCode < 500)
                {
                    var isNotProvisioned = !string.IsNullOrEmpty(responseBody)
                        && responseBody.IndexOf("No default project found for this company",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isNotProvisioned)
                    {
                        _logger?.LogWarning($"Model register rejected: tenant not provisioned (no default project). Admin must configure the company before any model can be registered. Model: {modelGuid}");
                    }
                    else
                    {
                        _logger?.LogWarning($"Model register client error {response.StatusCode} for {modelGuid} — not queuing (check server provisioning/config)");
                    }
                    return ModelSyncResult.RejectedByServer(statusCode, responseBody, isNotProvisioned);
                }

                // Server errors (5xx) — queue for retry.
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(modelGuid, apiRequest);
                    return ModelSyncResult.Queued(statusCode, responseBody);
                }
                return ModelSyncResult.NoTransport($"Server {statusCode} and no offline queue: {responseBody}");
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Model sync HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(modelGuid, apiRequest);
                    return ModelSyncResult.Queued(errorMessage: ex.Message);
                }
                return ModelSyncResult.NoTransport(ex.Message);
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Model sync timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(modelGuid, apiRequest);
                    return ModelSyncResult.Queued(errorMessage: ex.Message);
                }
                return ModelSyncResult.NoTransport(ex.Message);
            }
        }

        /// <summary>
        /// Queues a failed model sync operation for later offline sync.
        /// </summary>
        private async Task QueueForOfflineSyncAsync(string modelGuid, ModelApiRequest apiRequest)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = Endpoint,
                    HttpMethod = "POST",
                    Payload = apiRequest,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: modelGuid,
                    operationType: OfflineApiWrapper.OperationTypes.ModelRegister,
                    operationData: jsonData,
                    priority: 2); // Model registration is high priority

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: model_register for model {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline model operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fetches all registered models from the API and inserts only new ones into local SQLite.
        /// Skips models that already exist in the local database (no duplicates).
        /// Returns the number of new models inserted.
        /// </summary>
        public async Task<int> FetchModelsFromApiAsync()
        {
            int insertedCount = 0;

            try
            {
                if (_httpClient == null)
                {
                    _logger?.LogWarning("Cannot fetch models from API - HTTP client not available");
                    return 0;
                }

                if (!_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("Cannot fetch models from API - not authenticated (no token and no stored refresh token)");
                    return 0;
                }

                _logger?.LogInfo($"Fetching registered models from API: GET {GetEndpoint}");

                var response = await _httpClient.GetAsync(GetEndpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Failed to fetch models from API (Status: {response.StatusCode}, Body: {body})");
                    return 0;
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogInfo($"API response received ({json.Length} chars): {(json.Length > 500 ? json.Substring(0, 500) + "..." : json)}");

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                List<ModelApiResponse> apiModels = null;

                try
                {
                    // API returns {"success":true,"data":[...]} wrapper — try extracting from wrapper first
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiModels = JsonSerializer.Deserialize<List<ModelApiResponse>>(dataElement.GetRawText(), options);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiModels = JsonSerializer.Deserialize<List<ModelApiResponse>>(valuesElement.GetRawText(), options);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        // Direct array format
                        apiModels = JsonSerializer.Deserialize<List<ModelApiResponse>>(json, options);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        // Single object instead of array
                        var single = JsonSerializer.Deserialize<ModelApiResponse>(root.GetRawText(), options);
                        if (single != null)
                            apiModels = new List<ModelApiResponse> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize models API response: {ex.Message}", ex);
                    return 0;
                }

                if (apiModels == null || apiModels.Count == 0)
                {
                    _logger?.LogInfo("No models returned from API (list is null or empty after deserialization)");
                    return 0;
                }

                _logger?.LogInfo($"Fetched {apiModels.Count} models from API, checking for new entries...");

                foreach (var apiModel in apiModels)
                {
                    if (string.IsNullOrEmpty(apiModel.ModelGuid))
                    {
                        _logger?.LogDebug($"Skipping model with empty ModelGuid (Name: {apiModel.ModelName})");
                        continue;
                    }

                    _logger?.LogDebug($"Processing API model: {apiModel.ModelName} (GUID: {apiModel.ModelGuid})");

                    // Check if model already exists in local DB
                    var (exists, _) = await _modelRepository.GetModelRegistrationStatusAsync(apiModel.ModelGuid);

                    if (exists)
                    {
                        _logger?.LogDebug($"Model already exists locally, skipping: {apiModel.ModelName} ({apiModel.ModelGuid})");
                        continue;
                    }

                    // Map API response to domain model and insert
                    var localModel = MapFromApiResponse(apiModel);
                    _logger?.LogDebug($"Inserting new model: {localModel.ModelName} (GUID: {localModel.ModelGuid}, Path: {localModel.CentralModelPath})");

                    var success = await _modelRepository.RegisterModelAsync(localModel);

                    if (success)
                    {
                        insertedCount++;
                        _logger?.LogInfo($"Inserted new model from API: {localModel.ModelName} ({localModel.ModelGuid})");
                    }
                    else
                    {
                        _logger?.LogWarning($"Failed to insert model: {localModel.ModelName} ({localModel.ModelGuid})");
                    }
                }

                _logger?.LogInfo($"Model fetch complete: {insertedCount} new model(s) inserted, {apiModels.Count - insertedCount} already existed");
                return insertedCount;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"HTTP error fetching models: {ex.Message}", ex);
                return insertedCount;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Timeout fetching models: {ex.Message}", ex);
                return insertedCount;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch models from API: {ex.Message}", ex);
                return insertedCount;
            }
        }

        /// <summary>
        /// Fetches a single model from the API by modelGuid (GET /api/v1/Revit/models/{modelGuid}).
        /// </summary>
        public async Task<ModelApiResponse?> FetchModelByGuidAsync(string modelGuid)
        {
            try
            {
                _logger?.LogInfo($"Fetching model from API: {modelGuid}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching model");
                    return null;
                }

                var endpoint = $"{GetEndpoint}/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch model failed: {response.StatusCode} - {responseBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                // Promote to Info level so the user can grep [FullModelJson] in the log to
                // see EVERY field the backend returns — used to find any project-name field
                // we might not be deserializing yet. Truncated to 2000 chars so it doesn't
                // blow up the log on huge responses.
                _logger?.LogInfo($"[FullModelJson] /models/{modelGuid} returned ({json.Length} chars): {(json.Length > 2000 ? json.Substring(0, 2000) + "...(truncated)" : json)}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                ModelApiResponse? apiModel = null;

                try
                {
                    // Server returns { success, message, data: { ...model... } } — extract from "data" first
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
                    {
                        var dataJson = dataElement.GetRawText();
                        _logger?.LogDebug($"FetchModelByGuid data element ({dataJson.Length} chars): {(dataJson.Length > 500 ? dataJson.Substring(0, 500) + "..." : dataJson)}");
                        apiModel = JsonSerializer.Deserialize<ModelApiResponse>(dataJson, jsonOptions);
                    }
                    else
                    {
                        // Fallback: try direct deserialization (flat response without wrapper)
                        _logger?.LogDebug("FetchModelByGuid: no 'data' wrapper — deserializing raw response");
                        apiModel = JsonSerializer.Deserialize<ModelApiResponse>(json, jsonOptions);
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger?.LogWarning($"FetchModelByGuid: primary deserialization failed: {jsonEx.Message}");
                    try
                    {
                        apiModel = JsonSerializer.Deserialize<ModelApiResponse>(json, jsonOptions);
                    }
                    catch { }
                }

                if (apiModel == null)
                {
                    _logger?.LogWarning($"Model not found on API: {modelGuid}");
                    return null;
                }

                _logger?.LogInfo($"Fetched model from API: {modelGuid} (Name={apiModel.ModelName}, IsActive={apiModel.IsActive})");
                return apiModel;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch model from API: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Resolves a project's CURRENT name from its GUID by calling the project-entity
        /// endpoint. This is what the ZeManage web dashboard shows — distinct from the
        /// model record's <c>LocalProjectName</c>, which is a frozen snapshot captured
        /// from Revit ProjectInformation at registration time and never refreshes.
        ///
        /// Tries each path in <see cref="ProjectGetEndpointCandidates"/> in order until
        /// one returns 200 OK with a parseable name field. Walks the common response
        /// shapes the backend has used: <c>name</c>, <c>projectName</c>, <c>localProjectName</c>,
        /// or those same fields nested under a <c>data</c> wrapper.
        ///
        /// Returns null when no candidate succeeded or no name field was present.
        /// </summary>
        public async Task<string?> FetchProjectNameByIdAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId)) return null;
            if (_httpClient == null || !_httpClient.IsAuthenticated)
            {
                _logger?.LogDebug($"FetchProjectNameByIdAsync: HTTP client not available/authenticated for project {projectId}");
                return null;
            }

            foreach (var template in ProjectGetEndpointCandidates)
            {
                var url = string.Format(template, projectId);
                try
                {
                    _logger?.LogInfo($"[ProjectLookup] Trying {url}");
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"[ProjectLookup] {url} → HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    _logger?.LogDebug($"[ProjectLookup] {url} raw ({json.Length} chars): {(json.Length > 400 ? json.Substring(0, 400) + "..." : json)}");

                    var name = ExtractProjectNameFromJson(json);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _logger?.LogInfo($"[ProjectLookup] {url} → project name '{name}'");
                        return name;
                    }
                    _logger?.LogInfo($"[ProjectLookup] {url} succeeded but no name field found in response — trying next candidate");
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[ProjectLookup] {url} threw: {ex.GetType().Name}: {ex.Message} — trying next candidate");
                }
            }

            _logger?.LogWarning($"[ProjectLookup] All endpoint candidates exhausted for project {projectId} — no name resolved");
            return null;
        }

        /// <summary>
        /// Resolves a project's CURRENT name keyed by the MODEL'S GUID — calls
        /// GET /api/v1/Revit/projects/by-model/{modelGuid}. This is the endpoint the
        /// Session Information dialog should use because:
        ///   1. It's keyed by modelGuid, which we always have (no need to first fetch
        ///      the model and read its projectId).
        ///   2. It returns the project's CURRENT name as seen on the web dashboard
        ///      under <c>data.projectName</c> — distinct from the model record's
        ///      stale <c>localProjectName</c> snapshot.
        ///   3. Verified by user against Swagger (29 May 2026): returns
        ///      <c>{ success, data: { projectId, projectName, projectNumber } }</c>.
        ///
        /// Returns null when the call fails or the field is missing.
        /// </summary>
        public async Task<string?> FetchProjectNameByModelGuidAsync(string modelGuid)
        {
            if (string.IsNullOrWhiteSpace(modelGuid)) return null;
            if (_httpClient == null || !_httpClient.IsAuthenticated)
            {
                _logger?.LogDebug($"FetchProjectNameByModelGuidAsync: HTTP client not available/authenticated for model {modelGuid}");
                return null;
            }

            var url = $"/api/v1/Revit/projects/by-model/{modelGuid}";
            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug($"FetchProjectNameByModelGuidAsync: {url} → {(int)response.StatusCode} {response.StatusCode}");
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogInfo($"[ProjectByModel] {url} returned ({json.Length} chars): {(json.Length > 600 ? json.Substring(0, 600) + "...(truncated)" : json)}");
                var name = ExtractProjectNameFromJson(json);
                if (!string.IsNullOrWhiteSpace(name))
                    _logger?.LogInfo($"[ProjectByModel] Resolved projectName='{name}' for modelGuid={modelGuid}");
                return name;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"FetchProjectNameByModelGuidAsync exception for {modelGuid}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Walks a project-endpoint JSON response and pulls the project's display name
        /// out of whichever field the backend used. Supports: top-level <c>name</c>,
        /// <c>projectName</c>, <c>localProjectName</c>; the same fields nested under
        /// <c>data</c>; and the same nested under <c>data.project</c>. First non-empty
        /// match wins. Returns null on parse failure or when no candidate field is present.
        /// </summary>
        private static string? ExtractProjectNameFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Direct: { "name": "...", ... }
                string? direct = ReadFirstString(root, "name", "projectName", "localProjectName");
                if (!string.IsNullOrWhiteSpace(direct)) return direct;

                // Wrapped: { "data": { "name": "...", ... } }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.Object)
                    {
                        var wrapped = ReadFirstString(dataEl, "name", "projectName", "localProjectName");
                        if (!string.IsNullOrWhiteSpace(wrapped)) return wrapped;

                        // Double-wrapped: { "data": { "project": { "name": "..." } } }
                        if (dataEl.TryGetProperty("project", out var projectEl)
                            && projectEl.ValueKind == JsonValueKind.Object)
                        {
                            var nested = ReadFirstString(projectEl, "name", "projectName", "localProjectName");
                            if (!string.IsNullOrWhiteSpace(nested)) return nested;
                        }
                    }
                }
            }
            catch { /* parse failure — return null so caller falls back */ }
            return null;
        }

        private static string? ReadFirstString(JsonElement el, params string[] fieldNames)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var field in fieldNames)
            {
                if (el.TryGetProperty(field, out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Deactivates a model via PATCH /api/v1/Revit/models/{modelGuid}/deactivate.
        /// Queues to offline_queue if API is unavailable.
        /// </summary>
        public async Task<bool> DeactivateModelAsync(string modelGuid, string deactivatedBy, string deactivationReason)
        {
            try
            {
                _logger?.LogInfo($"Deactivating model: {modelGuid}");

                var endpoint = $"{GetEndpoint}/{modelGuid}/deactivate";
                var request = new ModelDeactivateRequest
                {
                    DeactivatedBy = deactivatedBy,
                    DeactivationReason = deactivationReason
                };

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    try
                    {
                        var response = await _httpClient.PatchAsync(endpoint, request);

                        if (response.IsSuccessStatusCode)
                        {
                            _logger?.LogInfo($"Model deactivated: {modelGuid} (Status: {response.StatusCode})");
                            return true;
                        }

                        var responseBody = await response.Content.ReadAsStringAsync();
                        _logger?.LogWarning($"Model deactivation failed: {modelGuid} (Status: {response.StatusCode}, Body: {responseBody})");

                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelDeactivate);
                            return true;
                        }
                        return false;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger?.LogError($"Model deactivation HTTP error: {ex.Message}", ex);
                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelDeactivate);
                            return true;
                        }
                        return false;
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger?.LogError($"Model deactivation timeout: {ex.Message}", ex);
                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelDeactivate);
                            return true;
                        }
                        return false;
                    }
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelDeactivate);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for model deactivation");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model deactivation failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Activates a model via PATCH /api/v1/Revit/models/{modelGuid}/activate.
        /// Queues to offline_queue if API is unavailable.
        /// </summary>
        public async Task<bool> ActivateModelAsync(string modelGuid)
        {
            try
            {
                _logger?.LogInfo($"Activating model: {modelGuid}");

                var endpoint = $"{GetEndpoint}/{modelGuid}/activate";
                var request = new { modelGuid = modelGuid };

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    try
                    {
                        var response = await _httpClient.PatchAsync(endpoint, null);

                        if (response.IsSuccessStatusCode)
                        {
                            _logger?.LogInfo($"Model activated: {modelGuid} (Status: {response.StatusCode})");
                            return true;
                        }

                        var responseBody = await response.Content.ReadAsStringAsync();
                        _logger?.LogWarning($"Model activation failed: {modelGuid} (Status: {response.StatusCode}, Body: {responseBody})");

                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelActivate);
                            return true;
                        }
                        return false;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger?.LogError($"Model activation HTTP error: {ex.Message}", ex);
                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelActivate);
                            return true;
                        }
                        return false;
                    }
                    catch (TaskCanceledException ex)
                    {
                        _logger?.LogError($"Model activation timeout: {ex.Message}", ex);
                        if (_offlineQueue != null)
                        {
                            await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelActivate);
                            return true;
                        }
                        return false;
                    }
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueOperationAsync(modelGuid, endpoint, "PATCH", request, OfflineApiWrapper.OperationTypes.ModelActivate);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for model activation");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Model activation failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Queues a failed operation for later offline sync.
        /// </summary>
        private async Task QueueOperationAsync(string modelGuid, string endpoint, string httpMethod, object payload, string operationType)
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
                    sessionId: modelGuid,
                    operationType: operationType,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: {operationType} for model {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline model operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Maps an API response DTO back to a RegisteredModel domain object for local storage.
        /// </summary>
        private static RegisteredModel MapFromApiResponse(ModelApiResponse apiResponse)
        {
            DateTime registeredAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(apiResponse.RegisteredAt))
                DateTime.TryParse(apiResponse.RegisteredAt, out registeredAt);

            DateTime? lastOpenedAt = null;
            if (!string.IsNullOrEmpty(apiResponse.LastOpenedAt))
            {
                if (DateTime.TryParse(apiResponse.LastOpenedAt, out var parsed))
                    lastOpenedAt = parsed;
            }

            return new RegisteredModel
            {
                ModelGuid = apiResponse.ModelGuid ?? "",
                ModelName = apiResponse.ModelName ?? "Unknown Model",
                CentralModelPath = apiResponse.CentralModelPath,
                ProjectName = apiResponse.LocalProjectName,
                ZemanageProjectId = apiResponse.ZemanageProjectId ?? apiResponse.ProjectIdAlternate,
                IsActive = apiResponse.IsActive,
                RegisteredAt = registeredAt,
                RegisteredBy = apiResponse.RegisteredBy ?? "API_SYNC",
                IsWorkshared = apiResponse.IsWorkshared,
                IsFamily = apiResponse.IsFamily,
                IsCloudModel = apiResponse.IsCloudModel,
                Notes = apiResponse.Notes,
                LastOpenedAt = lastOpenedAt,
                LastOpenedBy = apiResponse.LastOpenedBy
            };
        }

        /// <summary>
        /// Maps a RegisteredModel from SQLite to the API request DTO.
        /// Matches Swagger payload format exactly.
        /// </summary>
        private static ModelApiRequest MapToApiRequest(RegisteredModel model)
        {
            var registeredAtUtc = model.RegisteredAt.ToUniversalTime();

            // Convert model identifier to proper GUID format for API
            var modelGuidForApi = ConvertToGuid(model.ModelGuid);

            // Resolve projectId: prefer CloudProjectId (Revit API GUID) → ZemanageProjectId → path fallback
            var projectId = ConvertToGuidOrNull(model.CloudProjectId)
                         ?? ConvertToGuidOrNull(model.ZemanageProjectId);
            if (string.IsNullOrEmpty(projectId))
            {
                projectId = GenerateProjectIdFromPath(model.CentralModelPath);
            }

            return new ModelApiRequest
            {
                ModelGuid = modelGuidForApi,
                ModelName = model.ModelName ?? "Unknown Model",
                CentralModelPath = model.CentralModelPath ?? "Local File",
                LocalProjectName = model.ProjectName ?? "",
                ProjectId = projectId,
                ProjectName = model.ProjectName ?? "Unknown Project",
                IsActive = model.IsActive,
                RegisteredAt = registeredAtUtc,
                RegisteredBy = model.RegisteredBy ?? "System",
                Notes = model.Notes ?? "",
                LastOpenedAt = model.LastOpenedAt?.ToUniversalTime(),
                LastOpenedBy = model.LastOpenedBy ?? "",
                CreatedAt = registeredAtUtc,
                IsWorkshared = model.IsWorkshared,
                IsFamily = model.IsFamily,
                IsCloudModel = model.IsCloudModel,
                IsLocalModel = model.IsLocalCopy
            };
        }

        /// <summary>
        /// Generates a deterministic project ID from the model's parent folder path.
        /// Same logic as Application.GetLocalProjectId — groups models by folder.
        /// </summary>
        private static string? GenerateProjectIdFromPath(string? modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
                return null;

            var folder = System.IO.Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(folder))
                return null;

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
                return new Guid(hash).ToString();
            }
        }

        /// <summary>
        /// Converts a model identifier string to a proper GUID format.
        /// If already a valid GUID, returns as-is. Otherwise generates a deterministic GUID from the string.
        /// </summary>
        private static string ConvertToGuid(string modelIdentifier)
        {
            if (string.IsNullOrEmpty(modelIdentifier))
                return Guid.Empty.ToString();

            // Try parsing as existing GUID
            if (Guid.TryParse(modelIdentifier, out var existingGuid))
                return existingGuid.ToString();

            // Generate deterministic GUID from string using MD5 hash
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.UTF8.GetBytes(modelIdentifier);
                var hashBytes = md5.ComputeHash(inputBytes);
                var deterministicGuid = new Guid(hashBytes);
                return deterministicGuid.ToString();
            }
        }

        /// <summary>
        /// Converts a string to a GUID format, or returns null if invalid/empty.
        /// Used for optional GUID fields like cloudProjectId and zemanageProjectId.
        /// </summary>
        private static string? ConvertToGuidOrNull(string? value)
        {
            // Return null for null, empty, or placeholder values
            if (string.IsNullOrEmpty(value) || value == "string")
                return null;

            // Try parsing as existing GUID
            if (Guid.TryParse(value, out var existingGuid))
                return existingGuid.ToString();

            // Generate deterministic GUID from string using MD5 hash
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var inputBytes = System.Text.Encoding.UTF8.GetBytes(value);
                var hashBytes = md5.ComputeHash(inputBytes);
                var deterministicGuid = new Guid(hashBytes);
                return deterministicGuid.ToString();
            }
        }
    }

    #region Model Sync Result

    /// <summary>
    /// Outcome of a model registration sync attempt.
    /// Callers use this to decide UI state (ribbon button green vs warning)
    /// instead of treating any non-exception path as success.
    /// </summary>
    public enum ModelSyncOutcome
    {
        /// <summary>Server accepted the POST (2xx). Model is registered on the server under our tenant.</summary>
        ServerAccepted,
        /// <summary>Transient failure (network / 5xx). Request is queued for retry.</summary>
        Queued,
        /// <summary>Server rejected the request with a 4xx. Permanent until the caller changes something.</summary>
        RejectedByServer,
        /// <summary>No HTTP client and no offline queue available.</summary>
        NoTransport
    }

    /// <summary>
    /// Structured result from <see cref="ModelSyncService.SyncModelAsync"/>.
    /// </summary>
    public class ModelSyncResult
    {
        public ModelSyncOutcome Outcome { get; set; }
        public int? StatusCode { get; set; }
        public string? ResponseBody { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// True when the server rejected the POST because the tenant has no default project
        /// (server body contains "No default project found for this company"). Surface this
        /// in the UI so users know to contact their admin instead of retrying endlessly.
        /// </summary>
        public bool IsTenantNotProvisioned { get; set; }

        public bool ServerAccepted => Outcome == ModelSyncOutcome.ServerAccepted;
        public bool WasQueued       => Outcome == ModelSyncOutcome.Queued;
        public bool Rejected        => Outcome == ModelSyncOutcome.RejectedByServer;

        public static ModelSyncResult ServerAcceptedResult(int statusCode)
            => new ModelSyncResult { Outcome = ModelSyncOutcome.ServerAccepted, StatusCode = statusCode };

        public static ModelSyncResult Queued(int? statusCode = null, string? responseBody = null, string? errorMessage = null)
            => new ModelSyncResult { Outcome = ModelSyncOutcome.Queued, StatusCode = statusCode, ResponseBody = responseBody, ErrorMessage = errorMessage };

        public static ModelSyncResult RejectedByServer(int statusCode, string responseBody, bool isTenantNotProvisioned)
            => new ModelSyncResult { Outcome = ModelSyncOutcome.RejectedByServer, StatusCode = statusCode, ResponseBody = responseBody, IsTenantNotProvisioned = isTenantNotProvisioned };

        public static ModelSyncResult NoTransport(string reason)
            => new ModelSyncResult { Outcome = ModelSyncOutcome.NoTransport, ErrorMessage = reason };
    }

    #endregion

    #region Model API Request DTO

    /// <summary>
    /// Request body for model registration sync (used by both SignalR and HTTP POST).
    /// Matches Swagger payload format.
    /// </summary>
    public class ModelApiRequest
    {
        [JsonPropertyName("modelGuid")]
        public string ModelGuid { get; set; } = "";

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = "";

        [JsonPropertyName("centralModelPath")]
        public string CentralModelPath { get; set; } = "";

        [JsonPropertyName("localProjectName")]
        public string LocalProjectName { get; set; } = "";

        [JsonPropertyName("projectId")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = "";

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("registeredAt")]
        public DateTime RegisteredAt { get; set; }

        [JsonPropertyName("registeredBy")]
        public string RegisteredBy { get; set; } = "";

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = "";

        [JsonPropertyName("lastOpenedAt")]
        public DateTime? LastOpenedAt { get; set; }

        [JsonPropertyName("lastOpenedBy")]
        public string LastOpenedBy { get; set; } = "";

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("isWorkshared")]
        public bool IsWorkshared { get; set; }

        [JsonPropertyName("isFamily")]
        public bool IsFamily { get; set; }

        [JsonPropertyName("isCloudModel")]
        public bool IsCloudModel { get; set; }

        [JsonPropertyName("isLocalModel")]
        public bool IsLocalModel { get; set; }
    }

    #endregion

    #region Model API Response DTO

    /// <summary>
    /// Response DTO for GET /api/v1/Revit/models.
    /// Matches the server response format.
    /// </summary>
    public class ModelApiResponse
    {
        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; set; }

        [JsonPropertyName("centralModelPath")]
        public string? CentralModelPath { get; set; }

        [JsonPropertyName("localProjectName")]
        public string? LocalProjectName { get; set; }

        [JsonPropertyName("zemanageProjectId")]
        public string? ZemanageProjectId { get; set; }

        // Alternate field name the server uses on some endpoints (returns the same project
        // GUID under "projectId" instead of "zemanageProjectId"). MapFromApiResponse merges
        // them so the local DB always gets the GUID populated.
        [JsonPropertyName("projectId")]
        public string? ProjectIdAlternate { get; set; }

        // Web-assigned project/folder name. Distinct from LocalProjectName: this is
        // populated by the server when a model is assigned to a project folder on the
        // web dashboard. Older builds didn't deserialize this; capturing it now so the
        // Session Information dialog can show the folder name without needing a second
        // project-entity round-trip. Several alternate names are tried because the
        // server has used different conventions across releases.
        [JsonPropertyName("projectName")]
        public string? ProjectName { get; set; }

        [JsonPropertyName("folderName")]
        public string? FolderName { get; set; }

        [JsonPropertyName("projectFolderName")]
        public string? ProjectFolderName { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("registeredAt")]
        public string? RegisteredAt { get; set; }

        [JsonPropertyName("registeredBy")]
        public string? RegisteredBy { get; set; }

        [JsonPropertyName("deactivatedAt")]
        public string? DeactivatedAt { get; set; }

        [JsonPropertyName("deactivatedBy")]
        public string? DeactivatedBy { get; set; }

        [JsonPropertyName("deactivationReason")]
        public string? DeactivationReason { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("lastOpenedAt")]
        public string? LastOpenedAt { get; set; }

        [JsonPropertyName("lastOpenedBy")]
        public string? LastOpenedBy { get; set; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("isWorkshared")]
        public bool IsWorkshared { get; set; }

        [JsonPropertyName("isFamily")]
        public bool IsFamily { get; set; }

        [JsonPropertyName("isCloudModel")]
        public bool IsCloudModel { get; set; }

    }

    #endregion

    #region Model Deactivate Request DTO

    /// <summary>
    /// Request body for PATCH /api/v1/Revit/models/{modelGuid}/deactivate.
    /// </summary>
    public class ModelDeactivateRequest
    {
        [JsonPropertyName("deactivatedBy")]
        public string? DeactivatedBy { get; set; }

        [JsonPropertyName("deactivationReason")]
        public string? DeactivationReason { get; set; }
    }

    #endregion
}
