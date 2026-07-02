using System.Configuration;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Reads AppSettings from the DLL's own config file (BIManageRevit.dll.config).
    /// ConfigurationManager.AppSettings reads from the host app (Revit.exe) config,
    /// so we must explicitly open the DLL's config file via its assembly location.
    /// </summary>
    public static class AppConfigReader
    {
        public static string? Read(string key)
        {
            try
            {
                var assemblyLocation = typeof(AppConfigReader).Assembly.Location;
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
