using System;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Notifies the server to send email for audit log entries that require mail dispatch.
    /// The plugin does NOT send email — it calls POST /api/v1/Revit/audit-logs/{id}/send-mail
    /// to signal the server to handle the actual email delivery.
    /// Entries with sent_mail = 0 (false) are pending; sent_mail = 1 means no mail needed or already dispatched.
    /// </summary>
    public class AuditLogMailDispatchService
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly AuditRepository _auditRepository;
        private readonly ILogger? _logger;

        private const string EndpointTemplate = "/api/v1/Revit/audit-logs/{0}/send-mail";

        // Circuit breaker — prevents the 30K-spam pattern observed 2026-05-04 (15 MB log
        // file, ~3 401s/sec for 16 hours straight) where the upload-queue invoked dispatch
        // every 30 s, every entry returned 401, and there was no back-off. After
        // _authFailureThreshold consecutive cycles where the FIRST request returned 401,
        // dispatch is suppressed until _suppressUntil to give token refresh / re-sign-in a
        // chance without us hammering the server in the meantime. Any successful request
        // resets the counter.
        private const int _authFailureThreshold = 3;
        private static readonly TimeSpan _authFailureBackoff = TimeSpan.FromMinutes(5);
        private DateTime _suppressUntil = DateTime.MinValue;
        private int _consecutiveAuthFailureCycles;
        private readonly object _circuitBreakerLock = new object();

        public AuditLogMailDispatchService(
            AuthenticatedHttpClient? httpClient,
            AuditRepository auditRepository,
            ILogger? logger = null)
        {
            _httpClient = httpClient;
            _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
            _logger = logger;

            _logger?.LogInfo($"AuditLogMailDispatchService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Notify the server to send email for all pending audit log entries (sent_mail = 0).
        /// Must be called AFTER audit logs are synced AND evidence is uploaded,
        /// so the server has the full audit record + screenshots when composing the email.
        /// </summary>
        public async Task<int> DispatchPendingMailAsync(int batchSize = 50)
        {
            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogDebug("Mail dispatch skipped: not authenticated");
                    return 0;
                }

                // Circuit breaker: skip the entire batch if we recently exhausted auth.
                // Reset state when the suppression window has elapsed so we attempt one
                // probe request to detect recovery.
                lock (_circuitBreakerLock)
                {
                    if (DateTime.UtcNow < _suppressUntil)
                    {
                        _logger?.LogDebug($"Mail dispatch suppressed until {_suppressUntil:HH:mm:ss}Z (auth circuit-breaker after {_consecutiveAuthFailureCycles} consecutive 401 cycles).");
                        return 0;
                    }
                }

                var pendingEntries = await _auditRepository.GetPendingMailDispatchAsync(batchSize);

                if (pendingEntries.Count == 0)
                {
                    // Diagnostic: distinguish "no entries pending mail" from "entries are blocked
                    // by the 60-second post-evidence-upload hold" or "entries waiting on sync".
                    var diag = await _auditRepository.GetMailDispatchDiagnosticsAsync();
                    if (diag.TotalUnsent > 0)
                    {
                        _logger?.LogInfo(
                            $"Mail dispatch idle: 0 ready, {diag.UnsyncedCount} waiting for audit-log sync, " +
                            $"{diag.WaitingOnEvidence} waiting on evidence upload " +
                            $"({diag.StuckEvidence} of which are PERMANENTLY STUCK at max_retries — manual reset required), " +
                            $"{diag.WaitingOnHold} held by 60-second post-upload hold (total unsent: {diag.TotalUnsent}).");
                    }
                    return 0;
                }

                _logger?.LogInfo($"Dispatching mail notifications for {pendingEntries.Count} audit log entries");

                var successCount = 0;
                var batchAuthFailed = false;

                foreach (var entry in pendingEntries)
                {
                    try
                    {
                        var endpoint = string.Format(EndpointTemplate, entry.AuditLogId);
                        var response = await _httpClient.PostAsync(endpoint, null, requireAdminToken: true);

                        if (response.IsSuccessStatusCode)
                        {
                            await _auditRepository.MarkMailSentAsync(entry.AuditLogId);
                            successCount++;
                            _logger?.LogDebug($"Mail dispatch notified for audit log: {entry.AuditLogId}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // Audit log doesn't exist on server — mark as sent to stop retrying
                            await _auditRepository.MarkMailSentAsync(entry.AuditLogId);
                            _logger?.LogWarning($"Audit log {entry.AuditLogId} not found on server (404), marking mail as dispatched");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                        {
                            // Server rejected the request (e.g., "Email sending is not enabled")
                            // Mark as sent to stop retrying — server won't send mail for this entry
                            await _auditRepository.MarkMailSentAsync(entry.AuditLogId);
                            var body = await response.Content.ReadAsStringAsync();
                            _logger?.LogWarning($"Mail dispatch rejected for {entry.AuditLogId} (400), marking as dispatched: {body}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            // Auth is currently broken — there's no point trying the rest of this
                            // batch (every request will 401). Bail out of the loop and let the
                            // circuit-breaker logic below decide whether to suppress future
                            // dispatch cycles. The unsent entry stays at sent_mail=0 so the next
                            // cycle (after auth recovers) picks it up. AuthenticatedHttpClient
                            // already fired NotifyAuthExhausted upstream when there's no refresh
                            // path, which triggers force-logout → user re-signs → next cycle
                            // succeeds.
                            batchAuthFailed = true;
                            var skipped = pendingEntries.Count - (pendingEntries.IndexOf(entry) + 1);
                            _logger?.LogWarning($"Mail dispatch hit 401 Unauthorized — auth is currently broken. " +
                                $"Stopping batch (skipped {skipped} remaining entries) to avoid hammering the server.");
                            break;
                        }
                        else
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            _logger?.LogWarning($"Mail dispatch failed for {entry.AuditLogId}: {response.StatusCode} - {body}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Mail dispatch error for {entry.AuditLogId}: {ex.Message}");
                    }
                }

                // Circuit breaker bookkeeping. Trip after _authFailureThreshold consecutive
                // batches that hit 401 on the FIRST request — that's a strong signal auth is
                // broken in a way the user must fix (sign in again). Successful or partially-
                // successful batches reset the counter.
                bool authExhausted = false;
                lock (_circuitBreakerLock)
                {
                    if (batchAuthFailed && successCount == 0)
                    {
                        _consecutiveAuthFailureCycles++;
                        if (_consecutiveAuthFailureCycles >= _authFailureThreshold)
                        {
                            _suppressUntil = DateTime.UtcNow.Add(_authFailureBackoff);
                            _logger?.LogWarning($"Mail dispatch SUPPRESSED until {_suppressUntil:HH:mm:ss}Z " +
                                $"after {_consecutiveAuthFailureCycles} consecutive auth-failed cycles. " +
                                $"Will probe again in {_authFailureBackoff.TotalMinutes:F0} minutes.");
                            // Signal that this is a true auth-exhausted state, not a
                            // transient 401. Triggered when 3 batches in a row hit 401
                            // despite the inner AuthenticatedHttpClient retry-with-refresh
                            // succeeding to obtain a NEW bearer — that pattern means the
                            // server has blacklisted the AccessId (a refresh-issued token
                            // inherits the same AccessId) and the only recovery is for the
                            // user to sign in fresh. Without this, the user stays silently
                            // suppressed for 5 min while emails go unsent.
                            authExhausted = true;
                        }
                    }
                    else if (successCount > 0)
                    {
                        if (_consecutiveAuthFailureCycles > 0)
                            _logger?.LogInfo($"Mail dispatch auth recovered — clearing circuit breaker (had {_consecutiveAuthFailureCycles} prior failed cycles).");
                        _consecutiveAuthFailureCycles = 0;
                        _suppressUntil = DateTime.MinValue;
                    }
                }

                // Fire OUTSIDE the lock — NotifyAuthExhausted may dispatch UI events
                // (force-logout dialog) and we don't want to hold the circuit-breaker lock
                // across that.
                if (authExhausted && _httpClient != null)
                {
                    _logger?.LogWarning("Mail dispatch: notifying auth-exhausted to trigger user re-sign-in (AccessId likely blacklisted on server).");
                    try { _httpClient.NotifyAuthExhausted(wasAdminAttempt: true); }
                    catch (Exception notifyEx)
                    {
                        _logger?.LogWarning($"Mail dispatch: NotifyAuthExhausted threw — {notifyEx.Message}");
                    }
                }

                if (successCount > 0)
                {
                    _logger?.LogInfo($"Mail dispatch complete: {successCount}/{pendingEntries.Count} notified");
                }

                return successCount;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Mail dispatch batch failed: {ex.Message}", ex);
                return 0;
            }
        }
    }
}
