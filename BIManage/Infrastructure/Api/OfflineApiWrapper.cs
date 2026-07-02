using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Wraps HTTP POST/PATCH calls and queues them to offline_queue if the API call fails.
    /// Provides offline-first architecture for all POST API endpoints.
    /// </summary>
    public class OfflineApiWrapper
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository _offlineQueue;
        private readonly ILogger? _logger;
        private readonly string _sessionId;

        /// <summary>
        /// Operation types for offline queue (matches Swagger endpoints)
        /// </summary>
        public static class OperationTypes
        {
            public const string SessionOpen = "session_open";
            public const string SessionUpdate = "session_update";
            public const string ModelRegister = "model_register";
            public const string ModelSession = "model_session";
            public const string MetricsManual = "metrics_manual";
            public const string MetricsPeriodic = "metrics_periodic";
            public const string MetricsSyncSave = "metrics_syncsave";
            public const string CommandProtection = "command_protection";
            public const string CommandProtectionUpdate = "command_protection_update";
            public const string CommandProtectionDelete = "command_protection_delete";
            public const string ModelAdmins = "model_admins";
            public const string ModelDeactivate = "model_deactivate";
            public const string ModelActivate = "model_activate";
            public const string RuleSync = "rule_sync";
            public const string ModelSync = "model_sync";
            public const string PinProtectionSync = "pin_protection_sync";
            public const string PinProtectionDelete = "pin_protection_delete";
            public const string EvidenceUpload = "evidence_upload";
            public const string EventProtection = "event_protection";
            public const string EventProtectionUpdate = "event_protection_update";
            public const string EventProtectionDelete = "event_protection_delete";
            public const string EventProtectionToggle = "event_protection_toggle";
            public const string UnmonitoredUserReport = "unmonitored_user_report";
            public const string WorksetOpeningDuration = "workset_opening_duration";
            public const string LinkLoadingDuration = "link_loading_duration";
        }

        public OfflineApiWrapper(
            AuthenticatedHttpClient? httpClient,
            OfflineQueueRepository offlineQueue,
            string sessionId,
            ILogger? logger = null)
        {
            _httpClient = httpClient;
            _offlineQueue = offlineQueue ?? throw new ArgumentNullException(nameof(offlineQueue));
            _sessionId = sessionId ?? Guid.NewGuid().ToString();
            _logger = logger;

            _logger?.LogInfo("OfflineApiWrapper initialized");
        }

        /// <summary>
        /// Sends a POST request. If it fails (network error, timeout, or offline), queues for later sync.
        /// Returns true if sent successfully OR queued successfully.
        /// </summary>
        public async Task<(bool Success, bool Queued)> PostAsync(
            string endpoint,
            object payload,
            string operationType,
            int priority = 5)
        {
            // Try to send if we have an authenticated client
            if (_httpClient != null && _httpClient.IsAuthenticated)
            {
                try
                {
                    var response = await _httpClient.PostAsync(endpoint, payload);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"POST {endpoint} succeeded (Status: {response.StatusCode})");
                        return (true, false);
                    }

                    // Queue ALL non-success responses for retry (including 401 after re-auth)
                    var body = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"POST {endpoint} failed: {response.StatusCode} - {body} - queuing for retry");
                }
                catch (HttpRequestException ex)
                {
                    _logger?.LogWarning($"POST {endpoint} network error: {ex.Message} - queuing for retry");
                }
                catch (TaskCanceledException ex)
                {
                    _logger?.LogWarning($"POST {endpoint} timeout: {ex.Message} - queuing for retry");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"POST {endpoint} unexpected error: {ex.Message}", ex);
                }
            }
            else
            {
                _logger?.LogDebug($"POST {endpoint} - no authenticated client, queuing for later");
            }

            // Queue the operation for later sync
            return await QueueOperationAsync(endpoint, "POST", payload, operationType, priority);
        }

        /// <summary>
        /// Sends a PATCH request. If it fails (network error, timeout, or offline), queues for later sync.
        /// Returns true if sent successfully OR queued successfully.
        /// </summary>
        public async Task<(bool Success, bool Queued)> PatchAsync(
            string endpoint,
            object payload,
            string operationType,
            int priority = 5)
        {
            // Try to send if we have an authenticated client
            if (_httpClient != null && _httpClient.IsAuthenticated)
            {
                try
                {
                    var response = await _httpClient.PatchAsync(endpoint, payload);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"PATCH {endpoint} succeeded (Status: {response.StatusCode})");
                        return (true, false);
                    }

                    // Queue ALL non-success responses for retry
                    var body = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"PATCH {endpoint} failed: {response.StatusCode} - {body} - queuing for retry");
                }
                catch (HttpRequestException ex)
                {
                    _logger?.LogWarning($"PATCH {endpoint} network error: {ex.Message} - queuing for retry");
                }
                catch (TaskCanceledException ex)
                {
                    _logger?.LogWarning($"PATCH {endpoint} timeout: {ex.Message} - queuing for retry");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"PATCH {endpoint} unexpected error: {ex.Message}", ex);
                }
            }
            else
            {
                _logger?.LogDebug($"PATCH {endpoint} - no authenticated client, queuing for later");
            }

            // Queue the operation for later sync
            return await QueueOperationAsync(endpoint, "PATCH", payload, operationType, priority);
        }

        /// <summary>
        /// Queues an operation for later sync.
        /// </summary>
        private async Task<(bool Success, bool Queued)> QueueOperationAsync(
            string endpoint,
            string httpMethod,
            object payload,
            string operationType,
            int priority)
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

                var jsonData = JsonSerializer.Serialize(operationData, GetJsonOptions());

                var operation = OfflineOperation.Create(
                    sessionId: _sessionId,
                    operationType: operationType,
                    operationData: jsonData,
                    priority: priority);

                var queued = await _offlineQueue.EnqueueAsync(operation);

                if (queued)
                {
                    _logger?.LogInfo($"Queued offline operation: {operationType} ({endpoint})");
                    return (false, true);
                }
                else
                {
                    _logger?.LogError($"Failed to queue offline operation: {operationType}");
                    return (false, false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error queuing operation: {ex.Message}", ex);
                return (false, false);
            }
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }
    }

    /// <summary>
    /// Data structure stored in offline_queue.operation_data column.
    /// Contains all info needed to replay the HTTP request.
    /// </summary>
    public class OfflineOperationData
    {
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; } = string.Empty;

        [JsonPropertyName("httpMethod")]
        public string HttpMethod { get; set; } = "POST";

        [JsonPropertyName("payload")]
        public object? Payload { get; set; }

        [JsonPropertyName("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }
    }
}
