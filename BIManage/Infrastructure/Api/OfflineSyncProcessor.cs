using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Auth.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Background processor that syncs queued offline operations when API becomes available.
    /// Runs periodically to process pending items from offline_queue table.
    /// Attempts to authenticate before processing if refresh token is available.
    /// </summary>
    public class OfflineSyncProcessor : IDisposable
    {
        private readonly OfflineQueueRepository _offlineQueue;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly AuthTokenManager? _tokenManager;
        private readonly AuthApiService? _authApi;
        private readonly IFeatureToggleService? _featureToggleService;
        private readonly ILogger? _logger;
        private SessionSyncService? _sessionSyncService;
        private readonly Timer? _timer;
        private readonly object _syncLock = new object();
        private bool _isSyncing;
        private bool _disposed;

        // Device re-authentication cooldown to avoid hammering the server
        private DateTime _lastDeviceAuthAttempt = DateTime.MinValue;
        private readonly TimeSpan _deviceAuthCooldown = TimeSpan.FromSeconds(60);

        // Configuration
        private readonly TimeSpan _syncInterval = TimeSpan.FromSeconds(30);
        private readonly int _batchSize = 20;

        public OfflineSyncProcessor(
            OfflineQueueRepository offlineQueue,
            AuthenticatedHttpClient? httpClient,
            ILogger? logger = null,
            bool autoStart = true,
            AuthTokenManager? tokenManager = null,
            AuthApiService? authApi = null,
            IFeatureToggleService? featureToggleService = null)
        {
            _offlineQueue = offlineQueue ?? throw new ArgumentNullException(nameof(offlineQueue));
            _httpClient = httpClient;
            _tokenManager = tokenManager;
            _authApi = authApi;
            _featureToggleService = featureToggleService;
            _logger = logger;

            if (autoStart)
            {
                _timer = new Timer(OnTimerCallback, null, TimeSpan.FromSeconds(30), _syncInterval);
                _logger?.LogInfo($"OfflineSyncProcessor started (interval: {_syncInterval.TotalSeconds}s, batch: {_batchSize})");
            }
        }

        /// <summary>
        /// Sets the session sync service for FK validation before replaying queued operations.
        /// </summary>
        public void SetSessionSyncService(SessionSyncService sessionSyncService)
        {
            _sessionSyncService = sessionSyncService;
        }

        /// <summary>
        /// Manually trigger a sync operation.
        /// </summary>
        public async Task<SyncResult> SyncNowAsync()
        {
            return await ProcessQueueAsync();
        }

        /// <summary>
        /// Gets the current queue summary.
        /// </summary>
        public async Task<QueueSummary> GetQueueStatusAsync()
        {
            return await _offlineQueue.GetQueueSummaryAsync();
        }

        private void OnTimerCallback(object? state)
        {
            // Run sync in background, don't block timer
            Task.Run(async () =>
            {
                try
                {
                    await ProcessQueueAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Sync timer error: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Attempts to ensure authentication is available for API requests.
        /// Priority: 1) Valid in-memory token, 2) Stored refresh token, 3) Device validation by machine ID.
        /// The device validation fallback enables recovery when stored tokens are lost
        /// or when the server was unreachable during startup authentication.
        /// </summary>
        private async Task<bool> EnsureAuthenticationAsync()
        {
            // If already authenticated with valid token, we're good
            if (_tokenManager != null && _tokenManager.IsAuthenticated)
            {
                _logger?.LogDebug("OfflineSyncProcessor: Authentication confirmed (valid token)");
                return true;
            }

            // Try token refresh if we have a stored refresh token
            if (_tokenManager != null && _tokenManager.HasStoredRefreshToken)
            {
                _logger?.LogInfo("OfflineSyncProcessor: Attempting token refresh...");
                try
                {
                    var token = await _tokenManager.GetAccessTokenAsync();
                    if (!string.IsNullOrEmpty(token))
                    {
                        _logger?.LogInfo("OfflineSyncProcessor: Token refresh successful, ready to sync");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"OfflineSyncProcessor: Token refresh failed: {ex.Message}");
                }
            }

            // Fallback: Try device validation by machine ID.
            // This re-authenticates even when refresh token is lost or was never obtained.
            // Uses a cooldown to avoid hammering the server on every 30s tick.
            // Skip for admin sessions — device re-auth would downgrade the token (losing profileId).
            if (_authApi != null && _tokenManager != null && !_tokenManager.IsAdminSession
                && DateTime.UtcNow - _lastDeviceAuthAttempt > _deviceAuthCooldown)
            {
                _lastDeviceAuthAttempt = DateTime.UtcNow;
                try
                {
                    var machineId = MachineIdentifier.GetMachineId(_logger);
                    _logger?.LogInfo($"OfflineSyncProcessor: Attempting device validation for machine {machineId}...");

                    var response = await _authApi.ValidateDeviceAsync(machineId);
                    if (response?.AccessToken != null)
                    {
                        _tokenManager.SetTokensFromDevice(response);
                        _logger?.LogInfo("OfflineSyncProcessor: Device validation successful — authentication restored");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"OfflineSyncProcessor: Device validation failed: {ex.Message}");
                }
            }

            return false;
        }

        private async Task<SyncResult> ProcessQueueAsync()
        {
            // Skip if background sync is disabled in settings
            if (_featureToggleService != null && !_featureToggleService.IsFeatureEnabled("BackgroundSync"))
            {
                _logger?.LogDebug("Background sync disabled in settings, skipping queue processing");
                return new SyncResult { Skipped = true };
            }

            // Prevent concurrent sync operations
            lock (_syncLock)
            {
                if (_isSyncing)
                {
                    _logger?.LogDebug("Sync already in progress, skipping");
                    return new SyncResult { Skipped = true };
                }
                _isSyncing = true;
            }

            var result = new SyncResult();

            try
            {
                // Check HTTP client availability
                if (_httpClient == null)
                {
                    _logger?.LogDebug("OfflineSyncProcessor: No HTTP client configured, skipping sync");
                    return result;
                }

                // Try to ensure authentication before processing
                // This triggers token refresh if a refresh token is stored
                var canAuthenticate = await EnsureAuthenticationAsync();
                if (!canAuthenticate)
                {
                    // Check queue size for informational logging
                    var queueSummary = await _offlineQueue.GetQueueSummaryAsync();
                    if (queueSummary.PendingCount > 0)
                    {
                        _logger?.LogInfo($"OfflineSyncProcessor: {queueSummary.PendingCount} operations queued, waiting for authentication (register device to enable sync)");
                    }
                    else
                    {
                        _logger?.LogDebug("OfflineSyncProcessor: No pending operations and no authentication");
                    }
                    return result;
                }

                // Get pending operations
                var pendingOperations = await _offlineQueue.GetPendingOperationsAsync(_batchSize);
                if (pendingOperations.Count == 0)
                {
                    _logger?.LogDebug("No pending operations to sync");
                    return result;
                }

                result.TotalItems = pendingOperations.Count;
                _logger?.LogInfo($"Processing {pendingOperations.Count} pending offline operations");

                // Start sync log
                var syncLog = await _offlineQueue.StartSyncAsync(
                    Guid.NewGuid().ToString(),
                    "upload",
                    pendingOperations.Count);

                foreach (var operation in pendingOperations)
                {
                    try
                    {
                        // Mark as in-progress
                        await _offlineQueue.MarkInProgressAsync(operation.QueueId);

                        // Process the operation
                        var success = await ProcessOperationAsync(operation);

                        if (success)
                        {
                            await _offlineQueue.MarkCompletedAsync(operation.QueueId);
                            result.Succeeded++;
                            _logger?.LogInfo($"Synced operation: {operation.OperationType} (ID: {operation.QueueId})");
                        }
                        else
                        {
                            await _offlineQueue.MarkFailedAsync(operation.QueueId, "API request failed");
                            result.Failed++;
                            if (operation.MaxRetries > 0 && operation.RetryCount + 1 >= operation.MaxRetries)
                                _logger?.LogWarning($"Operation permanently failed after {operation.MaxRetries} retries: {operation.OperationType} ({operation.QueueId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        await _offlineQueue.MarkFailedAsync(operation.QueueId, ex.Message);
                        result.Failed++;
                        _logger?.LogError($"Failed to sync operation {operation.QueueId}: {ex.Message}", ex);
                    }
                }

                // Complete sync log
                await _offlineQueue.CompleteSyncAsync(
                    syncLog.SyncId,
                    result.Succeeded,
                    result.Failed,
                    result.Failed == 0);

                _logger?.LogInfo($"Sync completed: {result.Succeeded} succeeded, {result.Failed} failed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Sync processing error: {ex.Message}", ex);
            }
            finally
            {
                lock (_syncLock)
                {
                    _isSyncing = false;
                }
            }

            return result;
        }

        private async Task<bool> ProcessOperationAsync(OfflineOperation operation)
        {
            try
            {
                // Parse the operation data
                var operationData = JsonSerializer.Deserialize<OfflineOperationData>(
                    operation.OperationData,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (operationData == null)
                {
                    _logger?.LogWarning($"Invalid operation data for {operation.QueueId}");
                    return false;
                }

                // Handle multipart evidence uploads separately
                if (operationData.HttpMethod.Equals("POST_MULTIPART", StringComparison.OrdinalIgnoreCase))
                {
                    return await ProcessMultipartEvidenceUploadAsync(operationData);
                }

                // For operations that reference a SessionId (audit logs, metrics, model-syncs),
                // skip if the session hasn't been confirmed on the server — would cause FK 500
                if (_sessionSyncService != null && operation.OperationType != OfflineApiWrapper.OperationTypes.SessionOpen)
                {
                    var sessionId = ExtractSessionIdFromPayload(operationData.Payload);
                    if (!string.IsNullOrEmpty(sessionId) && !_sessionSyncService.IsSessionConfirmedOnServer(sessionId))
                    {
                        _logger?.LogDebug($"Deferring {operation.OperationType} — session {sessionId} not confirmed on server");
                        return false; // Keep in queue, retry after session is confirmed
                    }
                }

                // Execute the HTTP request. Admin-scoped operations (command/event/rule
                // protection writes) MUST carry the admin token so the server's profileId
                // claim parser succeeds — otherwise device-token retries get rejected with
                // a misleading 400 even though the server-side bearer auth passes.
                var requireAdmin = IsAdminScopedOperation(operation.OperationType);

                // Permanent 401-loop fix (2026-05-25): when an admin-scoped operation is queued
                // but the current session can't issue an admin token (user dropped from admin →
                // device-only, or admin refresh token expired), short-circuit BEFORE hitting the
                // server. The previous behavior was: send anyway → 401 → force-expire tokens →
                // refresh fails (no admin slot) → mark failed → next 30s cycle repeats forever.
                // Now we keep the operation in queue silently; the next cycle re-checks and the
                // op drains automatically once the user signs in as admin again.
                if (requireAdmin && _tokenManager != null && !_tokenManager.CanIssueAdminToken)
                {
                    _logger?.LogDebug($"Deferring admin-scoped {operation.OperationType} ({operationData.Endpoint}) — no admin token available, will retry after admin sign-in");
                    return false; // keep in queue, no HTTP round-trip, no 401 spam
                }

                var response = operationData.HttpMethod.ToUpperInvariant() switch
                {
                    "POST" => await _httpClient!.PostAsync(operationData.Endpoint, operationData.Payload, requireAdmin),
                    "PATCH" => await _httpClient!.PatchAsync(operationData.Endpoint, operationData.Payload, requireAdmin),
                    "PUT" => await _httpClient!.PutAsync(operationData.Endpoint, operationData.Payload, requireAdmin),
                    "DELETE" => await _httpClient!.DeleteAsync(operationData.Endpoint, requireAdmin),
                    _ => throw new NotSupportedException($"HTTP method not supported: {operationData.HttpMethod}")
                };

                if (response.IsSuccessStatusCode)
                {
                    // When a queued session_open succeeds, mark the session as confirmed so
                    // downstream audit_log_sync / metrics operations unblock on the next pass.
                    if (operation.OperationType == OfflineApiWrapper.OperationTypes.SessionOpen)
                    {
                        var sid = ExtractSessionIdFromPayload(operationData.Payload);
                        if (!string.IsNullOrEmpty(sid))
                            _sessionSyncService?.ConfirmSessionOnServer(sid);
                    }
                    return true;
                }

                var statusCode = (int)response.StatusCode;

                // 404 Not Found
                if (statusCode == 404)
                {
                    var method = operationData.HttpMethod.ToUpperInvariant();
                    if (method == "PUT" || method == "DELETE" || method == "PATCH")
                    {
                        // PUT/PATCH/DELETE to non-existent resource — retrying won't help, discard
                        _logger?.LogWarning($"Resource not found (404) for {method} {operationData.Endpoint} - discarding stale operation");
                        return true;
                    }

                    // POST 404 = endpoint not deployed yet, retry later
                    _logger?.LogWarning($"API not found (404) for {operationData.Endpoint} - will retry later");
                    return false;
                }

                // 401 Unauthorized - retriable after re-authentication
                if (statusCode == 401)
                {
                    _logger?.LogWarning($"Unauthorized (401) for {operationData.Endpoint} - will retry after re-auth");
                    return false;
                }

                // Other client errors (400, 403, 409, etc.) - bad request, don't retry
                if (statusCode >= 400 && statusCode < 500)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Client error {response.StatusCode} for {operationData.Endpoint}: {body}");
                    return true;
                }

                // Server error (5xx) — log body and handle known cases
                var serverErrorBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Server error {response.StatusCode} for {operationData.Endpoint}: {serverErrorBody}");

                // FK constraint violation — prerequisite data missing on server (e.g., model not registered).
                // Retrying won't help — discard immediately.
                if (serverErrorBody.Contains("foreign key constraint", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning($"Discarding {operation.OperationType} — FK constraint violation (prerequisite missing on server): {operationData.Endpoint}");
                    return true;
                }

                // Unique-constraint / duplicate-key violation — record already on server.
                // Endpoints using client-generated GUID PKs (e.g. /api/v1/Revit/audit-logs)
                // return a generic 500 with the PostgreSQL "23505: duplicate key value
                // violates unique constraint" body when the row was previously POSTed and
                // committed server-side. Discard — retrying will never succeed, and the
                // server already has the data we owed it. Without this, eight stuck
                // audit_log_sync rows looped for ~25 retry cycles each in
                // BIManageRevit_20260528_1420.log before MaxRetries finally drained them.
                if (serverErrorBody.IndexOf("duplicate key value violates unique constraint",
                                             StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _logger?.LogInfo($"Discarding {operation.OperationType} — record already on server (duplicate key): {operationData.Endpoint}");
                    return true;
                }

                // session_open 500 → try PATCH in case the session already exists on the server
                // (server returns generic 500 on duplicate POST, not a specific "entity changes" message)
                if (operation.OperationType == OfflineApiWrapper.OperationTypes.SessionOpen)
                {
                    _logger?.LogInfo($"Session POST returned 500 — attempting PATCH fallback: {operation.QueueId}");
                    return await TryPatchSessionFromQueueAsync(operationData);
                }

                // Persistent 500 errors — discard when max retries reached.
                // Uses the operation's MaxRetries (default 10), not a hardcoded limit.
                var maxRetries = operation.MaxRetries > 0 ? operation.MaxRetries : OfflineOperation.DefaultMaxRetries;
                if (operation.RetryCount >= maxRetries)
                {
                    _logger?.LogWarning($"Discarding {operation.OperationType} after {operation.RetryCount} retries (max: {maxRetries}, persistent 500): {operationData.Endpoint}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error processing operation: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// PATCH fallback for a queued session_open that returned 500 duplicate.
        /// Extracts the sessionId from the stored payload and sends a minimal PATCH status update.
        /// Returns true (discard) regardless — the session state on the server is best-effort at this point.
        /// </summary>
        private async Task<bool> TryPatchSessionFromQueueAsync(OfflineOperationData operationData)
        {
            try
            {
                // Extract sessionId from the stored payload (JsonElement or Dictionary)
                string? sessionId = null;
                var payloadJson = operationData.Payload?.ToString();
                if (!string.IsNullOrEmpty(payloadJson))
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("sessionId", out var sidProp))
                        sessionId = sidProp.GetString();
                }

                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger?.LogWarning("session_open fallback PATCH: could not extract sessionId — discarding");
                    return true;
                }

                var patchEndpoint = $"/api/v1/Revit/session/{sessionId}";
                var patchPayload = new SessionUpdateRequest { Status = "Active" };

                var patchResponse = await _httpClient!.PatchAsync(patchEndpoint, patchPayload);
                if (patchResponse.IsSuccessStatusCode)
                {
                    _sessionSyncService?.ConfirmSessionOnServer(sessionId);
                    _logger?.LogInfo($"Session PATCH fallback succeeded for queued session: {sessionId}");
                }
                else
                    _logger?.LogWarning($"Session PATCH fallback failed for {sessionId} ({patchResponse.StatusCode}) — discarding stale entry");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Session PATCH fallback error: {ex.Message}");
            }

            return true; // Always discard — stop retrying this entry
        }

        /// <summary>
        /// Returns true for operations that target admin-only server endpoints
        /// (command/event/rule protections) so the queued retry attaches the admin token.
        /// </summary>
        private static bool IsAdminScopedOperation(string? operationType)
        {
            return operationType switch
            {
                OfflineApiWrapper.OperationTypes.CommandProtection => true,
                OfflineApiWrapper.OperationTypes.CommandProtectionUpdate => true,
                OfflineApiWrapper.OperationTypes.CommandProtectionDelete => true,
                OfflineApiWrapper.OperationTypes.EventProtection => true,
                OfflineApiWrapper.OperationTypes.EventProtectionUpdate => true,
                OfflineApiWrapper.OperationTypes.EventProtectionDelete => true,
                OfflineApiWrapper.OperationTypes.EventProtectionToggle => true,
                OfflineApiWrapper.OperationTypes.RuleSync => true,
                "audit_log_sync" => true,
                _ => false
            };
        }

        /// <summary>
        /// Processes a multipart evidence upload from the offline queue.
        /// Rebuilds the multipart request from stored file paths and metadata.
        /// </summary>
        private async Task<bool> ProcessMultipartEvidenceUploadAsync(OfflineOperationData operationData)
        {
            try
            {
                // Deserialize the evidence payload from the stored JSON
                var payloadJson = operationData.Payload?.ToString();
                if (string.IsNullOrEmpty(payloadJson))
                {
                    _logger?.LogWarning("Evidence upload payload is empty");
                    return true; // Discard — nothing to retry
                }

                var evidencePayload = JsonSerializer.Deserialize<Core.Evidence.EvidenceUploadPayload>(
                    payloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (evidencePayload == null || string.IsNullOrEmpty(evidencePayload.AuditLogId))
                {
                    _logger?.LogWarning("Evidence upload payload missing AuditLogId, discarding");
                    return true; // Discard — can't upload without AuditLogId
                }

                using (var formData = new System.Net.Http.MultipartFormDataContent())
                {
                    formData.Add(new System.Net.Http.StringContent(evidencePayload.AuditLogId), "AuditLogId");

                    // Image1 = Before screenshot
                    if (!string.IsNullOrEmpty(evidencePayload.BeforeFilePath) && System.IO.File.Exists(evidencePayload.BeforeFilePath))
                    {
                        var beforeBytes = await Task.Run(() => System.IO.File.ReadAllBytes(evidencePayload.BeforeFilePath));
                        var beforeContent = new System.Net.Http.ByteArrayContent(beforeBytes);
                        beforeContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                        formData.Add(beforeContent, "Image1", System.IO.Path.GetFileName(evidencePayload.BeforeFilePath));
                        if (evidencePayload.BeforeCapturedAt.HasValue)
                            formData.Add(new System.Net.Http.StringContent(evidencePayload.BeforeCapturedAt.Value.ToString("o")), "CapturedAt1");
                    }

                    // Image2 = After screenshot
                    if (!string.IsNullOrEmpty(evidencePayload.AfterFilePath) && System.IO.File.Exists(evidencePayload.AfterFilePath))
                    {
                        var afterBytes = await Task.Run(() => System.IO.File.ReadAllBytes(evidencePayload.AfterFilePath));
                        var afterContent = new System.Net.Http.ByteArrayContent(afterBytes);
                        afterContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                        formData.Add(afterContent, "Image2", System.IO.Path.GetFileName(evidencePayload.AfterFilePath));
                        if (evidencePayload.AfterCapturedAt.HasValue)
                            formData.Add(new System.Net.Http.StringContent(evidencePayload.AfterCapturedAt.Value.ToString("o")), "CapturedAt2");
                    }

                    _logger?.LogInfo($"Replaying evidence upload from offline queue: auditLogId={evidencePayload.AuditLogId}");
                    var response = await _httpClient!.PostMultipartAsync(operationData.Endpoint, formData);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"Evidence upload replay succeeded: auditLogId={evidencePayload.AuditLogId}");
                        return true;
                    }

                    var statusCode = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync();

                    // Client errors (400, 403, 409) — bad data, don't retry
                    if (statusCode >= 400 && statusCode < 500 && statusCode != 401)
                    {
                        _logger?.LogWarning($"Evidence upload replay failed (non-retriable): {response.StatusCode} - {body}");
                        return true; // Discard
                    }

                    // 401 or 5xx — retriable
                    _logger?.LogWarning($"Evidence upload replay failed (retriable): {response.StatusCode} - {body}");
                    return false;
                }
            }
            catch (System.IO.FileNotFoundException ex)
            {
                _logger?.LogWarning($"Evidence file no longer exists, discarding: {ex.Message}");
                return true; // Discard — files cleaned up
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Evidence upload replay error: {ex.Message}", ex);
                return false; // Retry
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Change(Timeout.Infinite, 0);
            _timer?.Dispose();

            _logger?.LogInfo("OfflineSyncProcessor disposed");
        }

        /// <summary>
        /// Extracts sessionId from a queued JSON payload string.
        /// Handles both raw JSON and serialized string payloads.
        /// </summary>
        private static string? ExtractSessionIdFromPayload(object? payload)
        {
            try
            {
                var json = payload?.ToString();
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("sessionId", out var sessionIdProp))
                    return sessionIdProp.GetString();
                if (doc.RootElement.TryGetProperty("SessionId", out var sessionIdProp2))
                    return sessionIdProp2.GetString();
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Result of a sync operation
    /// </summary>
    public class SyncResult
    {
        public int TotalItems { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public bool Skipped { get; set; }

        public bool IsSuccess => Failed == 0 && !Skipped;
        public int Processed => Succeeded + Failed;
    }
}
