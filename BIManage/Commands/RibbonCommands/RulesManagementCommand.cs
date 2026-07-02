using System;
using System.Configuration;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Core.Rules;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views.Rules;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Ribbon command to open Rules Management dialog
    /// Allows viewing, creating, editing, and deleting rules from the database
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class RulesManagementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                // Get application services
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Rules Management", "Unable to access application services.");
                    return Result.Failed;
                }

                var logger = services.GetService<ILogger>();
                var ruleService = services.GetService<IRuleService>();
                var ruleRepository = services.GetService<RuleRepository>();
                var rulesSyncService = services.GetService<RulesSyncService>();
                var eventBus = services.GetService<ISignalREventBus>();

                if (ruleService == null || ruleRepository == null)
                {
                    TaskDialog.Show("Rules Management",
                        "Rule service not available.\nPlease ensure the application is properly initialized.");
                    return Result.Failed;
                }

                logger?.LogInfo($"Opening Rules Management dialog... (RulesSyncService: {(rulesSyncService != null ? "available" : "not available")})");

                // Use sign-in displayName for ModifiedBy/CreatedBy, fallback to Revit username
                var userService = services.GetService<IUserService>();
                var revitUsername = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.UserName ?? commandData.Application?.Application?.Username ?? Environment.UserName
                    : commandData.Application?.Application?.Username ?? Environment.UserName;
                var profileId = userService?.IsAuthenticated == true
                    ? userService.CurrentUser?.ProfileId : null;

                // Get API base URL from config (same method as RevitBootstrapper)
                var apiBaseUrl = ReadDllAppSetting("ApiBaseUrl");
                logger?.LogInfo($"Rules Management: API URL = {apiBaseUrl ?? "(not configured)"}");

                // Get database path for cache repository
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "Logs");
                var databasePath = Path.Combine(logDirectory, "bimanage.db");
                logger?.LogInfo($"Rules Management: Database path = {databasePath}");

                // Get current model GUID for auto-detection of project_id/model_guid
                string currentModelGuid = null;
                var doc = commandData.Application?.ActiveUIDocument?.Document;
                if (doc != null)
                {
                    currentModelGuid = ModelGuidHelper.GetModelGuid(doc, logger);
                    logger?.LogInfo($"Rules Management: Current model GUID = {currentModelGuid ?? "(none)"}");
                }

                // Check user role for scope visibility
                var isCompanyAdmin = userService?.IsCompanyAdmin ?? false;
                var isProjectAdmin = userService?.IsProjectAdmin ?? false;

                // Create and show the Rules Management dialog
                var dialog = new RulesManagementDialog(ruleService, ruleRepository, logger, revitUsername, apiBaseUrl, databasePath, rulesSyncService, currentModelGuid, isCompanyAdmin, profileId, eventBus, isProjectAdmin);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                logger?.LogInfo("Rules Management dialog closed");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error opening Rules Management: {ex.Message}";
                return Result.Failed;
            }
        }

        /// <summary>
        /// Reads an app setting from the DLL's config file (not Revit.exe's config).
        /// </summary>
        private static string ReadDllAppSetting(string key)
        {
            try
            {
                var assemblyLocation = typeof(RulesManagementCommand).Assembly.Location;
                var config = ConfigurationManager.OpenExeConfiguration(assemblyLocation);
                return config.AppSettings.Settings[key]?.Value;
            }
            catch
            {
                return null;
            }
        }
    }
}
