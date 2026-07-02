using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Licensing;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Ribbon command that displays the current license status dialog.
    /// Shows: license mode (Green/Yellow/Red), modules, seat usage, company, expiration.
    /// Available to all users (AlwaysAvailable).
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LicenseStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("ZeManage — License", "Unable to access application services.");
                    return Result.Failed;
                }

                var licenseCache = services.GetService<LicenseCache>();
                var licenseValidator = services.GetService<LicenseValidator>();
                var tokenManager = services.GetService<AuthTokenManager>();
                var userService = services.GetService<global::BIManage.Core.Identity.IUserService>();
                var logger = services.GetService<ILogger>();

                if (licenseCache == null)
                {
                    TaskDialog.Show("ZeManage — License", "License service not available.");
                    return Result.Failed;
                }

                var info = licenseCache.CurrentLicenseInfo;
                var machineId = MachineIdentifier.GetMachineId(logger);

                // Build license status summary
                var modeLabel = info.Mode switch
                {
                    LicenseMode.Licensed => "LICENSED (Active)",
                    LicenseMode.Passive => "PASSIVE MODE (Seats Exceeded)",
                    LicenseMode.Breached => "NOT LICENSED",
                    _ => "Unknown"
                };

                var statusIcon = info.Mode switch
                {
                    LicenseMode.Licensed => "🟢",
                    LicenseMode.Passive => "🟡",
                    LicenseMode.Breached => "🔴",
                    _ => "⚪"
                };

                var expirationText = info.Expiration.HasValue
                    ? info.Expiration.Value.ToString("yyyy-MM-dd")
                    : "N/A";

                var roleText = userService?.CurrentRole.ToString() ?? "Unknown";
                var companyText = !string.IsNullOrEmpty(info.CompanyName)
                    ? info.CompanyName
                    : info.CompanyId ?? "N/A";

                // Module status table
                var moduleStatus = "";
                foreach (LicenseModule module in Enum.GetValues(typeof(LicenseModule)))
                {
                    var enabled = info.IsModuleEnabled(module);
                    var icon = enabled ? "✓" : "✗";
                    moduleStatus += $"\n  {icon}  {module}";
                }

                var body = $"Status: {statusIcon} {modeLabel}" +
                           $"\n\nCompany: {companyText}" +
                           $"\nSeats: {info.SeatUsageDisplay}" +
                           $"\nExpiration: {expirationText}" +
                           $"\nUser Role: {roleText}" +
                           $"\nDevice ID: {machineId}" +
                           $"\n\nModules:{moduleStatus}" +
                           $"\n\nLast Checked: {licenseCache.LastChecked:yyyy-MM-dd HH:mm:ss UTC}";

                if (info.Status == LicenseStatus.GracePeriod)
                {
                    var graceEnd = info.FetchedAt + TimeSpan.FromHours(72);
                    var remaining = graceEnd - DateTime.UtcNow;
                    if (remaining.TotalHours > 0)
                        body += $"\n\nGrace Period: {remaining.TotalHours:F0} hours remaining";
                }

                var dialog = new TaskDialog("ZeManage — License Status")
                {
                    MainInstruction = modeLabel,
                    MainContent = body,
                    MainIcon = info.Mode == LicenseMode.Licensed
                        ? TaskDialogIcon.TaskDialogIconNone
                        : info.Mode == LicenseMode.Passive
                            ? TaskDialogIcon.TaskDialogIconWarning
                            : TaskDialogIcon.TaskDialogIconError
                };

                dialog.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                try
                {
                    var lg = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.Services
                        ?.GetService<global::BIManage.Infrastructure.Logging.ILogger>();
                    lg?.LogError($"LicenseStatusCommand failed: {ex.Message}", ex);
                }
                catch { }
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
