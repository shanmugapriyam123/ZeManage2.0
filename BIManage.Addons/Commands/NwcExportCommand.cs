using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Addons.Helpers;
using BIManage.Addons.ViewModels.NwcExport;
using BIManage.Addons.Views.Common;
using BIManage.Addons.Views.NwcExport;
using System.Diagnostics;

namespace BIManage.Addons.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class NwcExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet elems)
        {
            RevitWindowOwnership.RememberRevitHwnd(data.Application.MainWindowHandle);

            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            {
                msg = "Navisworks NWC Exporter is not installed or available in this Revit installation.";
                ZeMessageBox.Show("NWC Exporter Not Available",
                    "The Navisworks NWC Exporter is required but not installed.\n\nPlease install the Navisworks NWC Export Utility for your version of Revit.");

                string url = "https://www.autodesk.com/products/navisworks/3d-viewers";
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return Result.Failed;
            }

            var vm = new MainViewModel(data.Application.Application);
            var win = new BulkExportMainWindow { DataContext = vm };
            win.OwnByRevit(data.Application.MainWindowHandle);

            win.ShowDialog();
            return Result.Succeeded;
        }
    }
}
