using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Evidence
{
    /// <summary>
    /// Background service for uploading evidence files to backend API.
    /// Groups before/after screenshots by audit_log_id and uploads via
    /// POST /api/v1/Revit/evidence-images/with-images (multipart: AuditLogId + Image1 + CapturedAt1 + Image2 + CapturedAt2).
    /// On failure, queues to OfflineQueueRepository for retry by OfflineSyncProcessor.
    /// Uses AuthenticatedHttpClient for JWT auth + automatic 401 retry.
    /// </summary>
    public class EvidenceUploadQueue : IDisposable
    {
        private readonly EvidenceRepository _evidenceRepository;
        private readonly IScreenshotService _screenshotService;
        private readonly AuthenticatedHttpClient _httpClient;
        private readonly AuditLogSyncService? _auditLogSyncService;
        private readonly AuditLogMailDispatchService? _mailDispatchService;
        private readonly AuditRepository? _auditRepository;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;

        private readonly Timer _uploadTimer;
        private readonly SemaphoreSlim _uploadLock;
        private bool _disposed;

        // Configuration
        private readonly int _uploadIntervalSeconds = 30;
        private readonly int _batchSize = 10;
        private readonly TimeSpan _afterScreenshotGracePeriod = TimeSpan.FromMinutes(5);

        public const string UploadEndpoint = "/api/v1/Revit/evidence-images/with-images";

        public EvidenceUploadQueue(
            EvidenceRepository evidenceRepository,
            IScreenshotService screenshotService,
            AuthenticatedHttpClient httpClient,
            ILogger? logger = null,
            AuditLogSyncService? auditLogSyncService = null,
            OfflineQueueRepository? offlineQueue = null,
            AuditLogMailDispatchService? mailDispatchService = null,
            AuditRepository? auditRepository = null)
        {
            _evidenceRepository = evidenceRepository ?? throw new ArgumentNullException(nameof(evidenceRepository));
            _screenshotService = screenshotService ?? throw new ArgumentNullException(nameof(screenshotService));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _auditLogSyncService = auditLogSyncService;
            _mailDispatchService = mailDispatchService;
            _auditRepository = auditRepository;
            _offlineQueue = offlineQueue;
            _logger = logger;

            _uploadLock = new SemaphoreSlim(1, 1);

            // One-time reset of stuck failed uploads from previous sessions — gives them
            // another chance now that any earlier auth/projectId issues are fixed. Without
            // this, evidence rows that hit max_retries before today's fixes would stay
            // permanently invisible to GetPendingUploadsAsync, blocking mail dispatch.
            _ = Task.Run(async () =>
            {
                try { await _evidenceRepository.ResetStuckFailedUploadsAsync(); }
                catch (Exception ex) { _logger?.LogDebug($"Stuck-evidence reset failed: {ex.Message}"); }
            });

            // Start background upload timer
            _uploadTimer = new Timer(OnUploadTimerTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(_uploadIntervalSeconds));

            _logger?.LogInfo("Evidence upload queue started");
        }

        /// <summary>
        /// Manually trigger upload (for testing or immediate upload needs)
        /// </summary>
        public async Task TriggerUploadAsync()
        {
            await ProcessPendingUploadsAsync();
        }

        /// <summary>
        /// Background timer callback
        /// </summary>
        private async void OnUploadTimerTick(object? state)
        {
            try
            {
                await ProcessPendingUploadsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in upload timer tick: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Process pending evidence uploads grouped by audit_log_id.
        /// Each group produces one multipart POST with Image1 (before) and Image2 (after).
        /// </summary>
        private async Task ProcessPendingUploadsAsync()
        {
            if (!await _uploadLock.WaitAsync(0))
            {
                _logger?.LogDebug("Upload already in progress, skipping");
                return;
            }

            try
            {
                if (!_httpClient.IsAuthenticated)
                {
                    _logger?.LogDebug("Not authenticated, skipping evidence upload");
                    return;
                }

                // Pre-sync pending audit logs so evidence uploads can reference them (FK constraint)
                if (_auditLogSyncService != null)
                {
                    try
                    {
                        await _auditLogSyncService.UploadPendingAuditLogsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Pre-sync audit logs before evidence upload failed: {ex.Message}");
                    }
                }

                var pendingItems = await _evidenceRepository.GetPendingUploadsAsync(_batchSize);

                if (pendingItems.Count > 0)
                {
                    _logger?.LogInfo($"Processing {pendingItems.Count} pending evidence uploads");

                    // Group by audit_log_id — each group becomes one multipart upload
                    var grouped = pendingItems
                        .Where(e => !string.IsNullOrEmpty(e.AuditLogId))
                        .GroupBy(e => e.AuditLogId!)
                        .ToList();

                    // Also handle orphan evidence with no audit_log_id (upload individually)
                    var orphans = pendingItems
                        .Where(e => string.IsNullOrEmpty(e.AuditLogId))
                        .ToList();

                    foreach (var group in grouped)
                    {
                        await UploadEvidenceGroupAsync(group.Key, group.ToList());
                    }

                    foreach (var orphan in orphans)
                    {
                        await UploadEvidenceGroupAsync(null, new List<EvidenceRepository.EvidenceCapture> { orphan });
                    }
                }
                else
                {
                    _logger?.LogDebug("No pending evidence uploads");
                }

                // Notify server to send email for audit entries that require it.
                // Must run on every cycle — even when there are no pending evidence uploads —
                // because event protections (e.g. Import CAD) often have SendEmail=true with no
                // screenshot capture, so their audit entries never trigger an evidence upload yet
                // still need the server-side mail dispatch.
                if (_mailDispatchService != null)
                {
                    try
                    {
                        await _mailDispatchService.DispatchPendingMailAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Mail dispatch failed: {ex.Message}");
                    }
                }

                if (pendingItems.Count == 0)
                {
                    return;
                }

                // Cleanup old temp files after successful uploads
                var sessionIds = pendingItems.Select(e => e.SessionId).Distinct();
                foreach (var sessionId in sessionIds)
                {
                    await _screenshotService.CleanupTempFilesAsync(sessionId, olderThanHours: 24);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error processing pending uploads: {ex.Message}", ex);
            }
            finally
            {
                _uploadLock.Release();
            }
        }

        /// <summary>
        /// Upload a group of evidence (before + after) for one audit log entry as a single multipart request.
        /// POST /api/v1/Revit/evidence-images/with-images
        /// Fields: AuditLogId (required), Image1 + CapturedAt1 (before), Image2 + CapturedAt2 (after)
        /// </summary>
        private async Task UploadEvidenceGroupAsync(string? auditLogId, List<EvidenceRepository.EvidenceCapture> evidenceItems)
        {
            // Pick evidence items by stage. "applied" stage (from PinCommandBinding) maps to Image1.
            var beforeEvidence = evidenceItems.FirstOrDefault(e =>
                string.Equals(e.CaptureStage, "before", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.CaptureStage, "applied", StringComparison.OrdinalIgnoreCase));
            var afterEvidence = evidenceItems.FirstOrDefault(e =>
                string.Equals(e.CaptureStage, "after", StringComparison.OrdinalIgnoreCase));

            // Defer upload when "before" exists but "after" hasn't arrived yet and we're within the grace period.
            // This gives the user time to modify/delete the element so the after screenshot can be captured
            // and both images are sent together in a single multipart request.
            if (beforeEvidence != null && afterEvidence == null)
            {
                var elapsed = DateTime.UtcNow - beforeEvidence.CapturedAt;
                if (elapsed < _afterScreenshotGracePeriod)
                {
                    _logger?.LogDebug($"Deferring evidence upload for {beforeEvidence.EvidenceId}: " +
                        $"waiting for after screenshot (elapsed: {elapsed.TotalSeconds:F0}s / {_afterScreenshotGracePeriod.TotalSeconds:F0}s)");
                    return; // Leave as pending — will be retried on next timer tick
                }
                _logger?.LogInfo($"Grace period expired for {beforeEvidence.EvidenceId}, uploading before-only evidence");
            }

            // Use the first available evidence as the primary record
            var primaryEvidence = beforeEvidence ?? afterEvidence ?? evidenceItems.First();
            var evidenceId = primaryEvidence.EvidenceId;

            try
            {
                // AuditLogId is required by the server
                if (string.IsNullOrEmpty(auditLogId))
                {
                    _logger?.LogWarning($"Evidence group {evidenceId} has no AuditLogId — retrying audit log sync before next upload attempt");
                    // Attempt to upload any pending audit logs; on the next timer tick the evidence will be
                    // re-fetched and may have a valid AuditLogId if the audit log was just successfully synced.
                    if (_auditLogSyncService != null)
                    {
                        try { await _auditLogSyncService.UploadPendingAuditLogsAsync(); }
                        catch (Exception ex) { _logger?.LogDebug($"Audit log re-upload attempt failed: {ex.Message}"); }
                    }
                    // Leave as pending — do NOT mark as failed; retry on next timer tick
                    return;
                }

                // Verify file integrity for all items
                foreach (var evidence in evidenceItems)
                {
                    if (string.IsNullOrEmpty(evidence.FilePath) || !File.Exists(evidence.FilePath))
                    {
                        _logger?.LogWarning($"Evidence file not found: {evidence.EvidenceId} ({evidence.FilePath})");
                        await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "failed", error: "File not found");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(evidence.FileHashSha256))
                    {
                        var currentHash = await _screenshotService.CalculateFileHashAsync(evidence.FilePath);
                        if (currentHash != evidence.FileHashSha256)
                        {
                            _logger?.LogError($"File integrity check FAILED for {evidence.EvidenceId}");
                            await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "failed",
                                error: $"File hash mismatch (expected: {evidence.FileHashSha256}, actual: {currentHash})");
                            return;
                        }
                    }
                }

                // Mark all as uploading
                foreach (var evidence in evidenceItems)
                {
                    await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "uploading");
                }

                // Build multipart form data matching deployed API schema:
                // POST /api/v1/Revit/evidence-images/with-images
                // Fields: AuditLogId (required, uuid), Image1 (binary), CapturedAt1 (datetime), Image2 (binary), CapturedAt2 (datetime)
                using (var formData = new MultipartFormDataContent())
                {
                    // AuditLogId (required by server)
                    formData.Add(new StringContent(auditLogId), "AuditLogId");

                    // Image1 = Before screenshot + CapturedAt1
                    if (beforeEvidence != null && !string.IsNullOrEmpty(beforeEvidence.FilePath) && File.Exists(beforeEvidence.FilePath))
                    {
                        var beforeBytes = await Task.Run(() => File.ReadAllBytes(beforeEvidence.FilePath));
                        var beforeContent = new ByteArrayContent(beforeBytes);
                        beforeContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                        formData.Add(beforeContent, "Image1", Path.GetFileName(beforeEvidence.FilePath));
                        formData.Add(new StringContent(beforeEvidence.CapturedAt.ToString("o")), "CapturedAt1");
                    }

                    // Image2 = After screenshot + CapturedAt2
                    if (afterEvidence != null && !string.IsNullOrEmpty(afterEvidence.FilePath) && File.Exists(afterEvidence.FilePath))
                    {
                        var afterBytes = await Task.Run(() => File.ReadAllBytes(afterEvidence.FilePath));
                        var afterContent = new ByteArrayContent(afterBytes);
                        afterContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                        formData.Add(afterContent, "Image2", Path.GetFileName(afterEvidence.FilePath));
                        formData.Add(new StringContent(afterEvidence.CapturedAt.ToString("o")), "CapturedAt2");
                    }

                    // Upload via authenticated client
                    _logger?.LogInfo($"Uploading evidence group: {evidenceId} (auditLogId={auditLogId}, before={beforeEvidence != null}, after={afterEvidence != null})");
                    var response = await _httpClient.PostMultipartAsync(UploadEndpoint, formData);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"Evidence uploaded successfully: {evidenceId}");

                        foreach (var evidence in evidenceItems)
                        {
                            await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "completed");
                        }
                    }
                    else
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        var errorMsg = $"Upload failed: HTTP {response.StatusCode} - {errorBody}";
                        _logger?.LogWarning($"Evidence upload failed: {evidenceId} - {errorMsg}");

                        // 400 "not found" handling — distinguish two superficially-identical
                        // failure modes via the LOCAL synced flag:
                        //
                        //   synced=0 → audit log genuinely never reached server. The orphan
                        //              is real; mark evidence failed + sent_mail=1 to stop
                        //              the retry loop. (the original "zombie cleanup" case)
                        //
                        //   synced=1 → audit log IS on server. The 400 is a race condition
                        //              between AuditLogSyncService finishing (server has the
                        //              row in audit_log table) and the evidence endpoint
                        //              being able to see it (DB tx not yet visible / cache
                        //              not yet invalidated / replication lag). DO NOT mark
                        //              sent_mail=1 in this case — if we do, mail dispatch's
                        //              "WHERE synced=1 AND sent_mail=0" query will silently
                        //              skip this row forever and no email will EVER be sent
                        //              for an audit log that's actually on the server. That
                        //              was the "audit log on server, but mail not sent" bug.
                        //              Instead, queue the evidence for offline retry; the
                        //              normal mail dispatch path will pick this audit log up
                        //              on the next cycle once the server's view is consistent.
                        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                            errorBody.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        {
                            bool? isSynced = null;
                            if (_auditRepository != null && !string.IsNullOrEmpty(auditLogId))
                            {
                                try { isSynced = await _auditRepository.IsSyncedAsync(auditLogId); }
                                catch (Exception lookupEx) { _logger?.LogDebug($"IsSyncedAsync lookup failed for {auditLogId}: {lookupEx.Message}"); }
                            }

                            if (isSynced == true)
                            {
                                // Audit log IS on server — server-side race. Don't touch sent_mail.
                                // Re-queue evidence for retry so it eventually lands; the mail
                                // dispatch cycle will hit /send-mail for this audit log normally.
                                _logger?.LogInfo($"Evidence {evidenceId} 400 'not found' but audit log {auditLogId} IS synced (synced=1) — treating as transient race, re-queueing evidence (NOT marking sent_mail).");
                                await QueueEvidenceForOfflineSyncAsync(auditLogId, beforeEvidence, afterEvidence, evidenceItems, errorMsg);
                            }
                            else
                            {
                                // Real orphan: local audit log was never synced to server.
                                // Original cleanup behaviour — stop the retry loop.
                                foreach (var evidence in evidenceItems)
                                    await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "failed",
                                        error: "Audit log not found on server — discarding evidence");

                                if (_auditRepository != null && !string.IsNullOrEmpty(auditLogId))
                                {
                                    try
                                    {
                                        await _auditRepository.MarkMailSentAsync(auditLogId);
                                        _logger?.LogInfo($"Marked orphan audit log {auditLogId} as mail-handled (server has no record, synced={(isSynced.HasValue ? isSynced.Value.ToString() : "unknown")}).");
                                    }
                                    catch (Exception cleanupEx)
                                    {
                                        _logger?.LogDebug($"Orphan audit-log cleanup failed for {auditLogId}: {cleanupEx.Message}");
                                    }
                                }
                                _logger?.LogWarning($"Evidence {evidenceId} discarded — audit log {auditLogId} missing on server");
                            }
                        }
                        else
                        {
                            // Queue to offline queue for retry by OfflineSyncProcessor
                            await QueueEvidenceForOfflineSyncAsync(auditLogId, beforeEvidence, afterEvidence, evidenceItems, errorMsg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to upload evidence group {evidenceId}: {ex.Message}", ex);

                // Queue to offline queue for retry
                await QueueEvidenceForOfflineSyncAsync(auditLogId, beforeEvidence, afterEvidence, evidenceItems, ex.Message);
            }
        }

        /// <summary>
        /// Queue failed evidence upload to the offline queue for retry by OfflineSyncProcessor.
        /// Stores file paths and metadata so the processor can rebuild the multipart request.
        /// </summary>
        private async Task QueueEvidenceForOfflineSyncAsync(
            string? auditLogId,
            EvidenceRepository.EvidenceCapture? beforeEvidence,
            EvidenceRepository.EvidenceCapture? afterEvidence,
            List<EvidenceRepository.EvidenceCapture> evidenceItems,
            string errorMessage)
        {
            // Mark all as failed in evidence table
            foreach (var evidence in evidenceItems)
            {
                await _evidenceRepository.UpdateUploadStatusAsync(evidence.EvidenceId, "failed", error: errorMessage);
            }

            if (_offlineQueue == null)
            {
                _logger?.LogWarning("No offline queue available, evidence upload cannot be retried");
                return;
            }

            try
            {
                var payload = new EvidenceUploadPayload
                {
                    AuditLogId = auditLogId,
                    BeforeFilePath = beforeEvidence?.FilePath,
                    BeforeCapturedAt = beforeEvidence?.CapturedAt,
                    AfterFilePath = afterEvidence?.FilePath,
                    AfterCapturedAt = afterEvidence?.CapturedAt,
                    EvidenceIds = evidenceItems.Select(e => e.EvidenceId).ToList()
                };

                var operationData = new OfflineOperationData
                {
                    Endpoint = UploadEndpoint,
                    HttpMethod = "POST_MULTIPART",
                    Payload = payload,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = System.Text.Json.JsonSerializer.Serialize(operationData,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

                var sessionId = evidenceItems.FirstOrDefault()?.SessionId ?? "unknown";
                var operation = OfflineOperation.Create(
                    sessionId: sessionId,
                    operationType: OfflineApiWrapper.OperationTypes.EvidenceUpload,
                    operationData: jsonData,
                    priority: 4);

                await _offlineQueue.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued evidence upload to offline queue: auditLogId={auditLogId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue evidence upload to offline queue: {ex.Message}", ex);
            }
        }

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;

            _logger?.LogInfo("Disposing evidence upload queue");

            _uploadTimer?.Dispose();

            if (!(_uploadLock?.Wait(TimeSpan.FromSeconds(5)) ?? true))
                _logger?.LogWarning("Upload lock wait timed out during dispose");

            _uploadLock?.Dispose();

            _disposed = true;
        }

        #endregion
    }

    /// <summary>
    /// Payload stored in offline_queue for evidence upload replay.
    /// Contains file paths and metadata needed to rebuild the multipart request.
    /// </summary>
    public class EvidenceUploadPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("auditLogId")]
        public string? AuditLogId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("beforeFilePath")]
        public string? BeforeFilePath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("beforeCapturedAt")]
        public DateTime? BeforeCapturedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("afterFilePath")]
        public string? AfterFilePath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("afterCapturedAt")]
        public DateTime? AfterCapturedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("evidenceIds")]
        public List<string> EvidenceIds { get; set; } = new List<string>();
    }
}
