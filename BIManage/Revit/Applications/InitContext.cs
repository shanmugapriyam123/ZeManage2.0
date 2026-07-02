using System;
using System.IO;
using Autodesk.Revit.ApplicationServices;

namespace BIManage.Revit.Applications
{
    /// <summary>
    ///     Carries contextual information required to bootstrap the add-in.
    /// </summary>
    public class InitContext
    {
        public ControlledApplication ControlledApplication { get; }
        public string DataDirectory { get; }
        public string LogDirectory => Path.Combine(DataDirectory, "Logs");

        public InitContext(ControlledApplication controlledApplication, string dataDirectory)
        {
            ControlledApplication = controlledApplication ?? throw new ArgumentNullException(nameof(controlledApplication));
            DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        }

        public static InitContext CreateDefault(ControlledApplication controlledApplication)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataDirectory = Path.Combine(appData, "BIManageRevit");
            return new InitContext(controlledApplication, dataDirectory);
        }
    }
}
