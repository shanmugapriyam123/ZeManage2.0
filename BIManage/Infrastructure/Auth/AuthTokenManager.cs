using System;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Auth.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Auth
{
    /// <summary>
    /// Central token lifecycle manager.
    /// Tracks two distinct access tokens in separate slots:
    /// - admin token (from /admin-login) — has profileId claim, used for admin-scoped writes
    /// - device token (from /validate-and-authenticate) — no profileId, used for heartbeats/sessions/registration
    /// Each token has its own expiry. Persisted refresh token is routed by the active session flag.
    /// Access tokens are held in memory only (never persisted).
    /// </summary>
    public class AuthTokenManager
    {
        private readonly SecureTokenStorage _secureStorage;
        private readonly AuthApiService _authApi;
        private readonly Core.Features.IFeatureToggleService? _featureToggle;
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        // In-memory only — admin token (has profileId, for admin-scoped requests)
        private string? _adminAccessToken;
        private DateTime _adminExpiresAt = DateTime.MinValue;

        // In-memory only — device token (no profileId, for device-scoped requests)
        private string? _deviceAccessToken;
        private DateTime _deviceExpiresAt = DateTime.MinValue;

        // Tracks whether the active session is admin (drives refresh-endpoint routing)
        private bool _isAdminSession;

        public AuthTokenManager(SecureTokenStorage secureStorage, AuthApiService authApi, ILogger? logger = null, Core.Features.IFeatureToggleService? featureToggle = null)
        {
            _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
            _authApi = authApi ?? throw new ArgumentNullException(nameof(authApi));
            _featureToggle = featureToggle;
            _logger = logger;
        }

        /// <summary>
        /// Whether any usable access token is currently in memory and not expired.
        /// </summary>
        public bool IsTokenValid => HasValidAdminToken || HasValidDeviceToken;

        /// <summary>
        /// Whether the active session's access token is near expiry (within 60 seconds).
        /// </summary>
        public bool IsNearExpiry
        {
            get
            {
                if (_isAdminSession && !string.IsNullOrEmpty(_adminAccessToken))
                    return DateTime.UtcNow >= _adminExpiresAt.AddSeconds(-60);
                if (!string.IsNullOrEmpty(_deviceAccessToken))
                    return DateTime.UtcNow >= _deviceExpiresAt.AddSeconds(-60);
                return false;
            }
        }

        public bool IsAuthenticated => IsTokenValid;
        public bool IsAdminSession => _isAdminSession;

        private bool HasValidAdminToken =>
            !string.IsNullOrEmpty(_adminAccessToken) && DateTime.UtcNow < _adminExpiresAt;

        private bool HasValidDeviceToken =>
            !string.IsNullOrEmpty(_deviceAccessToken) && DateTime.UtcNow < _deviceExpiresAt;

        /// <summary>
        /// Raised when the server explicitly rejects the refresh token (400/401/403).
        /// Subscribers should trigger UI logout (clear identity, update ribbon, show dialog).
        /// Parameter: wasAdminSession — true if the rejected session was an admin login.
        /// </summary>
        public event Action<bool> TokenRejected;

        /// <summary>
        /// Raised when an authenticated request hits 401 AND there is no path forward
        /// without user intervention (no stored refresh token to retry with). Distinct
        /// from <see cref="TokenRejected"/>:
        ///   - <see cref="TokenRejected"/> = server explicitly rejected the refresh token.
        ///   - <see cref="AuthExhausted"/> = client has no refresh token at all (cleared,
        ///     never persisted, or storage tampered) — the only fix is for the user to
        ///     sign in again.
        /// Subscribers should trigger the same UI logout flow as <see cref="TokenRejected"/>.
        /// Throttled internally so transient failures (network, 5xx) cannot cause repeated
        /// logout dialogs in a short window.
        /// Parameter: wasAdminAttempt — true if the failing request needed an admin token.
        /// </summary>
        public event Action<bool> AuthExhausted;

        /// <summary>
        /// Fires when a previously-announced AuthExhausted state is cleared because
        /// fresh tokens were issued (typically via OfflineSyncProcessor's device
        /// re-validation 30s after a force-logout). Subscribers should attempt to
        /// re-establish connections that were torn down — most importantly SignalR,
        /// which otherwise stays disconnected until next process restart even though
        /// valid tokens are now available.
        /// </summary>
        public event Action TokensReissuedAfterExhaustion;

        // Throttle: a single AuthExhausted event per cooldown so a burst of failed
        // requests (e.g. on dialog open that fans out 4 admin-scoped fetches at once)
        // doesn't queue 4 logout dialogs.
        // PLUS a sticky "already announced" gate: once the AuthExhausted event has fired
        // and shown the user the "please sign in" dialog, suppress further events until
        // tokens are re-issued via SetTokens*. Otherwise OfflineSyncProcessor's 30 s timer
        // keeps hitting admin-scoped 401s and the 2 min cooldown re-arms every 2 min,
        // producing a permanent dialog loop until session restart (10+ observed in the
        // wild on a customer machine with stale admin-auth.dat).
        private DateTime _lastAuthExhaustedAt = DateTime.MinValue;
        private bool _authExhaustedAnnounced;
        private static readonly TimeSpan AuthExhaustedCooldown = TimeSpan.FromMinutes(2);
        private readonly object _authExhaustedLock = new object();

        /// <summary>
        /// Internal trigger called when an authenticated request fails because the refresh
        /// pipeline returned null AND no recovery path exists. Throttled by
        /// <see cref="AuthExhaustedCooldown"/>. Returns true if the event was actually
        /// raised this call (i.e. not throttled).
        /// </summary>
        internal bool NotifyAuthExhausted(bool wasAdminAttempt)
        {
            // GUARD: For admin-attempt exhaustion, only surface the user-visible
            // "Your admin session has expired" dialog if the user actually IS in an
            // admin role. Otherwise we're spamming a regular user with a message
            // that asks them to "sign in again to continue managing protections" —
            // a session they never had.
            //
            // Background services (AuditLogMailDispatchService, CommandProtectionSync,
            // etc.) call admin-required endpoints unconditionally for any signed-in
            // user; for a non-admin those calls 401 forever, and without this guard
            // the AuthExhausted dialog loops every 10-15 minutes (sticky-cleared by
            // SetTokensFromDevice from background OfflineSyncProcessor re-auths).
            if (wasAdminAttempt && !BIManage.Core.Identity.RoleFlagStore.HasAdminPrivileges)
            {
                _logger?.LogDebug("AuthExhausted (admin) suppressed — current user has no admin role flags. " +
                                 "A background service made an admin-required call that 401'd; not surfacing to user.");
                return false;
            }

            // Suppress when there's no signed-in user at all — e.g. fresh install before
            // first sign-in. The "auth exhausted" condition is meaningless if the user
            // has never authenticated and the call site is just speculatively probing.
            // Heuristic: if BOTH admin and device slots are empty AND no refresh token is
            // stored, assume never-signed-in and skip the event.
            var neverAuthenticated = string.IsNullOrEmpty(_adminAccessToken)
                                  && string.IsNullOrEmpty(_deviceAccessToken)
                                  && !HasStoredRefreshToken
                                  && _lastAuthExhaustedAt == DateTime.MinValue;
            if (neverAuthenticated)
            {
                _logger?.LogDebug("AuthExhausted suppressed — never authenticated this session.");
                return false;
            }

            lock (_authExhaustedLock)
            {
                // Sticky-suppress: if we already announced this exhausted state and the user
                // hasn't re-signed-in (no SetTokens* call cleared the flag), don't keep
                // surfacing the same dialog every 2 minutes. The user has already been told.
                if (_authExhaustedAnnounced)
                    return false;

                var now = DateTime.UtcNow;
                if (now - _lastAuthExhaustedAt < AuthExhaustedCooldown)
                    return false;
                _lastAuthExhaustedAt = now;
                _authExhaustedAnnounced = true;
            }

            _logger?.LogWarning($"Auth exhausted (wasAdminAttempt={wasAdminAttempt}) — refresh token unavailable. " +
                "User must sign in again. (Further AuthExhausted events suppressed until re-sign-in.)");
            try { AuthExhausted?.Invoke(wasAdminAttempt); }
            catch (Exception ex) { _logger?.LogWarning($"AuthExhausted handler error: {ex.Message}"); }
            return true;
        }

        /// <summary>
        /// Resets the sticky AuthExhausted suppression. Called from SetTokens* paths after a
        /// successful sign-in so a subsequent unrelated auth-exhausted condition can surface
        /// the dialog again. Idempotent.
        /// </summary>
        /// <summary>Applies the admin active/inactive toggle read from a device-auth/refresh
        /// response to FeatureToggleService — the HTTP-polling fallback for
        /// EmployeeActivationListener's SignalR fast path. Every successful device token
        /// acquisition (validate-device, refresh, both happen frequently) re-confirms this.</summary>
        private void ApplyCaptureState(bool isActive)
        {
            if (_featureToggle == null) return;
            if (_featureToggle.IsEmployeeCaptureDisabled == isActive)
            {
                _featureToggle.IsEmployeeCaptureDisabled = !isActive;
                _logger?.LogInfo($"Capture state confirmed via auth response: {(isActive ? "enabled" : "disabled (admin deactivated)")}");
            }
        }

        private void ClearAuthExhaustedAnnouncement()
        {
            bool wasAnnounced;
            lock (_authExhaustedLock)
            {
                wasAnnounced = _authExhaustedAnnounced;
                if (_authExhaustedAnnounced)
                {
                    _authExhaustedAnnounced = false;
                    _lastAuthExhaustedAt = DateTime.MinValue;
                    _logger?.LogInfo("AuthExhausted suppression cleared (tokens re-issued).");
                }
            }
            // Fire OUTSIDE the lock so subscribers (e.g. SignalRService.ConnectAsync)
            // don't deadlock against our internal monitor if they call back into the
            // token manager during the reconnect.
            if (wasAnnounced)
            {
                try { TokensReissuedAfterExhaustion?.Invoke(); }
                catch (Exception ex) { _logger?.LogWarning($"TokensReissuedAfterExhaustion handler error: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Generic setter (legacy): writes into the active session's slot and persists the refresh token.
        /// </summary>
        public void SetTokens(string accessToken, string refreshToken, DateTime expiresAt)
        {
            if (_isAdminSession)
            {
                _adminAccessToken = accessToken;
                _adminExpiresAt = expiresAt;
            }
            else
            {
                _deviceAccessToken = accessToken;
                _deviceExpiresAt = expiresAt;
            }
            _secureStorage.SaveRefreshToken(refreshToken);
            ClearAuthExhaustedAnnouncement();
            _logger?.LogInfo($"Tokens stored ({(_isAdminSession ? "admin" : "device")} slot). Expires at: {expiresAt:yyyy-MM-dd HH:mm:ss} UTC");
        }

        /// <summary>
        /// Stores tokens from an admin login response into the admin slot.
        /// Marks the active session as admin so subsequent refreshes use /admin-refresh.
        /// Does NOT touch the device slot.
        /// </summary>
        public void SetTokensFromLogin(LoginResponse response)
        {
            if (response?.AccessToken == null || response.RefreshToken == null)
            {
                _logger?.LogWarning("Login response missing tokens");
                return;
            }

            _isAdminSession = true;
            var expiry = response.TokenExpiry ?? DateTime.UtcNow.AddHours(1);
            _adminAccessToken = response.AccessToken;
            _adminExpiresAt = expiry;
            // Save the admin refresh token to its OWN slot so device-side flows can't
            // overwrite it across restarts. Also save to the shared auth.dat for backward
            // compatibility with any caller that still reads LoadRefreshToken().
            _secureStorage.SaveRefreshToken(response.RefreshToken);
            _secureStorage.SaveAdminRefreshToken(response.RefreshToken);
            ClearAuthExhaustedAnnouncement();
            _logger?.LogInfo($"Admin token stored (expires {expiry:yyyy-MM-dd HH:mm:ss} UTC). Will use /admin-refresh.");
        }

        /// <summary>
        /// Stores tokens from a device auth response into the device slot.
        /// CRITICAL: this no longer overwrites the admin slot — both tokens coexist
        /// so admin-scoped requests keep using the admin token while heartbeats/sessions
        /// use the device token. Per backend analysis: admin and device tokens carry
        /// different claims (admin has profileId; device does not), so admin-only endpoints
        /// (command/event/rule protections) reject device tokens with a misleading 400.
        /// </summary>
        public void SetTokensFromDevice(DeviceAuthResponse response)
        {
            if (response?.AccessToken == null || response.RefreshToken == null)
            {
                _logger?.LogWarning("Device auth response missing tokens");
                return;
            }

            var expiry = response.TokenExpiry ?? DateTime.UtcNow.AddHours(1);
            _deviceAccessToken = response.AccessToken;
            _deviceExpiresAt = expiry;
            ClearAuthExhaustedAnnouncement();
            ApplyCaptureState(response.IsActive);
            _logger?.LogInfo($"Device token stored (expires {expiry:yyyy-MM-dd HH:mm:ss} UTC). Admin token preserved={(HasValidAdminToken)}.");

            // Persist company name from the device-auth response so device-only users
            // (no admin-login round-trip) still see their tenant in the Device Status
            // dialog. Identity restore on the next launch — and DeviceStatusCommand's
            // SecureTokenStorage fallback in the current session — will pick it up.
            _secureStorage.UpdateStoredCompanyName(response.CompanyName, response.CompanyId);

            // Only persist the device refresh token AND switch the session flag if there is no
            // active admin session. If an admin is signed in, keep their refresh token and flag
            // so admin-scoped requests continue to refresh via /admin-refresh.
            if (!_isAdminSession)
            {
                _secureStorage.SaveRefreshToken(response.RefreshToken);
                // Only clear ProfileId if the stored identity says the user is NOT an admin role.
                // Previously this ran unconditionally, which formed a self-reinforcing loop:
                // a device-refresh path with _isAdminSession=false wiped ProfileId → next boot
                // RestoreAdminSessionFlag(true) skipped because ProfileId is empty → _isAdminSession
                // stays false → device-refresh wipes ProfileId again. Admin users got stuck refreshing
                // through /device-refresh forever and every admin-scoped request 401'd.
                var storedIdentity = _secureStorage.LoadUserIdentity();
                var storedIsAdmin = storedIdentity?.IsCompanyAdmin == true || storedIdentity?.IsProjectAdmin == true;
                if (!storedIsAdmin)
                {
                    try { _secureStorage.ClearProfileId(); } catch { }
                }
                else
                {
                    _logger?.LogDebug("Stored identity is admin — preserving ProfileId so next-boot RestoreAdminSessionFlag can fire.");
                }
            }
            else
            {
                _logger?.LogDebug("Admin session active — device refresh token NOT persisted (admin refresh token preserved).");
            }
        }

        /// <summary>
        /// Returns a valid access token for general use, preferring the admin slot when
        /// an admin session is active (so admin-scoped requests carry profileId). Falls
        /// back to the device token for non-admin contexts. Auto-refreshes when expired.
        /// </summary>
        public async Task<string?> GetAccessTokenAsync()
        {
            if (_isAdminSession)
            {
                var adminToken = await GetAdminAccessTokenAsync();
                if (!string.IsNullOrEmpty(adminToken)) return adminToken;
            }
            return await GetDeviceAccessTokenAsync();
        }

        /// <summary>
        /// Returns the currently-cached admin access token (no refresh attempt, no awaits).
        /// Returns null if no token is stored or the user isn't in an admin session.
        /// Use this when you need the token synchronously — e.g. attaching it to a
        /// fire-and-forget call right before ClearTokens() wipes local state.
        /// </summary>
        public string? GetCachedAdminAccessToken()
            => _isAdminSession ? _adminAccessToken : null;

        /// <summary>
        /// Returns a valid admin access token (with profileId claim). Use for admin-scoped
        /// writes (command/event/rule protections, audit-log writes). Returns null if the
        /// user isn't signed in as admin or refresh fails.
        /// </summary>
        public async Task<string?> GetAdminAccessTokenAsync()
        {
            if (HasValidAdminToken && DateTime.UtcNow < _adminExpiresAt.AddSeconds(-60))
                return _adminAccessToken;

            await _refreshLock.WaitAsync();
            try
            {
                if (HasValidAdminToken && DateTime.UtcNow < _adminExpiresAt.AddSeconds(-60))
                    return _adminAccessToken;

                if (!_isAdminSession)
                {
                    _logger?.LogDebug("GetAdminAccessTokenAsync: not an admin session — no admin token available.");
                    return null;
                }

                _logger?.LogInfo("Admin token expired/near-expiry — refreshing via /admin-refresh.");
                return await RefreshAccessTokenAsync(forceAdmin: true);
            }
            finally { _refreshLock.Release(); }
        }

        /// <summary>
        /// Returns a valid device access token. Use for heartbeats, sessions, model registration.
        /// </summary>
        public async Task<string?> GetDeviceAccessTokenAsync()
        {
            if (HasValidDeviceToken && DateTime.UtcNow < _deviceExpiresAt.AddSeconds(-60))
                return _deviceAccessToken;

            await _refreshLock.WaitAsync();
            try
            {
                if (HasValidDeviceToken && DateTime.UtcNow < _deviceExpiresAt.AddSeconds(-60))
                    return _deviceAccessToken;

                _logger?.LogInfo("Device token expired/near-expiry — refreshing.");
                // Device-token refresh path: only routes through device refresh when admin
                // session is not active (otherwise we'd consume the admin refresh token).
                return await RefreshAccessTokenAsync(forceAdmin: false);
            }
            finally { _refreshLock.Release(); }
        }

        /// <summary>
        /// Refreshes the access token using the stored refresh token.
        /// Routes to /admin-refresh or /refresh based on either the explicit override or
        /// the current session flag. Clears the stored refresh token only when the server
        /// explicitly rejects it (400/401/403). Network errors preserve the stored token.
        /// </summary>
        private async Task<string?> RefreshAccessTokenAsync(bool? forceAdmin = null)
        {
            var useAdminRefresh = forceAdmin ?? _isAdminSession;
            // Admin refreshes use ONLY the dedicated admin-auth.dat slot.
            //
            // REGRESSION FIX (2026-05-25): the previous code fell back to the shared
            // auth.dat slot when admin-auth.dat was missing ("for older installs"). But
            // auth.dat holds a DEVICE refresh token, and the server rejects device
            // tokens at /admin-refresh with:
            //   400 BadRequest "Device refresh tokens must use the refresh endpoint"
            // The catch(TokenRejectedException) handler then wiped EVERY token and
            // auto-logged the user out — even though their device session was healthy.
            // Observed in the field on 2026-05-25: every boot where admin-auth.dat
            // had been lost (machine wipe, manual cleanup, dev iteration) resulted in
            // a forced logout 1 second after launch.
            //
            // For device refresh, the behavior is unchanged: read auth.dat.
            string? refreshToken;
            if (useAdminRefresh)
            {
                refreshToken = _secureStorage.LoadAdminRefreshToken();
                if (string.IsNullOrEmpty(refreshToken))
                {
                    // PROACTIVE FORCE RE-SIGN-IN (added 2026-05-26): admin-auth.dat slot is
                    // empty but the session is still flagged as admin in memory. The PREVIOUS
                    // behaviour was to silently demote to device-only and let admin endpoints
                    // 401 on the next call — that meant every admin operation (rule save,
                    // event toggle, audit log POST, mail dispatch) failed with 401 until the
                    // user manually signed out + in. User-reported pattern: "401 vara koodathu",
                    // "sila neram velai, sila neram velai illa".
                    //
                    // The ONLY way the admin slot becomes empty while the role flag is admin
                    // is a half-cleared state from a prior force-logout, migration, or aborted
                    // refresh. Best UX: fire NotifyAuthExhausted RIGHT HERE so the Sign-In
                    // dialog appears at the FIRST admin call instead of after an embarrassing
                    // 401 round-trip. The 401 is then never visible to the user.
                    _logger?.LogWarning("Admin refresh requested but admin-auth.dat slot is empty — forcing re-sign-in (skipping silent demote to avoid downstream 401s).");
                    _isAdminSession = false;
                    try { NotifyAuthExhausted(wasAdminAttempt: true); }
                    catch (Exception nx) { _logger?.LogWarning($"NotifyAuthExhausted from empty admin slot threw: {nx.Message}"); }
                    return null;
                }
            }
            else
            {
                refreshToken = _secureStorage.LoadRefreshToken();
            }

            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger?.LogWarning("No refresh token available for token refresh");
                ClearInMemoryTokens(useAdminRefresh);
                return null;
            }

            try
            {
                var response = useAdminRefresh
                    ? await _authApi.AdminRefreshTokenAsync(refreshToken)
                    : await _authApi.RefreshTokenAsync(refreshToken);

                if (response?.AccessToken != null && response.RefreshToken != null)
                {
                    var expiry = response.TokenExpiry ?? DateTime.UtcNow.AddHours(1);
                    if (useAdminRefresh)
                    {
                        _adminAccessToken = response.AccessToken;
                        _adminExpiresAt = expiry;
                        // Rotate the admin refresh token to its own slot so the next
                        // restart can re-issue an admin token even if a device flow
                        // overwrites auth.dat in the meantime.
                        _secureStorage.SaveAdminRefreshToken(response.RefreshToken);
                    }
                    else
                    {
                        _deviceAccessToken = response.AccessToken;
                        _deviceExpiresAt = expiry;
                        ApplyCaptureState(response.IsActive);
                    }
                    _secureStorage.SaveRefreshToken(response.RefreshToken);

                    // Device-refresh response carries companyName; keep stored identity
                    // current so a server-side rename eventually propagates without
                    // requiring an admin-login round-trip. Admin-refresh response shape
                    // is the same but the admin login path already maintains the full
                    // identity, so no-op for it (UpdateStoredCompanyName ignores empties).
                    if (!useAdminRefresh)
                        _secureStorage.UpdateStoredCompanyName(response.CompanyName);

                    _logger?.LogInfo($"Token refreshed successfully ({(useAdminRefresh ? "admin" : "device")})");
                    return useAdminRefresh ? _adminAccessToken : _deviceAccessToken;
                }

                _logger?.LogWarning("Token refresh returned empty tokens (preserving stored refresh token for retry)");
                ClearInMemoryTokens(useAdminRefresh);
                return null;
            }
            catch (TokenRejectedException ex)
            {
                _logger?.LogWarning($"Token refresh rejected by server: {ex.Message}");
                var wasAdmin = useAdminRefresh;

                // Scope the wipe to the slot that was rejected:
                //   Admin refresh rejected   → clear admin only, keep device session alive.
                //   Device refresh rejected  → device session is dead, clear everything and force re-sign-in.
                // The previous unscoped ClearTokens() caused a single rejected admin
                // refresh (or a wrong-endpoint upload-the-bug-above) to auto-logout
                // a perfectly valid device-only user.
                if (wasAdmin)
                {
                    ClearAdminTokens();
                    _logger?.LogInfo("Admin refresh rejected — admin slot cleared, device session preserved.");
                }
                else
                {
                    ClearTokens();
                }

                try { TokenRejected?.Invoke(wasAdmin); }
                catch (Exception evEx) { _logger?.LogWarning($"TokenRejected event handler error: {evEx.Message}"); }
                return null;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _logger?.LogWarning($"Token refresh failed (network error, preserving refresh token): {ex.Message}");
                ClearInMemoryTokens(useAdminRefresh);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogWarning($"Token refresh timed out (preserving refresh token): {ex.Message}");
                ClearInMemoryTokens(useAdminRefresh);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Token refresh failed: {ex.Message}", ex);
                ClearInMemoryTokens(useAdminRefresh);
                return null;
            }
        }

        /// <summary>
        /// Clears all tokens (memory + secure storage). Used on explicit logout only.
        /// </summary>
        public void ClearTokens()
        {
            _adminAccessToken = null;
            _adminExpiresAt = DateTime.MinValue;
            _deviceAccessToken = null;
            _deviceExpiresAt = DateTime.MinValue;
            _isAdminSession = false;
            _secureStorage.ClearTokens();
            _logger?.LogInfo("All tokens cleared (admin + device + stored refresh token)");
        }

        /// <summary>
        /// Clears only the admin token slot and flips the active session back to device-scope.
        /// Used when the server downgrades this session (e.g. another admin logged in elsewhere) —
        /// the user remains signed in via the device token, but admin-scoped requests will require
        /// re-authentication.
        /// </summary>
        public void ClearAdminTokens()
        {
            _adminAccessToken = null;
            _adminExpiresAt = DateTime.MinValue;
            _isAdminSession = false;
            _logger?.LogInfo("Admin token cleared (device token preserved — session downgraded to NormalUser)");
        }

        private void ClearInMemoryTokens(bool clearAdminSlot)
        {
            if (clearAdminSlot)
            {
                _adminAccessToken = null;
                _adminExpiresAt = DateTime.MinValue;
            }
            else
            {
                _deviceAccessToken = null;
                _deviceExpiresAt = DateTime.MinValue;
            }
            _logger?.LogDebug($"In-memory {(clearAdminSlot ? "admin" : "device")} token cleared (stored refresh token preserved)");
        }

        /// <summary>
        /// Restores admin session state from persisted identity.
        /// Must be called before GetAccessTokenAsync() during startup
        /// so the correct refresh endpoint (admin-refresh) is used.
        /// </summary>
        public void RestoreAdminSessionFlag(bool isAdmin)
        {
            // Don't trust a cached admin flag if there's no refresh token in secure storage
            // to back it up. Without the refresh token, every admin-scoped request will 401
            // forever (audit log sync, evidence upload, mail dispatch all break) and the user
            // is stuck in a "half-signed-in" state — the UI thinks they're an admin but
            // nothing admin-scoped works. Cleaner to demote to device-only on startup so the
            // Sign In button reappears and the user can authenticate fresh.
            if (isAdmin)
            {
                // Admin slot is the authoritative source; fall back to the shared auth.dat
                // slot for older installs that signed in before the dedicated slot existed.
                var adminToken = _secureStorage?.LoadAdminRefreshToken();
                var anyRefreshToken = !string.IsNullOrEmpty(adminToken)
                    ? adminToken
                    : _secureStorage?.LoadRefreshToken();
                if (string.IsNullOrEmpty(anyRefreshToken))
                {
                    _logger?.LogWarning("Admin session flag was True but no refresh token in secure storage — clearing admin flag (user must Sign In fresh).");
                    _isAdminSession = false;
                    return;
                }
            }
            _isAdminSession = isAdmin;
            _logger?.LogInfo($"Admin session flag restored: {isAdmin}");
        }

        /// <summary>
        /// Forces both access tokens to expire immediately so the next request triggers refresh.
        /// </summary>
        public void ForceExpireAccessToken()
        {
            _adminExpiresAt = DateTime.MinValue;
            _deviceExpiresAt = DateTime.MinValue;
            _logger?.LogInfo("Access tokens force-expired for refresh");
        }

        public bool HasStoredRefreshToken => _secureStorage.HasStoredToken;

        /// <summary>
        /// True only when an admin access token CAN be issued without the user re-signing in.
        /// Requires both an admin session flag AND an admin refresh token on disk (or a fallback
        /// shared refresh token from older installs). Used by OfflineSyncProcessor to short-circuit
        /// admin-scoped operations when the user has dropped to device-only — prevents the 401-spam
        /// loop where every queued admin POST/PUT hits the wire without a token, gets 401, force-
        /// expires the cached tokens, fails to refresh (no admin slot), and repeats every 30s.
        /// </summary>
        public bool CanIssueAdminToken =>
            _isAdminSession
            && (_secureStorage.HasStoredAdminRefreshToken || _secureStorage.HasStoredToken);
    }
}
