using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManageRevit.BIManage.Views.Developer;
using Nice3point.Revit.Toolkit.External;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to view current role based on login.
    /// Role is determined by the API response during authentication.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SetDeveloperRoleCommand : ExternalCommand
    {
        public override void Execute()
        {
            try
            {
                var app = BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Switch Role", "Service registry not available");
                    return;
                }

                var logger = services.GetService<ILogger>();
                var userService = services.GetService<IUserService>();

                if (userService == null)
                {
                    TaskDialog.Show("Switch Role", "UserService not available");
                    return;
                }

                // Require authentication - role comes from login
                if (!userService.IsAuthenticated)
                {
                    TaskDialog.Show("Sign In Required",
                        "Please sign in first to view your role.\n\n" +
                        "Your role is determined by your login credentials.");
                    return;
                }

                var currentRole = userService.CurrentRole;
                var currentUser = userService.CurrentUser;

                logger?.LogInfo($"Opening role dialog. Current role: {currentRole}, User: {currentUser?.Email}");

                // Show the role dialog with login-based role
                var dialog = new RoleSwitchDialog(currentRole, currentUser);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
                var result = dialog.ShowDialog();

                if (result == true && dialog.SelectedRole != currentRole)
                {
                    var selectedRole = dialog.SelectedRole;
                    ApplyRoleChange(userService, selectedRole, logger);

                    logger?.LogInfo($"Role switched: {currentRole} -> {selectedRole}");
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Role Error",
                    $"An error occurred:\n\n{ex.Message}");
            }
        }

        private void ApplyRoleChange(IUserService userService, UserRole newRole, ILogger? logger)
        {
            switch (newRole)
            {
                case UserRole.CompanyAdministrator:
                    userService.UpdateAdminFlags(
                        isCompanyAdmin: true,
                        isProjectAdmin: false);
                    logger?.LogInfo("Role set to Super Admin (CompanyAdministrator)");
                    break;

                case UserRole.ProjectAdministrator:
                    userService.UpdateAdminFlags(
                        isCompanyAdmin: false,
                        isProjectAdmin: true,
                        projectIds: new System.Collections.Generic.List<string> { "*" });
                    logger?.LogInfo("Role set to Admin (ProjectAdministrator)");
                    break;

                case UserRole.NormalUser:
                    userService.UpdateAdminFlags(
                        isCompanyAdmin: false,
                        isProjectAdmin: false);
                    logger?.LogInfo("Role set to User (NormalUser)");
                    break;
            }
        }
    }
}
