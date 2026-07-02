using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Auth.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Licensing
{
    /// <summary>
    /// Central license validation engine.
    /// Fetches license status from server API, manages offline grace period,
    /// handles passive mode, and supports periodic re-validation.
    /// </summary>
    public class LicenseValidator
    {
        private readonly LicenseCache _cache;
        private readonly LicensePolicyResolver _policyResolver;
        private readonly AuthTokenManager _tokenManager;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly ILogger? _logger;
        private Infrastructure.Api.SessionSyncService? _sessionSyncService;

        private static readonly TimeSpan GracePeriodDuration = TimeSpan.FromHours(72);
        private static readonly TimeSpan RevalidationInterval = TimeSpan.FromHours(4);
        private const int ExpirationWarnDays = 7;

        /// <summary>
        /// Fired when the license mode transitions (e.g., Licensed→Passive, Passive→Breached).
        /// </summary>
        public event EventHandler<LicenseStatusChangedEventArgs>? LicenseStatusChanged;

        public LicenseValidator(
            LicenseCache cache,
            LicensePolicyResolver policyResolver,
            ILogger? logger,
            AuthTokenManager tokenManager,
            AuthenticatedHttpClient? httpClient = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _policyResolver = policyResolver ?? throw new ArgumentNullException(nameof(policyResolver));
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Sets the session sync service for reading license data from heartbeat responses.
        /// Called after API services are registered (SessionSyncService is created after LicenseValidator).
        /// </summary>
        public void SetSessionSyncService(Infrastructure.Api.SessionSyncService syncService)
        {
            _sessionSyncService = syncService;
        }

        /// <summary>
        /// Current cached license info. Never null (returns default if not yet validated).
        /// </summary>
        public LicenseInfo CurrentInfo => _cache.CurrentLicenseInfo;

        /// <summary>
        /// Primary validation — call after auto-auth completes at startup.
        /// 1. If authenticated → fetch license status from server API → cache + persist
        /// 2. If server unreachable → evaluate offline grace period from persisted cache
        /// 3. If no cache → return Invalid
        /// </summary>
        public async Task<LicenseInfo> ValidateAsync()
        {
            var oldMode = _cache.Mode;
            bool isAuthenticated = _tokenManager.IsTokenValid || _tokenManager.HasStoredRefreshToken;

            // Step 1: Try to fetch from server if we have a valid token.
            // On cold start, the heartbeat response arrives after SessionSyncService
            // posts the session — wait up to FirstHeartbeatWaitSeconds for it to land
            // before falling through to cache/Invalid. Without this wait, ValidateAsync
            // runs before the first heartbeat and incorrectly marks a valid license
            // as Breached (status=Invalid) for the rest of the session.
            if (isAuthenticated)
            {
                var serverInfo = await FetchLicenseStatusFromServerAsync();
                if (serverInfo == null)
                {
                    // Wait briefly for the first heartbeat response to populate
                    serverInfo = await WaitForFirstHeartbeatAsync(FirstHeartbeatWaitSeconds);
                }

                if (serverInfo != null)
                {
                    _cache.SetLicenseInfo(serverInfo);
                    _logger?.LogInfo($"License validated: status={serverInfo.Status}, mode={serverInfo.Mode}, " +
                                    $"passive={serverInfo.IsPassiveMode}, seats={serverInfo.SeatUsageDisplay}");

                    RaiseIfModeChanged(oldMode, serverInfo.Mode);
                    return serverInfo;
                }
            }

            // Step 2: Server unreachable — evaluate grace period from persisted cache
            var cachedInfo = _cache.LoadPersistedCache();
            if (cachedInfo != null)
            {
                var graceResult = EvaluateGracePeriod(cachedInfo);
                _cache.SetLicenseInfo(graceResult);
                _logger?.LogInfo($"License from cache: status={graceResult.Status}, mode={graceResult.Mode}");

                RaiseIfModeChanged(oldMode, graceResult.Mode);
                return graceResult;
            }

            // Step 3: No cache available. Behavior differs by auth state:
            //   - Authenticated but no heartbeat yet → defer (return Pending) so UpdateFromHeartbeatResponse
            //     can finalize the license once the first heartbeat arrives. Features stay gated
            //     until then, but we don't flip to Breached prematurely.
            //   - Not authenticated → truly Invalid (user must sign in or register device)
            if (isAuthenticated)
            {
                var pendingInfo = LicenseInfo.CreateDefault();
                pendingInfo.Status = LicenseStatus.Pending;
                _cache.SetLicenseInfo(pendingInfo);
                _logger?.LogInfo("[License] Initial validation deferred — awaiting first heartbeat (authenticated but no cache)");

                RaiseIfModeChanged(oldMode, pendingInfo.Mode);
                return pendingInfo;
            }

            var invalidInfo = LicenseInfo.CreateDefault();
            invalidInfo.Status = LicenseStatus.Invalid;
            _cache.SetLicenseInfo(invalidInfo);
            _logger?.LogWarning("License validation: not authenticated and no cache — status Invalid");

            RaiseIfModeChanged(oldMode, invalidInfo.Mode);
            return invalidInfo;
        }

        /// <summary>
        /// Polls SessionSyncService.LastHeartbeatResponse briefly at startup so the first
        /// heartbeat can land before the initial license validation falls through to
        /// cache/Invalid. Returns null if no heartbeat arrives within the timeout.
        /// </summary>
        private static readonly TimeSpan FirstHeartbeatWaitSeconds = TimeSpan.FromSeconds(10);

        private async Task<LicenseInfo?> WaitForFirstHeartbeatAsync(TimeSpan timeout)
        {
            if (_sessionSyncService == null) return null;

            var deadline = DateTime.UtcNow + timeout;
            var pollInterval = TimeSpan.FromMilliseconds(250);

            while (DateTime.UtcNow < deadline)
            {
                var heartbeat = _sessionSyncService.LastHeartbeatResponse;
                if (heartbeat != null)
                    return MapHeartbeatToLicenseInfo(heartbeat);

                await Task.Delay(pollInterval);
            }

            _logger?.LogDebug($"[License] No heartbeat received within {timeout.TotalSeconds:F0}s — using cached/pending state");
            return null;
        }

        /// <summary>
        /// Synchronous validation fallback for contexts where async is not available.
        /// Only checks token validity and cached state — does NOT call the server.
        /// </summary>
        public LicenseStatus Validate()
        {
            if (_tokenManager.IsTokenValid)
            {
                _cache.SetStatus(LicenseStatus.Valid);
                return LicenseStatus.Valid;
            }

            var cached = _cache.LoadPersistedCache();
            if (cached != null)
            {
                var result = EvaluateGracePeriod(cached);
                _cache.SetLicenseInfo(result);
                return result.Status;
            }

            _cache.SetStatus(LicenseStatus.Invalid);
            return LicenseStatus.Invalid;
        }

        /// <summary>
        /// Returns true if enough time has passed since the last validation to warrant a re-check.
        /// </summary>
        public bool NeedsRevalidation()
        {
            return DateTime.UtcNow - _cache.LastChecked >= RevalidationInterval;
        }

        /// <summary>
        /// Forces a re-fetch of license status from the server.
        /// Call periodically from IdlingService (every 4 hours).
        /// </summary>
        public async Task<LicenseInfo> RevalidateAsync()
        {
            _logger?.LogInfo("License re-validation triggered");

            // Force token refresh to ensure we have a valid token
            try
            {
                await _tokenManager.GetAccessTokenAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Token refresh failed during re-validation: {ex.Message}");
            }

            return await ValidateAsync();
        }

        /// <summary>
        /// Checks if the license is approaching expiration.
        /// Returns warning info for 7/3/1 day thresholds.
        /// </summary>
        public (bool ShouldWarn, int DaysRemaining) CheckExpirationWarning()
        {
            var info = _cache.CurrentLicenseInfo;
            if (info.Expiration == null || info.Status == LicenseStatus.Expired)
                return (info.Status == LicenseStatus.Expired, 0);

            var daysRemaining = (int)(info.Expiration.Value - DateTime.UtcNow).TotalDays;

            if (daysRemaining <= 0)
                return (true, 0);

            if (daysRemaining <= ExpirationWarnDays)
                return (true, daysRemaining);

            return (false, daysRemaining);
        }

        /// <summary>
        /// Fetches license status from the server API via AuthenticatedHttpClient.
        /// Returns null if the call fails (server unreachable, auth error, etc.).
        /// </summary>
        private Task<LicenseInfo?> FetchLicenseStatusFromServerAsync()
        {
            // License data comes from the heartbeat response body (not a separate endpoint).
            // SessionSyncService.LastHeartbeatResponse is populated after each successful heartbeat PATCH.
            var heartbeat = _sessionSyncService?.LastHeartbeatResponse;
            if (heartbeat == null)
            {
                _logger?.LogDebug("No heartbeat response available — license data not yet received from server");
                return Task.FromResult<LicenseInfo?>(null);
            }

            var info = MapHeartbeatToLicenseInfo(heartbeat);
            _logger?.LogInfo($"License from heartbeat: mode={heartbeat.LicenseMode}, sessions={heartbeat.ActiveSessionCount}/{heartbeat.MaxUsers}");
            return Task.FromResult<LicenseInfo?>(info);
        }

        /// <summary>
        /// Maps the heartbeat response to our internal LicenseInfo model.
        /// LicenseMode: 0 = RestrictionMode (Breached), 1 = ActiveMode (Licensed), 2 = PassiveMode (Passive)
        /// </summary>
        private LicenseInfo MapHeartbeatToLicenseInfo(Infrastructure.Api.HeartbeatResponse heartbeat)
        {
            var isPassive = heartbeat.LicenseMode == 2;
            var isRestricted = heartbeat.LicenseMode == 0;

            return new LicenseInfo
            {
                Status = isRestricted ? LicenseStatus.Revoked : LicenseStatus.Valid,
                IsPassiveMode = isPassive,
                // Use server-provided modules if available, otherwise default all 5 for Active/Passive
                EnabledModules = isRestricted
                    ? new List<int>()
                    : (heartbeat.EnabledModules != null && heartbeat.EnabledModules.Count > 0)
                        ? heartbeat.EnabledModules
                        : new List<int> { 1, 2, 3, 4, 5 },
                // Preserve previous CompanyName when heartbeat omits it (current staging
                // server commonly does). Without this, every heartbeat would NULL-stomp the
                // value that DeviceStatusCommand/LicenseGuard wrote from the user identity,
                // leaving the Device Status dialog with an empty Company row until the next
                // open re-runs the fallback.
                CompanyName = !string.IsNullOrEmpty(heartbeat.CompanyName)
                    ? heartbeat.CompanyName
                    : _cache.CurrentLicenseInfo?.CompanyName,
                TotalSeats = heartbeat.MaxUsers,
                ActiveSeats = heartbeat.ActiveSessionCount,
                FetchedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Evaluates whether the cached license is within the offline grace period.
        /// Grace period: 72 hours from last successful validation.
        /// Weekend extension: if grace end falls on Sat/Sun, extend to next Tuesday EOD.
        /// </summary>
        private LicenseInfo EvaluateGracePeriod(LicenseInfo cachedInfo)
        {
            // If the cached status was already expired/revoked, stay that way
            if (cachedInfo.Status == LicenseStatus.Expired || cachedInfo.Status == LicenseStatus.Revoked)
            {
                _logger?.LogInfo($"Cached license is {cachedInfo.Status} — no grace period");
                return cachedInfo;
            }

            // Check if the license itself has expired by date
            if (cachedInfo.Expiration.HasValue && cachedInfo.Expiration.Value < DateTime.UtcNow)
            {
                cachedInfo.Status = LicenseStatus.Expired;
                _logger?.LogInfo("Cached license expiration date has passed");
                return cachedInfo;
            }

            var graceEnd = cachedInfo.FetchedAt + GracePeriodDuration;

            // Weekend extension: if grace end falls on Saturday or Sunday,
            // extend to next Tuesday 23:59:59 UTC
            if (graceEnd.DayOfWeek == DayOfWeek.Saturday)
                graceEnd = graceEnd.Date.AddDays(3).AddSeconds(-1); // Tuesday EOD
            else if (graceEnd.DayOfWeek == DayOfWeek.Sunday)
                graceEnd = graceEnd.Date.AddDays(2).AddSeconds(-1); // Tuesday EOD

            if (DateTime.UtcNow < graceEnd)
            {
                var remaining = graceEnd - DateTime.UtcNow;
                _logger?.LogInfo($"License in grace period — {remaining.TotalHours:F1}h remaining (ends {graceEnd:u})");
                cachedInfo.Status = LicenseStatus.GracePeriod;
                return cachedInfo;
            }

            // Grace period expired
            _logger?.LogWarning("License grace period expired — no server validation within 72h");
            cachedInfo.Status = LicenseStatus.Invalid;
            return cachedInfo;
        }

        /// <summary>
        /// Updates the license cache directly from a heartbeat response.
        /// Lightweight — no token refresh, no HTTP call. Called after every heartbeat.
        /// Fires LicenseStatusChanged if the mode transitions.
        /// </summary>
        public void UpdateFromHeartbeatResponse(Infrastructure.Api.HeartbeatResponse response)
        {
            if (response == null) return;
            var oldMode = _cache.Mode;
            var info = MapHeartbeatToLicenseInfo(response);
            _cache.SetLicenseInfo(info);
            _logger?.LogDebug($"[License] Heartbeat update: mode={info.Mode}, seats={info.ActiveSeats}/{info.TotalSeats}");
            RaiseIfModeChanged(oldMode, info.Mode);
        }

        private void RaiseIfModeChanged(LicenseMode oldMode, LicenseMode newMode)
        {
            if (oldMode != newMode)
            {
                _logger?.LogInfo($"License mode changed: {oldMode} → {newMode}");
                LicenseStatusChanged?.Invoke(this, new LicenseStatusChangedEventArgs(oldMode, newMode));
            }
        }
    }

    /// <summary>
    /// Event args for license mode transitions.
    /// </summary>
    public class LicenseStatusChangedEventArgs : EventArgs
    {
        public LicenseMode OldMode { get; }
        public LicenseMode NewMode { get; }

        public LicenseStatusChangedEventArgs(LicenseMode oldMode, LicenseMode newMode)
        {
            OldMode = oldMode;
            NewMode = newMode;
        }
    }
}
