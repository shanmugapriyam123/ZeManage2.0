using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
using BIManageRevit.BIManage.Views.Protection;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class EventRestrictionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            ILogger? logger = null;
            try
            {
                // Get services from Application instance using reflection (same pattern as other commands)
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                EventProtectionRepository? eventProtectionRepository = null;
                EventProtectionSyncService? eventSync = null;
                ISignalREventBus? eventBus = null;

                if (services != null)
                {
                    logger = services.GetService<ILogger>();

                    // Get database path
                    var logDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var databasePath = Path.Combine(logDirectory, "bimanage.db");

                    // Create event protection repository
                    var integrityService = services.GetService<global::BIManage.Data.SQLite.DatabaseIntegrityService>();
                    eventProtectionRepository = new EventProtectionRepository(databasePath, logger, integrityService);

                    eventSync = services.GetService<EventProtectionSyncService>();
                    eventBus = services.GetService<ISignalREventBus>();

                    logger?.LogInfo("EventRestrictionCommand: Opening dialog with repository");
                }

                // Use sign-in displayName for ModifiedBy/CreatedBy, fallback to Revit username
                var userService = services?.GetService<IUserService>();
                var revitUsername = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.UserName ?? commandData.Application.Application.Username
                    : commandData.Application.Application.Username;
                var profileId = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.ProfileId : null;
                var isCompanyAdmin = userService?.IsCompanyAdmin ?? false;
                var isProjectAdmin = userService?.IsProjectAdmin ?? false;

                // Get current model GUID for API fetch
                var doc = commandData.Application?.ActiveUIDocument?.Document;
                var currentModelGuid = doc != null ? ModelGuidHelper.GetModelGuid(doc, logger) : null;

                // Project GUID is resolved asynchronously inside the dialog's OnLoaded
                // handler so dialog open is not delayed by an HTTP roundtrip on the UI thread.

                // Create dialog with dependencies
                var dialog = new EventProtectionDialog(
                    revitUsername: revitUsername,
                    repository: eventProtectionRepository,
                    projectId: null,
                    logger: logger,
                    isCompanyAdmin: isCompanyAdmin,
                    profileId: profileId,
                    syncService: eventSync,
                    modelGuid: currentModelGuid,
                    eventBus: eventBus,
                    currentProjectIdGuid: null,
                    isProjectAdmin: isProjectAdmin);

                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                // Refresh in-memory enforcement cache so changes take effect immediately
                var eventProtectionService = services?.GetService<IEventProtectionService>();
                eventProtectionService?.LoadSettingsFromDatabase(projectId: null, modelGuid: currentModelGuid);

                // Refresh the CAD Explode ribbon swap visibility — its install state is gated on
                // Ze_CADExplodeProtection.Enabled, which the user may have just toggled.
                try { global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.RefreshCADExplodeSwapState(); }
                catch (Exception swapEx) { logger?.LogWarning($"Explode swap refresh after dialog: {swapEx.Message}"); }

                logger?.LogInfo("Event Restriction dialog closed — enforcement cache refreshed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"EventRestrictionCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
