using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace BIManage.Addons.Helpers
{
    public static class RevitWindowOwnership
    {
        public static IntPtr CachedRevitHwnd { get; private set; } = IntPtr.Zero;

        public static void RememberRevitHwnd(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                CachedRevitHwnd = hwnd;
        }

        public static void OwnByRevit(this Window window)
        {
            if (window == null) return;
            var hwnd = CachedRevitHwnd != IntPtr.Zero
                ? CachedRevitHwnd
                : Process.GetCurrentProcess().MainWindowHandle;
            window.OwnByRevit(hwnd);
        }

        public static void OwnByRevit(this Window window, IntPtr revitHwnd)
        {
            if (window == null) return;
            if (revitHwnd == IntPtr.Zero)
            {
                Debug.WriteLine("[BIManage.Addons] OwnByRevit: revitHwnd is zero, skipping.");
                return;
            }

            try
            {
                new WindowInteropHelper(window).Owner = revitHwnd;
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BIManage.Addons] OwnByRevit failed: " + ex);
            }
        }
    }
}
