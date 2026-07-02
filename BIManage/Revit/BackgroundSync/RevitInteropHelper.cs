using System;
using System.Runtime.InteropServices;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// Win32 interop utilities for non-interference background operations.
    /// Provides system-level idle detection, silent Revit wake (WM_NULL),
    /// foreground window detection, and status bar text updates.
    /// </summary>
    internal static class RevitInteropHelper
    {
        // ── Win32 Structures ─────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        // ── P/Invoke Declarations ────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        // ── Constants ────────────────────────────────────────────────────────────

        private const uint WM_NULL = 0x0000;
        private const uint WM_SETTEXT = 0x000C;
        private const string StatusBarClass = "msctls_statusbar32";

        // ── Idle Detection ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the duration since the last mouse/keyboard input at the system level.
        /// Uses Win32 GetLastInputInfo — works regardless of which application is in focus.
        /// </summary>
        public static TimeSpan GetTimeSinceLastInput()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };

            if (!GetLastInputInfo(ref info))
                return TimeSpan.Zero;

            var elapsedMs = (long)GetTickCount() - (long)info.dwTime;
            if (elapsedMs < 0)
                elapsedMs = 0; // Handle tick count wraparound (~49.7 days)

            return TimeSpan.FromMilliseconds(elapsedMs);
        }

        // ── WM_NULL Silent Poke ──────────────────────────────────────────────────

        /// <summary>
        /// Sends WM_NULL to Revit's main window to wake up the message pump.
        /// This triggers Idling/ExternalEvent processing without stealing focus
        /// or bringing Revit to the foreground — critical for background sync.
        /// </summary>
        public static void PokeRevit(IntPtr mainWindowHandle)
        {
            if (mainWindowHandle == IntPtr.Zero)
                return;

            PostMessage(mainWindowHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        // ── Foreground Window ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if Revit is the currently active foreground window.
        /// When false, the user is in another application — a safe time for background sync.
        /// </summary>
        public static bool IsRevitForeground(IntPtr revitMainWindowHandle)
        {
            if (revitMainWindowHandle == IntPtr.Zero)
                return false;

            return GetForegroundWindow() == revitMainWindowHandle;
        }

        // ── Status Bar ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the text of Revit's status bar (bottom-left) to provide passive feedback
        /// during background operations. Non-modal — never interrupts the user.
        /// </summary>
        public static void SetStatusText(IntPtr mainWindowHandle, string text)
        {
            if (mainWindowHandle == IntPtr.Zero || text == null)
                return;

            var statusBar = FindStatusBar(mainWindowHandle);
            if (statusBar == IntPtr.Zero)
                return;

            SendMessage(statusBar, WM_SETTEXT, IntPtr.Zero, text);
        }

        /// <summary>
        /// Clears the status bar text by setting it to empty string.
        /// Call after background operations complete.
        /// </summary>
        public static void ClearStatusText(IntPtr mainWindowHandle)
        {
            SetStatusText(mainWindowHandle, string.Empty);
        }

        private static IntPtr FindStatusBar(IntPtr mainWindowHandle)
        {
            // Revit's status bar is a msctls_statusbar32 child of the main window
            return FindWindowEx(mainWindowHandle, IntPtr.Zero, StatusBarClass, null);
        }
    }
}
