using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Core.Identity;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    // Auto-logout on role change via SignalR ForceTokenRefresh.
    /// <summary>
    /// Listens for server-pushed ForceTokenRefresh messages.
    /// Triggered when a user's role or permissions change — the current JWT needs to be
    /// replaced with a new one carrying updated claims.
    /// Forces immediate token expiry then calls GetAccessTokenAsync() to obtain a fresh JWT.
    /// Per user spec: if the user's admin role actually flipped between the snapshot taken
    /// BEFORE refresh and the identity AFTER refresh (e.g. Company Admin → Project Admin or
    /// vice versa), the same flow as ForceLogout (mode="Logout") is invoked so the user is
    /// kicked out and forced to sign in again with the new role.
    /// </summary>
    public class ForceTokenRefreshListener : SignalListenerBase
    {
        private readonly AuthTokenManager _tokenManager;
        private readonly IUserService _userService;
        private readonly ISignalREventBus _eventBus;
        private readonly SecureTokenStorage? _secureStorage;

        public override string Name => "ForceTokenRefresh";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.ForceTokenRefresh
        };

        public ForceTokenRefreshListener(
            AuthTokenManager tokenManager,
            IUserService userService,
            ISignalREventBus eventBus,
            ILogger logger,
            SecureTokenStorage? secureStorage = null) : base(logger)
        {
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _secureStorage = secureStorage; // Optional — needed to wipe persisted UserIdentity.
        }

        protected override async Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            var payload = GetPayloadSafe<ForceRefreshPayload>(message);
            var reason = payload?.Reason ?? "Token refresh requested by server";

            _logger?.LogInfo($"[ForceTokenRefresh] Received. Reason: {reason}");

            // Force token expiry so GetAccessTokenAsync() triggers a real refresh
            _tokenManager.ForceExpireAccessToken();

            // Trigger refresh immediately — obtains new JWT with updated permissions
            var token = await _tokenManager.GetAccessTokenAsync();
            var refreshed = !string.IsNullOrEmpty(token);

            _logger?.LogInfo($"[ForceTokenRefresh] Token refresh {(refreshed ? "succeeded" : "failed")}");

            _eventBus.Publish(new ForceTokenRefreshEvent
            {
                Reason = reason,
                TokenRefreshed = refreshed,
                Source = Name
            });

            // ── Auto-logout on ForceTokenRefresh ─────────────────────────────────
            // Per user spec: the server only emits ForceTokenRefresh when a user's
            // role/permissions have changed (e.g. Company Admin ⇆ Project Admin),
            // so this signal should always result in the user being signed out and
            // forced to re-authenticate against the new role. The previous "detect
            // role swap from CurrentUser flags" check never fired because
            // GetAccessTokenAsync just rotates the JWT — it does NOT parse the
            // new claims back into _userService.CurrentUser, so the pre/post
            // IsCompanyAdmin/IsProjectAdmin values were always identical and the
            // logout branch was skipped. Always running the ForceLogout flow here
            // matches the user's expected behaviour.
            _logger?.LogWarning("[ForceTokenRefresh] Role/permission change signaled by server — running auto-logout.");

            // Mirror SignInViewModel.SignOut() so the signout sticks across restart:
            //   1. ClearTokens()        — wipes admin + device tokens + refresh token.
            //   2. ClearUserIdentity()  — removes the persisted UserIdentity blob from
            //                             %APPDATA%, otherwise the old admin identity is
            //                             auto-restored on the next Revit launch.
            //   3. UserService.Logout() — resets in-memory CurrentUser to Windows user
            //                             and fires RoleChanged, which the ribbon listens
            //                             to and hides admin-only buttons.
            //   4. UserDisplayNameCache.Clear() — drops the display-name cache so any
            //                             stale admin name disappears from UIs.
            _tokenManager.ClearTokens();
            try { _secureStorage?.ClearUserIdentity(); }
            catch (Exception ex) { _logger?.LogDebug($"[ForceTokenRefresh] ClearUserIdentity failed: {ex.Message}"); }
            _userService.Logout();
            try { UserDisplayNameCache.Clear(); } catch { }

            _eventBus.Publish(new ForceLogoutEvent
            {
                Reason = "Your role was changed by an administrator. Please sign in again.",
                Source = Name + "/RoleSwap"
            });

            // Surface a visible notification to the user so they know what happened.
            // Without this, the SignalR-driven logout is invisible — tokens are gone
            // but the user just sees admin buttons disappear and doesn't know why.
            ShowUserNotification(
                "Role Changed",
                "Your role was changed by an administrator. You have been signed out — please sign in again to continue.");

            _logger?.LogInfo("[ForceTokenRefresh] Tokens cleared, identity wiped, and user reset after role-change signal.");
        }

        /// <summary>
        /// Non-blocking notification on the WPF dispatcher thread. Falls back to
        /// log-only if no dispatcher is available (headless / test contexts).
        /// </summary>
        private void ShowUserNotification(string title, string message)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        System.Windows.MessageBox.Show(
                            message, title,
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"[ForceTokenRefresh] Notification dialog failed: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ForceTokenRefresh] Dispatcher unavailable for notification: {ex.Message}");
            }
        }

        private class ForceRefreshPayload
        {
            public string Reason { get; set; }
            public DateTime? Timestamp { get; set; }
        }
    }
}
