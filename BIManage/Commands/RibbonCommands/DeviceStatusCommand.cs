using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeviceStatusCommand : IExternalCommand
    {
        internal static PushButton? RibbonButton;
        internal static bool _isDeviceRegistered = false;

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
                    TaskDialog.Show("Device Status", "Unable to access application services.");
                    return Result.Failed;
                }

                var tokenManager = services.GetService<AuthTokenManager>();
                var authApi = services.GetService<AuthApiService>();
                var logger = services.GetService<ILogger>();
                var machineId = MachineIdentifier.GetMachineId(logger);

                if (tokenManager != null && (tokenManager.IsAuthenticated || tokenManager.HasStoredRefreshToken))
                {
                    // ✓ REGISTERED — update icon and show status dialog
                    var licenseCache = services.GetService<global::BIManage.Licensing.LicenseCache>();
                    bool isPassive = licenseCache?.Mode == global::BIManage.Licensing.LicenseMode.Passive;

                    // Resolve session sync service and session ID
                    var sessionSync = services.GetService<global::BIManage.Infrastructure.Api.SessionSyncService>();
                    string? sessionId = null;
                    try
                    {
                        var ctx = services.GetService<global::BIManage.Revit.Context.IRevitContext>();
                        sessionId = ctx?.SessionId.ToString();
                    }
                    catch { }

                    // Resolve admin role label and company name from user identity
                    string? adminTypeLabel = null;
                    string? companyName = null;
                    try
                    {
                        var userService = services.GetService<global::BIManage.Core.Identity.IUserService>();
                        adminTypeLabel = userService?.CurrentRole switch
                        {
                            global::BIManage.Core.Identity.UserRole.CompanyAdministrator => "Company Admin",
                            global::BIManage.Core.Identity.UserRole.ProjectAdministrator => "Project Admin",
                            _ => null
                        };
                        companyName = userService?.CurrentUser?.CompanyName;

                        // Device-only users never go through SignInViewModel, so userService.CurrentUser
                        // is populated from Windows identity only (no CompanyName). Fall back to the
                        // persisted SecureTokenStorage identity that SetTokensFromDevice keeps fresh
                        // from the device-auth response.
                        if (string.IsNullOrEmpty(companyName))
                        {
                            var secureStorage = services.GetService<SecureTokenStorage>();
                            companyName = secureStorage?.LoadUserIdentity()?.CompanyName;
                        }
                    }
                    catch { }

                    var licenseInfo = licenseCache?.CurrentLicenseInfo;
                    // Identity is authoritative for the displayed company name — always
                    // overwrite when we have one, don't just fill-if-empty. The heartbeat
                    // path can leave a stale value in LicenseInfo.CompanyName (server omits
                    // companyName in current staging responses), and a fill-if-empty guard
                    // lets the stale value survive long after the user's identity has been
                    // refreshed to a different company.
                    if (licenseInfo != null && !string.IsNullOrEmpty(companyName))
                        licenseInfo.CompanyName = companyName;

                    var statusDialog = new BIManageRevit.BIManage.Views.Auth.DeviceStatusDialog(
                        isRegistered: true,
                        machineId: machineId,
                        isPassive: isPassive,
                        sessionSyncService: sessionSync,
                        sessionId: sessionId,
                        adminTypeLabel: adminTypeLabel,
                        licenseInfo: licenseInfo);
                    new System.Windows.Interop.WindowInteropHelper(statusDialog)
                    {
                        Owner = commandData.Application.MainWindowHandle
                    };

                    // Refresh ribbon icon based on live
                    //
                    //
                    //
                    //
                    //
                    //
                    //
                    // license mode
                    // A successful heartbeat = device registered; icon color reflects license state
                    statusDialog.LiveStatusResolved += (isActive, licenseMode) =>
                    {
                        if (licenseMode == 0) // Breached
                            UpdateButtonAppearance(false);
                        else if (licenseMode == 2) // Passive
                            SetButtonYellow();
                        else // Licensed (1) or other — device is healthy
                            UpdateButtonAppearance(true);
                    };

                    statusDialog.ShowDialog();
                }
                else
                {
                    // ✗ NOT REGISTERED — show red icon + license key dialog
                    UpdateButtonAppearance(false);

                    var dialog = new BIManageRevit.BIManage.Views.Auth.LicenseKeyDialog(machineId);
                    new System.Windows.Interop.WindowInteropHelper(dialog)
                    {
                        Owner = commandData.Application.MainWindowHandle
                    };
                    var result = dialog.ShowDialog();

                    if (result == true && !string.IsNullOrWhiteSpace(dialog.LicenseKey) && authApi != null && tokenManager != null)
                    {
                        try
                        {
                            var response = Task.Run(() => authApi.RegisterDeviceAsync(dialog.LicenseKey.Trim(), machineId)).GetAwaiter().GetResult();

                            if (response?.AccessToken != null)
                            {
                                tokenManager.SetTokensFromDevice(response);

                                if (!string.IsNullOrEmpty(response.CompanyId))
                                {
                                    var signalRConfig = services.GetService<global::BIManage.Infrastructure.SignalR.Core.SignalRConfiguration>();
                                    if (signalRConfig != null)
                                        signalRConfig.CompanyId = response.CompanyId;
                                }

                                logger?.LogInfo($"Device registered successfully: {machineId}");
                                UpdateButtonAppearance(true);

                                // Show success
                                var successDialog = new BIManageRevit.BIManage.Views.Auth.DeviceStatusDialog(true, machineId);
                                new System.Windows.Interop.WindowInteropHelper(successDialog)
                                {
                                    Owner = commandData.Application.MainWindowHandle
                                };
                                successDialog.ShowDialog();
                            }
                            else
                            {
                                // Show not registered status
                                var failDialog = new BIManageRevit.BIManage.Views.Auth.DeviceStatusDialog(false, machineId);
                                new System.Windows.Interop.WindowInteropHelper(failDialog)
                                {
                                    Owner = commandData.Application.MainWindowHandle
                                };
                                failDialog.ShowDialog();
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning($"Device registration failed: {ex.Message}");
                            var errorDialog = new BIManageRevit.BIManage.Views.Auth.DeviceStatusDialog(false, machineId);
                            new System.Windows.Interop.WindowInteropHelper(errorDialog)
                            {
                                Owner = commandData.Application.MainWindowHandle
                            };
                            errorDialog.ShowDialog();
                        }
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                try
                {
                    var lg = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.Services
                        ?.GetService<global::BIManage.Infrastructure.Logging.ILogger>();
                    lg?.LogError($"DeviceStatusCommand failed: {ex.Message}", ex);
                }
                catch { }
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Active toast reference — dismissed when device becomes registered.</summary>
        internal static BIManageRevit.BIManage.Views.Auth.DeviceWarningToast? ActiveToast;

        public static void UpdateButtonAppearance(bool isRegistered)
        {
            _isDeviceRegistered = isRegistered;
            SetButtonIcon(isRegistered ? "DeviceGreen" : "DeviceRed");

            // Always dismiss toast when device status changes to registered
            if (isRegistered)
                DismissToast();
        }

        public static void DismissToast()
        {
            if (ActiveToast == null) return;
            try
            {
                // Set flag — toast polls this and closes itself on its own thread
                ActiveToast.ShouldDismiss = true;
                ActiveToast = null;
            }
            catch { ActiveToast = null; }
        }

        public static void SetButtonYellow()
        {
            SetButtonIcon("DeviceYellow");
        }

        private static void SetButtonIcon(string iconName)
        {
            try
            {
                if (RibbonButton == null) return;

                // Determine icon folder based on Revit theme
                var iconFolder = "Icons";
                try
                {
                    var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                    var isDark = app?.GetType().GetField("_isDarkTheme",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                    if (isDark != null && (bool)(isDark.GetValue(app) ?? false))
                        iconFolder = "IconsWhite";
                }
                catch { }

                var icon32 = new BitmapImage();
                icon32.BeginInit();
                icon32.UriSource = new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{iconName}_32.png");
                icon32.CacheOption = BitmapCacheOption.OnLoad;
                icon32.EndInit();
                icon32.Freeze();

                var icon16 = new BitmapImage();
                icon16.BeginInit();
                icon16.UriSource = new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{iconName}_16.png");
                icon16.CacheOption = BitmapCacheOption.OnLoad;
                icon16.EndInit();
                icon16.Freeze();

                RibbonButton.LargeImage = icon32;
                RibbonButton.Image = icon16;
            }
            catch { }
        }
    }
}
