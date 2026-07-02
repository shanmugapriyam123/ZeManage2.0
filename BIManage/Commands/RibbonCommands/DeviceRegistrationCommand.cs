using System;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using Microsoft.VisualBasic;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to register the device with a license key.
    /// This enables API sync functionality for sessions, models, and metrics.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeviceRegistrationCommand : IExternalCommand
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
                    TaskDialog.Show("Device Registration", "Unable to access application services.");
                    return Result.Failed;
                }

                var authApi = services.GetService<AuthApiService>();
                var tokenManager = services.GetService<AuthTokenManager>();
                var logger = services.GetService<ILogger>();

                if (authApi == null || tokenManager == null)
                {
                    TaskDialog.Show("Device Registration", "Authentication services not available.\nCheck App.config for ApiBaseUrl setting.");
                    return Result.Failed;
                }

                // Check if already authenticated
                if (tokenManager.IsAuthenticated || tokenManager.HasStoredRefreshToken)
                {
                    var result = TaskDialog.Show(
                        "Device Registration",
                        "This device is already registered.\nDo you want to re-register with a new license key?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                    if (result != TaskDialogResult.Yes)
                    {
                        return Result.Cancelled;
                    }
                }

                // Get machine ID
                var machineId = MachineIdentifier.GetMachineId(logger);
                logger?.LogInfo($"Device registration requested for machine: {machineId}");

                // Prompt for license key using VB InputBox
                var licenseKey = Interaction.InputBox(
                    "Enter your license key to register this device:\n\n" +
                    $"Machine ID: {machineId}",
                    "Device Registration",
                    "");

                if (string.IsNullOrWhiteSpace(licenseKey))
                {
                    logger?.LogInfo("Device registration cancelled - no license key entered");
                    return Result.Cancelled;
                }

                // Register the device (async call)
                logger?.LogInfo($"Registering device with license key...");

                // Use Task.Run to avoid deadlock — .Result on UI thread blocks the
                // SynchronizationContext that the async continuation needs to resume on
                var response = Task.Run(() => authApi.RegisterDeviceAsync(licenseKey.Trim(), machineId)).GetAwaiter().GetResult();

                if (response?.AccessToken != null)
                {
                    // Store tokens
                    tokenManager.SetTokensFromDevice(response);

                    logger?.LogInfo($"Device registered successfully: {machineId}");

                    TaskDialog.Show(
                        "Device Registration",
                        "Device registered successfully!\n\n" +
                        "API sync is now enabled.\n" +
                        "Sessions, models, and metrics will sync automatically.");

                    return Result.Succeeded;
                }
                else
                {
                    logger?.LogWarning($"Device registration failed - no tokens returned");

                    TaskDialog.Show(
                        "Device Registration",
                        "Device registration failed.\n\n" +
                        "Please check:\n" +
                        "- License key is valid\n" +
                        "- API server is running\n" +
                        "- Network connectivity");

                    return Result.Failed;
                }
            }
            catch (AggregateException ex)
            {
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                message = $"Registration error: {innerMessage}";

                TaskDialog.Show(
                    "Device Registration",
                    $"Failed to register device:\n\n{innerMessage}\n\n" +
                    "Please check API server connectivity.");

                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = $"Error during device registration: {ex.Message}";
                TaskDialog.Show("Device Registration", $"Error: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
