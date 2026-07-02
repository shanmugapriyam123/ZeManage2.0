using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManageRevit.BIManage.Views.Sessions;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SessionInformationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Session Information", "Unable to access application services.");
                    return Result.Failed;
                }

                var sessionRepository = services.GetService<SessionRepository>();
                var revitContext = services.GetService<IRevitContext>();
                var registeredModels = services.GetService<RegisteredModelsRepository>();
                var modelSyncService = services.GetService<ModelSyncService>();
                var logger = services.GetService<ILogger>();

                if (sessionRepository == null || revitContext == null)
                {
                    TaskDialog.Show("Session Information", "Session services not available.");
                    return Result.Failed;
                }

                // Get current session ID
                var sessionId = revitContext.SessionId.ToString();

                logger?.LogInfo("Opening Session Information dialog...");

                // Get UIApplication for live Revit data (journal filename, open documents)
                var uiApp = commandData.Application;

                // Create and show the Session Information dialog
                var dialog = new SessionInformationDialog(sessionRepository, logger, sessionId, uiApp, registeredModels, modelSyncService);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                logger?.LogInfo("Session Information dialog closed");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error retrieving session information: {ex.Message}";
                return Result.Failed;
            }
        }
    }
}
