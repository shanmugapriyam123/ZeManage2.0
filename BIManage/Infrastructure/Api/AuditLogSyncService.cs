using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs local audit log entries to the backend via POST /api/v1/Revit/audit-logs.
    
    /// Follows the same pattern as CommandProtectionSyncService.
    /// Pre-syncs referenced sessions to avoid FK constraint violations on the server.
    /// </summary>
    public class AuditLogSyncService
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly AuditRepository _auditRepository;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly SessionSyncService? _sessionSyncService;
        private readonly ILogger? _logger;

        private const string Endpoint = "/api/v1/Revit/audit-logs";

        // Rate-limit back-off: when the server returns 429 (Too Many Requests),
        // we honour the Retry-After header by suppressing further uploads until
        // _rateLimitedUntil. Without this, the previous code re-tried 429-rejected
        // entries on the next sync cycle (every ~30 s) — observed pattern in
        // BIManageRevit_20260507_1300.log line 720, 726 (same audit-log id retried
        // 11 s after a 429). Class-level state is fine because all sync cycles run
        // serially through DispatchPendingMailAsync / EvidenceUploadQueue.
        private DateTime _rateLimitedUntil = DateTime.MinValue;
        private static readonly TimeSpan _defaultRateLimitBackoff = TimeSpan.FromSeconds(60);
        private readonly object _rateLimitLock = new object();

        public AuditLogSyncService(
            AuthenticatedHttpClient? httpClient,
            AuditRepository auditRepository,
            OfflineQueueRepository? offlineQueue = null,
            ILogger? logger = null,
            SessionSyncService? sessionSyncService = null)
        {
            _httpClient = httpClient;
            _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
            _offlineQueue = offlineQueue;
            _sessionSyncService = sessionSyncService;
            _logger = logger;

            _logger?.LogInfo($"AuditLogSyncService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Upload all unsynced audit log entries to the backend in batches.
        /// </summary>
        public async Task<int> UploadPendingAuditLogsAsync(int batchSize = 50)
        {
            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogDebug("Not authenticated, skipping audit log sync");
                    return 0;
                }

                // Honour the rate-limit back-off window if a previous batch was 429'd.
                lock (_rateLimitLock)
                {
                    if (DateTime.UtcNow < _rateLimitedUntil)
                    {
                        var remaining = (_rateLimitedUntil - DateTime.UtcNow).TotalSeconds;
                        _logger?.LogDebug($"Audit log sync skipped — rate-limit back-off active for {remaining:F0}s more.");
                        return 0;
                    }
                }

                var unsyncedEntries = await _auditRepository.GetUnsyncedEntriesAsync(batchSize);

                if (unsyncedEntries.Count == 0)
                {
                    _logger?.LogDebug("No unsynced audit log entries");
                    return 0;
                }

                _logger?.LogInfo($"Uploading {unsyncedEntries.Count} unsynced audit log entries");

                // Pre-sync referenced sessions to avoid FK constraint violations on the server.
                // The server's Revit_Audit_Log_Tab has an FK to Revit_Sessions_Tab on SessionId.
                // Only attempt upload for entries whose session is confirmed on the server.
                var confirmedSessionIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (_sessionSyncService != null)
                {
                    var distinctSessionIds = unsyncedEntries
                        .Where(e => !string.IsNullOrEmpty(e.SessionId))
                        .Select(e => e.SessionId!)
                        .Distinct()
                        .ToList();

                    foreach (var sessionId in distinctSessionIds)
                    {
                        // Check if already confirmed from a previous sync cycle
                        if (_sessionSyncService.IsSessionConfirmedOnServer(sessionId))
                        {
                            confirmedSessionIds.Add(sessionId);
                            continue;
                        }

                        // Try to sync the session now
                        try
                        {
                            var synced = await _sessionSyncService.SyncSessionAsync(sessionId);
                            if (synced && _sessionSyncService.IsSessionConfirmedOnServer(sessionId))
                            {
                                confirmedSessionIds.Add(sessionId);
                            }
                            else
                            {
                                _logger?.LogWarning($"Session {sessionId} sync attempted but not confirmed on server — audit logs for this session will be deferred");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug($"Pre-sync session {sessionId} failed: {ex.Message}");
                        }
                    }
                }

                var successCount = 0;

                foreach (var entry in unsyncedEntries)
                {
                    // Skip entries missing mandatory FK fields — server will reject them
                    if (string.IsNullOrEmpty(entry.SessionId) || string.IsNullOrEmpty(entry.ModelGuid))
                    {
                        _logger?.LogWarning($"Audit log {entry.AuditLogId} skipped — missing sessionId or modelGuid");
                        await _auditRepository.MarkSyncedAsync(entry.AuditLogId);
                        continue;
                    }

                    // Skip entries whose session is not confirmed on the server — would cause FK 500
                    if (_sessionSyncService != null && !confirmedSessionIds.Contains(entry.SessionId))
                    {
                        _logger?.LogDebug($"Audit log {entry.AuditLogId} deferred — session {entry.SessionId} not confirmed on server");
                        continue; // Leave as unsynced, will retry next cycle after session is confirmed
                    }

                    var success = await UploadSingleEntryAsync(entry);
                    if (success)
                    {
                        await _auditRepository.MarkSyncedAsync(entry.AuditLogId);
                        successCount++;
                    }

                    // If the previous request tripped the 429 back-off, every subsequent
                    // request in this batch will also 429 — bail out so we don't burn through
                    // the rest of the queue uselessly.
                    lock (_rateLimitLock)
                    {
                        if (DateTime.UtcNow < _rateLimitedUntil)
                        {
                            var skipped = unsyncedEntries.Count - (unsyncedEntries.IndexOf(entry) + 1);
                            if (skipped > 0)
                                _logger?.LogInfo($"Audit log sync paused mid-batch — rate-limited; skipping {skipped} remaining (will retry after back-off).");
                            break;
                        }
                    }
                }

                _logger?.LogInfo($"Audit log sync complete: {successCount}/{unsyncedEntries.Count} uploaded");
                return successCount;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Audit log sync failed: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Upload a single audit log entry to the backend.
        /// </summary>
        private async Task<bool> UploadSingleEntryAsync(ProtectionAuditEntry entry)
        {
            try
            {
                var request = MapToApiRequest(entry);

                _logger?.LogDebug($"Audit log upload: {entry.AuditLogId} (sessionId: {entry.SessionId ?? "null"}, command: {entry.CommandName ?? "N/A"})");

                var response = await _httpClient!.PostAsync(Endpoint, request, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogDebug($"Audit log synced: {entry.AuditLogId}");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Audit log sync failed for {entry.AuditLogId}: {response.StatusCode} - {responseBody}");

                // 429 Too Many Requests — server is rate-limiting us. Set the class-level
                // back-off (driven by Retry-After header when present, or 60 s default) so
                // subsequent batches see _rateLimitedUntil and skip until the window passes.
                // Don't mark the entry as synced — the next cycle (after back-off) will pick
                // it up. Without this, the same entry was retried every ~10 s in the field
                // (BIManageRevit_20260507_1300.log line 720, 726) and the orphan-evidence
                // cascade kicked in because the audit log never made it to the server before
                // its evidence upload hit the same audit-log-id.
                if ((int)response.StatusCode == 429)
                {
                    var backoff = _defaultRateLimitBackoff;
                    try
                    {
                        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                            backoff = delta;
                        else if (response.Headers.RetryAfter?.Date is DateTimeOffset until)
                            backoff = until.UtcDateTime - DateTime.UtcNow;
                    }
                    catch { /* fall back to default */ }
                    if (backoff < TimeSpan.FromSeconds(5)) backoff = TimeSpan.FromSeconds(5);
                    if (backoff > TimeSpan.FromMinutes(10)) backoff = TimeSpan.FromMinutes(10);

                    lock (_rateLimitLock)
                    {
                        _rateLimitedUntil = DateTime.UtcNow.Add(backoff);
                    }
                    _logger?.LogWarning($"Audit log sync rate-limited (429) — backing off for {backoff.TotalSeconds:F0}s. Entry {entry.AuditLogId} will be retried after the window.");
                    return false;
                }

                // Server already has this audit log (e.g. previous POST committed
                // server-side but the client lost the response, then re-queued).
                // Return true so the caller MarkSyncedAsync's the local row — from
                // the system's perspective the row IS on the server. Without this,
                // the entry would be re-queued and loop in OfflineSyncProcessor for
                // ~25 retry cycles before MaxRetries discards it (observed in
                // BIManageRevit_20260528_1420.log).
                if ((int)response.StatusCode >= 500 &&
                    responseBody.IndexOf("duplicate key value violates unique constraint",
                                          StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _logger?.LogInfo($"Audit log {entry.AuditLogId} already on server (duplicate key) — treating as synced");
                    return true;
                }

                // Queue for offline retry on server errors — mark as synced so the direct
                // upload path doesn't re-pick it next cycle (offline queue owns the retry now)
                if ((int)response.StatusCode >= 500 && _offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(entry);
                    return true; // Treat as "handled" — offline queue will retry
                }

                return false;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _logger?.LogError($"Audit log sync HTTP error for {entry.AuditLogId}: {ex.Message}", ex);

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(entry);
                    return true; // Offline queue owns it now
                }

                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Audit log sync timeout for {entry.AuditLogId}: {ex.Message}", ex);

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(entry);
                    return true; // Offline queue owns it now
                }

                return false;
            }
        }

        /// <summary>
        /// Maps a local ProtectionAuditEntry to the API request DTO.
        /// </summary>
        private static CreateRevitAuditLogRequest MapToApiRequest(ProtectionAuditEntry entry)
        {
            return new CreateRevitAuditLogRequest
            {
                AuditLogId = entry.AuditLogId,
                UserName = entry.UserName,
                WasCompanyAdmin = entry.WasCompanyAdmin ? 1 : 0,
                WasProjectAdmin = entry.WasProjectAdmin ? 1 : 0,
                ModelGuid = entry.ModelGuid,
                ProtectionId = entry.ProtectionId,
                CommandName = entry.CommandName,
                Mode = entry.Mode.ToString(),
                Action = entry.Action.ToString(),
                ElementIds = entry.ElementIds,
                ElementCount = entry.ElementCount,
                ElementCategory = entry.ElementCategory,
                ElementFamilyType = entry.ElementFamilyType,
                ElementName = entry.ElementName,
                Reason = entry.Reason,
                UserComment = entry.UserComment,
                EventSource = entry.EventSource,
                OverrideMethod = entry.OverrideMethod,
                SessionId = entry.SessionId,
                SentMail = true, // Per user request: tell the server every audit entry is already mail-handled (sent_mail=1 / [v]).
                // Force a UTC ISO-8601 string with the trailing "Z" marker so the server
                // (and downstream mail formatter) treats the timestamp as UTC. Two failure
                // modes this guards against:
                //   1. entry.Timestamp may have Kind=Unspecified after SQLite round-trip
                //      (SQLite stores ISO-8601 without tz info, .NET reads it back as
                //      Unspecified). .ToString("o") on an Unspecified DateTime emits NO
                //      "Z" / no offset → server treats it as local-time-of-server and
                //      the mail timestamp ends up an hour off the user's actual action time.
                //   2. entry.Timestamp may have Kind=Local on legacy rows written before
                //      DateTime.Now → DateTime.UtcNow was applied across the audit
                //      pipeline. Convert those to UTC explicitly before serialising.
                // Our audit timestamps are now always written as DateTime.UtcNow, so
                // Unspecified rows in SQLite are by-convention UTC — just stamp the Kind.
                Timestamp = NormalizeToUtcIso(entry.Timestamp)
            };
        }

        /// <summary>
        /// Normalises a DateTime to a UTC ISO-8601 string with a "Z" suffix so the server
        /// receives an unambiguous UTC instant. Handles all three DateTimeKind values:
        ///   Utc         → as-is, formatted with Z
        ///   Local       → converted via ToUniversalTime() then formatted with Z
        ///   Unspecified → treated as UTC (matches the audit pipeline's invariant: all
        ///                 timestamps are written via DateTime.UtcNow; SQLite drops the
        ///                 Kind during round-trip but the value is still UTC)
        /// </summary>
        private static string NormalizeToUtcIso(DateTime value)
        {
            DateTime utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
            // "o" on a Kind=Utc DateTime emits the trailing "Z" automatically.
            return utc.ToString("o");
        }

        private async Task QueueForOfflineSyncAsync(ProtectionAuditEntry entry)
        {
            try
            {
                var request = MapToApiRequest(entry);

                var operationData = new OfflineOperationData
                {
                    Endpoint = Endpoint,
                    HttpMethod = "POST",
                    Payload = request,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = System.Text.Json.JsonSerializer.Serialize(operationData,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

                var operation = OfflineOperation.Create(
                    sessionId: entry.SessionId ?? "unknown",
                    operationType: "audit_log_sync",
                    operationData: jsonData,
                    priority: 2); // Higher priority than command protection

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline audit log sync: {entry.AuditLogId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline audit log operation: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Request DTO matching the backend CreateRevitAuditLogRequest schema.
    /// POST /api/v1/Revit/audit-logs
    /// </summary>
    public class CreateRevitAuditLogRequest
    {
        [JsonPropertyName("auditLogId")]
        public string AuditLogId { get; set; } = string.Empty;

        [JsonPropertyName("userName")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("wasCompanyAdmin")]
        public int WasCompanyAdmin { get; set; }

        [JsonPropertyName("wasProjectAdmin")]
        public int WasProjectAdmin { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("protectionId")]
        public string? ProtectionId { get; set; }

        [JsonPropertyName("commandName")]
        public string? CommandName { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("elementIds")]
        public string? ElementIds { get; set; }

        [JsonPropertyName("elementCount")]
        public int ElementCount { get; set; }

        [JsonPropertyName("elementCategory")]
        public string? ElementCategory { get; set; }

        [JsonPropertyName("elementFamilyType")]
        public string? ElementFamilyType { get; set; }

        [JsonPropertyName("elementName")]
        public string? ElementName { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("userComment")]
        public string? UserComment { get; set; }

        [JsonPropertyName("eventSource")]
        public string? EventSource { get; set; }

        [JsonPropertyName("overrideMethod")]
        public string? OverrideMethod { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("sentMail")]
        public bool SentMail { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }
    }
}
