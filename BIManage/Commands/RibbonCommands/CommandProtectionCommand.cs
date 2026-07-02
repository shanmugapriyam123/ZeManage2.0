using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Commands.Bindings;
using BIManageRevit.BIManage.Views.Protection;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandProtectionCommand : IExternalCommand
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

                CommandProtectionRepository? commandProtectionRepository = null;
                CacheRepository? cacheRepository = null;
                CommandProtectionBinding? commandProtectionBinding = null;
                CommandProtectionSyncService? syncService = null;
                ISignalREventBus? eventBus = null;
                RevitApiService? revitApiService = null;

                if (services != null)
                {
                    logger = services.GetService<ILogger>();
                    commandProtectionBinding = services.GetService<CommandProtectionBinding>();
                    revitApiService = services.GetService<RevitApiService>();

                    // Get database path
                    var logDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var databasePath = Path.Combine(logDirectory, "bimanage.db");

                    // Use registered singletons
                    var integrityService = services.GetService<global::BIManage.Data.SQLite.DatabaseIntegrityService>();
                    commandProtectionRepository = services.GetService<CommandProtectionRepository>()
                        ?? new CommandProtectionRepository(databasePath, logger, integrityService);
                    cacheRepository = services.GetService<CacheRepository>() ?? new CacheRepository(databasePath, logger);
                    syncService = services.GetService<CommandProtectionSyncService>();
                    eventBus = services.GetService<ISignalREventBus>();

                    logger?.LogInfo("CommandProtectionCommand: Opening dialog with repositories and sync service");
                }

                // Use sign-in displayName for ModifiedBy/CreatedBy, fallback to Revit username
                var userService = services?.GetService<IUserService>();
                var revitUsername = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.UserName ?? commandData.Application.Application.Username
                    : commandData.Application.Application.Username;
                var profileId = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.ProfileId : null;
                var isCompanyAdmin = userService?.IsCompanyAdmin ?? false;

                // Get current model GUID for model-specific API fetch
                string? currentModelGuid = null;
                try
                {
                    var doc = commandData.Application.ActiveUIDocument?.Document;
                    if (doc != null)
                        currentModelGuid = global::BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, logger);
                }
                catch { /* Use null — will fall back to fetch all */ }

                // Create dialog with all dependencies
                var dialog = new CommandProtectionDialog(
                    revitUsername: revitUsername,
                    repository: commandProtectionRepository,
                    projectId: null,
                    logger: logger,
                    commandProtectionBinding: commandProtectionBinding,
                    cacheRepository: cacheRepository,
                    syncService: syncService,
                    isCompanyAdmin: isCompanyAdmin,
                    profileId: profileId,
                    eventBus: eventBus,
                    modelGuid: currentModelGuid,
                    revitApiService: revitApiService);

                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                // Refresh in-memory enforcement cache so changes take effect immediately
                commandProtectionBinding?.LoadAndRegisterCommands();
                logger?.LogInfo("Command Protection dialog closed — enforcement cache refreshed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"CommandProtectionCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
