using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to open the Dashboard website in the default browser.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DashboardCommand : IExternalCommand
    {
        private static readonly string DashboardUrl =
            AppConfigReader.Read("DashboardUrl") ?? "https://zemanage.com/dashboard";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DashboardUrl,
                    UseShellExecute = true
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Dashboard",
                    $"Unable to open the dashboard.\n\n" +
                    $"Please navigate to: {DashboardUrl}\n\n" +
                    $"Error: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
