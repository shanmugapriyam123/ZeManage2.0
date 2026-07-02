using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Features;
using BIManage.Core.Protection;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Views.Protection;
using System;

namespace BIManage.Commands.RibbonCommands
{
    /// <summary>
    /// Command to configure the protection override password
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ConfigurePasswordCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (global::BIManage.Common.Helpers.CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (global::BIManageRevit.Commands.RibbonCommands.LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                // Get services from DI container via reflection
                var app = BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Error", "Service registry not available.");
                    return Result.Failed;
                }

                // Get services from DI container
                var featureToggleService = services.GetService<IFeatureToggleService>();

                // Check if developer tools are enabled
                if (featureToggleService != null && !featureToggleService.IsFeatureEnabled("DeveloperTools"))
                {
                    TaskDialog.Show("Access Denied",
                        "This feature requires developer tools to be enabled.");
                    return Result.Cancelled;
                }

                // Get PasswordManager from DI
                var passwordManager = services.GetService<PasswordManager>();
                if (passwordManager == null)
                {
                    TaskDialog.Show("Error", 
                        "Password manager is not available. Please contact your administrator.");
                    return Result.Failed;
                }

                // Show password configuration dialog
                var dialog = new PasswordConfigDialog(passwordManager);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                var result = dialog.ShowDialog();

                if (result == true && dialog.Success)
                {
                    TaskDialog.Show("Success", 
                        "Override password has been configured successfully.");
                    return Result.Succeeded;
                }

                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = $"Error configuring password: {ex.Message}";
                TaskDialog.Show("Error", message);
                return Result.Failed;
            }
        }
    }
}
