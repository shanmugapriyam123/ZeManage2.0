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
    /// <summary>
    /// Listens for server-pushed ForceLogout messages.
    /// Triggered when an admin revokes access OR when a duplicate admin login elsewhere
    /// requires this session to either fully log out or just drop admin privileges.
    ///
    /// Modes (selected via payload.Mode):
    ///  - "Logout"  (default, backward compat) → clear tokens + reset identity to Windows user.
    ///  - "Downgrade" → keep user logged in but revoke admin flags (user becomes NormalUser).
    ///                  Used when another admin signs in elsewhere and the server wants to
    ///                  hand over the admin seat without disrupting the user's Revit session.
    /// </summary>
    public class ForceLogoutListener : SignalListenerBase
    {
        private readonly AuthTokenManager _tokenManager;
        private readonly IUserService _userService;
        private readonly ISignalREventBus _eventBus;

        public override string Name => "ForceLogout";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.ForceLogout
        };

        public ForceLogoutListener(
            AuthTokenManager tokenManager,
            IUserService userService,
            ISignalREventBus eventBus,
            ILogger logger) : base(logger)
        {
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        protected override Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            var payload = GetPayloadSafe<ForceLogoutPayload>(message);
            var mode = (payload?.Mode ?? "Logout").Trim();
            var isDowngrade = string.Equals(mode, "Downgrade", StringComparison.OrdinalIgnoreCase);

            var defaultReason = isDowngrade
                ? "Another admin signed in elsewhere — your admin privileges have been revoked. You remain signed in as a regular user."
                : "Your session was terminated by an administrator";
            var reason = string.IsNullOrWhiteSpace(payload?.Reason) ? defaultReason : payload.Reason;

            if (isDowngrade)
            {
                _logger?.LogWarning($"[ForceLogout/Downgrade] Revoking admin privileges. Reason: {reason}");

                // Drop admin slot token only — keep the device token so the user stays signed in.
                _tokenManager.ClearAdminTokens();

                // Demote local identity: NormalUser. RibbonVisibilityManager reacts to RoleChanged
                // and hides admin-only buttons automatically.
                _userService.UpdateAdminFlags(isCompanyAdmin: false, isProjectAdmin: false);

                _eventBus.Publish(new ForceLogoutEvent
                {
                    Reason = reason,
                    Source = Name + "/Downgrade"
                });

                ShowUserNotification("Admin Privileges Revoked", reason);

                _logger?.LogInfo("[ForceLogout/Downgrade] Admin flags cleared, user remains signed in as NormalUser.");
                return Task.CompletedTask;
            }

            _logger?.LogWarning($"[ForceLogout] Received. Reason: {reason}");

            _tokenManager.ClearTokens();
            _userService.Logout();

            _eventBus.Publish(new ForceLogoutEvent
            {
                Reason = reason,
                Source = Name
            });

            _logger?.LogInfo("[ForceLogout] Tokens cleared and user reset.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Show a non-blocking notification dialog on the WPF dispatcher thread.
        /// Falls back to log-only if no dispatcher is available (headless/test contexts).
        /// </summary>
        private void ShowUserNotification(string title, string message)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        System.Windows.MessageBox.Show(
                            message,
                            title,
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"[ForceLogout] Notification dialog failed: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ForceLogout] Dispatcher unavailable for notification: {ex.Message}");
            }
        }

        private class ForceLogoutPayload
        {
            /// <summary>Human-readable reason shown to the user.</summary>
            public string Reason { get; set; }

            /// <summary>
            /// "Logout" (default) for full logout, "Downgrade" to keep user signed in as NormalUser.
            /// Backward compat: missing/null = "Logout".
            /// </summary>
            public string Mode { get; set; }

            public DateTime? Timestamp { get; set; }
        }
    }
}
