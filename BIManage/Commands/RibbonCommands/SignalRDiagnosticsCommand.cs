using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.SignalR.Core;
using Nice3point.Revit.Toolkit.External;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Developer command to view SignalR connection diagnostics and test connectivity
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SignalRDiagnosticsCommand : ExternalCommand
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
                    TaskDialog.Show("SignalR Diagnostics", "Service registry not available.");
                    return;
                }

                var signalRService = services.GetService<ISignalRService>();
                var connectionManager = services.GetService<ISignalRConnectionManager>();

                if (signalRService == null)
                {
                    TaskDialog.Show("SignalR Diagnostics",
                        "SignalR is not configured.\n\n" +
                        "Possible reasons:\n" +
                        "- SignalR feature toggle is disabled\n" +
                        "- Hub URL is not set in configuration\n" +
                        "- MachineId is not available");
                    return;
                }

                var dialog = new BIManageRevit.BIManage.Views.SignalR.SignalRDiagnosticsDialog(
                    signalRService, connectionManager);
                new System.Windows.Interop.WindowInteropHelper(dialog)
                {
                    Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
                };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("SignalR Diagnostics Error",
                    $"An error occurred:\n\n{ex.Message}");
            }
        }
    }
}
