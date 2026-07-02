using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SupportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (global::BIManage.Common.Helpers.CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            try
            {
                var uiApp = commandData.Application;
                var app = uiApp.Application;
                var doc = uiApp.ActiveUIDocument?.Document;

                // Gather Revit context info
                var revitVersion = app.VersionNumber;
                var revitBuild = app.VersionBuild;
                var revitUsername = app.Username;

                // Session ID from plugin context
                string? sessionId = null;
                try
                {
                    var services = BIManage.Revit.Applications.Application.Instance?.Services;
                    var context = services?.GetService<global::BIManage.Revit.Context.IRevitContext>();
                    sessionId = context?.SessionId.ToString();
                }
                catch { }

                // Model info
                var modelName = doc?.Title;
                var modelPath = doc?.PathName;

                // Check if user is admin
                bool isAdmin = false;
                global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient? httpClient = null;
                global::BIManage.Infrastructure.Logging.ILogger? logger = null;
                try
                {
                    var services = BIManage.Revit.Applications.Application.Instance?.Services;
                    var userService = services?.GetService<global::BIManage.Core.Identity.IUserService>();
                    if (userService != null)
                    {
                        var role = userService.CurrentRole;
                        isAdmin = role == global::BIManage.Core.Identity.UserRole.CompanyAdministrator
                               || role == global::BIManage.Core.Identity.UserRole.ProjectAdministrator;
                    }
                    httpClient = services?.GetService<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient>();
                    logger = services?.GetService<global::BIManage.Infrastructure.Logging.ILogger>();
                }
                catch { }

                var dialog = new BIManage.Views.Support.SupportDialog(
                    revitVersion: revitVersion,
                    revitBuild: revitBuild,
                    revitUsername: revitUsername,
                    sessionId: sessionId,
                    modelName: modelName,
                    modelPath: modelPath,
                    isAdmin: isAdmin,
                    httpClient: httpClient,
                    logger: logger);

                new System.Windows.Interop.WindowInteropHelper(dialog)
                {
                    Owner = commandData.Application.MainWindowHandle
                };
                dialog.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                try
                {
                    var lg = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.Services
                        ?.GetService<global::BIManage.Infrastructure.Logging.ILogger>();
                    lg?.LogError($"SupportCommand failed: {ex.Message}", ex);
                }
                catch { }
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
