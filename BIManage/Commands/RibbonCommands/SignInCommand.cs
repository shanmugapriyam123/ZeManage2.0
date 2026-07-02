using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Applications;
using BIManage.Revit.Context;
using BIManageRevit.BIManage.Views.Auth;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SignInCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            ILogger? logger = null;
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Sign In", "Unable to access application services.");
                    return Result.Failed;
                }

                var authApi = services.GetService<AuthApiService>();
                var tokenManager = services.GetService<AuthTokenManager>();
                var userService = services.GetService<IUserService>();
                var secureStorage = services.GetService<SecureTokenStorage>();
                var sessionSyncService = services.GetService<SessionSyncService>();
                var revitContext = services.GetService<IRevitContext>();
                logger = services.GetService<ILogger>();

                if (authApi == null || tokenManager == null)
                {
                    TaskDialog.Show("Sign In",
                        "Authentication services not available.\n\n" +
                        "Please check that ApiBaseUrl is configured in App.config.");
                    return Result.Failed;
                }

                var sessionId = revitContext?.SessionId.ToString();
                logger?.LogInfo("Opening Sign In dialog");

                var dialog = new SignInDialog(authApi, tokenManager, logger, userService, secureStorage, sessionSyncService, sessionId);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                // Refresh ribbon button visibility directly after dialog closes.
                // We are on the Revit API thread (IExternalCommand), so call UpdateAllButtonVisibility() directly
                // because ExternalEvents raised during the modal dialog may be lost.
                try
                {
                    var appInst = BIManage.Revit.Applications.Application.Instance;
                    var visManager = appInst?.GetType().GetField("_ribbonVisibilityManager",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.GetValue(appInst) as RibbonVisibilityManager;
                    visManager?.UpdateAllButtonVisibility();
                }
                catch (Exception visEx)
                {
                    logger?.LogWarning($"Failed to refresh ribbon visibility: {visEx.Message}");
                }

                if (dialog.Success)
                {
                    var role = userService?.CurrentRole.ToString() ?? "Unknown";
                    logger?.LogInfo($"User signed in successfully via Sign In dialog (Role: {role})");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"SignInCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
