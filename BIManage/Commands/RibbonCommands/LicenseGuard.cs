using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Logging;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Shared license check — returns true if license is breached (caller should abort).
    /// Shows the Device Status dialog so the user can re-enter their license key.
    /// </summary>
    internal static class LicenseGuard
    {
        /// <summary>
        /// Returns true if the license is breached and the command should not proceed.
        /// Automatically opens the Device Status dialog when breached.
        /// </summary>
        internal static bool IsBlockedByLicense(ExternalCommandData commandData)
        {
            var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            var services = app?.GetType().GetField("_services",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

            if (services == null) return false;

            var licenseCache = services.GetService<global::BIManage.Licensing.LicenseCache>();
            if (licenseCache == null) return false;

            if (licenseCache.Mode != global::BIManage.Licensing.LicenseMode.Breached)
                return false;

            // License is breached — show Device Status dialog
            var tokenManager = services.GetService<global::BIManage.Infrastructure.Auth.AuthTokenManager>();
            bool isRegistered = tokenManager != null && (tokenManager.IsAuthenticated || tokenManager.HasStoredRefreshToken);
            var logger = services.GetService<ILogger>();
            var machineId = MachineIdentifier.GetMachineId(logger);

            var sessionSync = services.GetService<global::BIManage.Infrastructure.Api.SessionSyncService>();
            string? sessionId = null;
            try
            {
                var ctx = services.GetService<global::BIManage.Revit.Context.IRevitContext>();
                sessionId = ctx?.SessionId.ToString();
            }
            catch { }

            var licenseInfo = licenseCache.CurrentLicenseInfo;
            // Identity is authoritative for the displayed company name — always overwrite
            // when we have one, don't just fill-if-empty (the heartbeat path can leave a
            // stale value in LicenseInfo.CompanyName that would otherwise outlive a tenant
            // change). See DeviceStatusCommand for the matching mutation.
            try
            {
                var userService = services.GetService<global::BIManage.Core.Identity.IUserService>();
                var freshCompanyName = userService?.CurrentUser?.CompanyName;

                // Device-only users never go through SignInViewModel, so userService.CurrentUser
                // has no CompanyName. Fall back to the SecureTokenStorage identity that
                // SetTokensFromDevice keeps fresh from the device-auth response.
                if (string.IsNullOrEmpty(freshCompanyName))
                {
                    var secureStorage = services.GetService<global::BIManage.Infrastructure.Auth.SecureTokenStorage>();
                    freshCompanyName = secureStorage?.LoadUserIdentity()?.CompanyName;
                }

                if (licenseInfo != null && !string.IsNullOrEmpty(freshCompanyName))
                    licenseInfo.CompanyName = freshCompanyName;
            }
            catch { }

            var statusDialog = new BIManageRevit.BIManage.Views.Auth.DeviceStatusDialog(
                isRegistered: isRegistered,
                machineId: machineId,
                isPassive: false,
                sessionSyncService: sessionSync,
                sessionId: sessionId,
                licenseInfo: licenseInfo);
            new System.Windows.Interop.WindowInteropHelper(statusDialog)
            {
                Owner = commandData.Application.MainWindowHandle
            };
            statusDialog.ShowDialog();

            return true;
        }
    }
}
