using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Features;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.BIManage.Views.SyncSettings;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            ILogger? logger = null;
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                SyncRepository? syncRepository = null;
                IFeatureToggleService? featureToggleService = null;

                if (services != null)
                {
                    logger = services.GetService<ILogger>();
                    featureToggleService = services.GetService<IFeatureToggleService>();

                    var logDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var databasePath = Path.Combine(logDirectory, "bimanage.db");

                    syncRepository = new SyncRepository(databasePath, logger);
                }

                var dialog = new SyncSettingsDialog(
                    syncRepository: syncRepository,
                    logger: logger,
                    featureToggleService: featureToggleService);

                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                logger?.LogInfo("Sync Settings dialog closed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"SyncSettingsCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
