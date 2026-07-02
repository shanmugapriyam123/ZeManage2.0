using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.BIManage.Views.Sessions;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CrashRegisterCommand : IExternalCommand
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
                    TaskDialog.Show("Crash Register", "Unable to access application services.");
                    return Result.Failed;
                }

                var sessionRepository = services.GetService<SessionRepository>();
                var logger = services.GetService<ILogger>();

                if (sessionRepository == null)
                {
                    TaskDialog.Show("Crash Register", "Session services not available.");
                    return Result.Failed;
                }

                logger?.LogInfo("Opening Crash Register dialog...");

                var dialog = new CrashRegisterDialog(sessionRepository, logger);
                dialog.Show();

                logger?.LogInfo("Crash Register dialog opened");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error opening crash register: {ex.Message}";
                return Result.Failed;
            }
        }
    }
}
