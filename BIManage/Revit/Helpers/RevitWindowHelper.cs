using System;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Helpers
{
    /// <summary>
    /// Single source of truth for the WPF dialog Owner HWND in BIManage.
    ///
    /// Why this exists — root cause we ship-fixed in plan
    /// synthetic-foraging-badger.md (BIManageRevit_20260522_1034.log / _1228.log):
    ///
    /// <para>
    /// <c>Process.GetCurrentProcess().MainWindowHandle</c> is computed via Win32
    /// <c>EnumWindows</c> on every access and returns whatever top-level visible
    /// window is "first" at that moment. While Revit rebuilds a contextual ribbon
    /// tab (e.g. after a multi-select), it creates and destroys ribbon-panel
    /// HwndSources rapidly. For a brief window, EnumWindows returns one of those
    /// transient panel HWNDs. Our BeforeExecuted handler captures it, calls
    /// <c>ShowDialog()</c> ~50ms later, and WPF's <c>CreateWindowEx</c> then fails
    /// with <c>ERROR_INVALID_WINDOW_HANDLE</c> (1400) because the captured HWND
    /// has already been destroyed. The dialog throws and the protected command
    /// proceeded without authorization.
    /// </para>
    ///
    /// <para>
    /// <c>UIApplication.MainWindowHandle</c> is the canonical Revit MDI parent
    /// HWND. It is stable for the entire Revit process lifetime, does not depend
    /// on EnumWindows window ordering, and is exposed by the Revit API on every
    /// version we target (Revit 2019+). We capture it once at OnApplicationInitialized
    /// and never re-read it.
    /// </para>
    ///
    /// Always call <see cref="SetOwner"/> instead of constructing a
    /// <c>WindowInteropHelper</c> with a raw HWND. Never use
    /// <c>Process.GetCurrentProcess().MainWindowHandle</c> as a WPF Owner.
    /// </summary>
    public static class RevitWindowHelper
    {
        private static IntPtr _cachedMainWindow = IntPtr.Zero;
        private static ILogger? _logger;

        /// <summary>
        /// Called exactly once from <c>Application.OnApplicationInitialized</c> after
        /// <c>UIApplication</c> is created. The cached HWND is never refreshed —
        /// Revit's main window HWND does not change across the process lifetime.
        /// </summary>
        public static void Initialize(UIApplication uiApp, ILogger? logger)
        {
            _logger = logger;
            try
            {
                _cachedMainWindow = uiApp?.MainWindowHandle ?? IntPtr.Zero;
                if (_cachedMainWindow == IntPtr.Zero)
                    _logger?.LogWarning("[RevitWindowHelper] UIApplication.MainWindowHandle returned IntPtr.Zero at init — dialogs will open without an owner until restart.");
                else
                    _logger?.LogDebug($"[RevitWindowHelper] Cached Revit main HWND: 0x{_cachedMainWindow.ToInt64():X}");
            }
            catch (Exception ex)
            {
                _cachedMainWindow = IntPtr.Zero;
                _logger?.LogWarning($"[RevitWindowHelper] Initialize failed: {ex.Message} — dialogs will open without an owner.");
            }
        }

        /// <summary>
        /// Returns the cached canonical Revit main HWND, or <see cref="IntPtr.Zero"/>
        /// if <see cref="Initialize"/> has not yet been called or returned a zero handle.
        /// Callers MUST treat zero as "skip Owner assignment".
        /// </summary>
        public static IntPtr GetOwnerHwnd() => _cachedMainWindow;

        /// <summary>
        /// Assigns the cached Revit main HWND as the WPF dialog's Owner. No-op if no
        /// valid HWND is cached — WPF treats no Owner as parentless, which is safe.
        /// Assigning <see cref="IntPtr.Zero"/> via <c>WindowInteropHelper.Owner</c>
        /// followed by <c>ShowDialog()</c> is what threw
        /// <c>Win32Exception: Invalid window handle</c> in the original repro.
        /// </summary>
        public static void SetOwner(System.Windows.Window? dialog)
        {
            if (dialog == null) return;
            var hwnd = _cachedMainWindow;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = hwnd };
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[RevitWindowHelper] Owner assignment failed: {ex.Message} — continuing without an owner.");
            }
        }
    }
}
