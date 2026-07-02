using System.Windows;

namespace BIManage.Addons.Helpers
{
    /// <summary>
    /// Helper for WPF window state management.
    /// PreventMinimize is kept as no-op for backward compatibility (minimize is now allowed).
    /// </summary>
    public static class WindowRestoreHelper
    {
        public static void PreventMinimize(Window window)
        {
            // No-op: minimize is now allowed for all windows
        }
    }
}
