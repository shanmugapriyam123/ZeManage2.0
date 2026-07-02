using System.IO;

namespace BIManage.Revit.Applications
{
    /// <summary>
    ///     Pre-flight guards for bootstrap safety.
    /// </summary>
    public static class BootstrapGuards
    {
        public static bool Validate(InitContext context, out string? failureReason)
        {
            failureReason = null;

            if (context == null)
            {
                failureReason = "Bootstrap context was not provided.";
                return false;
            }

            if (context.ControlledApplication == null)
            {
                failureReason = "Revit ControlledApplication is unavailable.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(context.DataDirectory);
            }
            catch (IOException ioEx)
            {
                failureReason = $"Failed to prepare data directory: {ioEx.Message}";
                return false;
            }

            return true;
        }
    }
}
