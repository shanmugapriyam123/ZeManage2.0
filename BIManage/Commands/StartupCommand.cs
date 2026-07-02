using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;

namespace BIManageRevit.Commands
{
    /// <summary>
    ///     External command entry point
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class StartupCommand : ExternalCommand
    {
        public override void Execute()
        {
            // Capture UIApplication on first command execution
            var app = BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            if (app != null)
            {
                app.SetUIApplication(UiApplication);
            }
        }
    }
}  