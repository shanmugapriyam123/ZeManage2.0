using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeManage.AgentService.Interop;

/// <summary>Foreground-window helpers. Browser URL capture is intentionally limited to the legacy
/// Chrome_OmniboxView child-window text (works on many Chrome/Edge builds) — the reference Agent's
/// UI-Automation fallback (FlaUI) was dropped here to keep this service's dependency footprint
/// smaller; a browser record with no URL is simply skipped by the sync layer, not an error.</summary>
internal static class Win32Window
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, char[] lParam);

    private const uint WM_GETTEXT = 0x000D;

    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out int pid);
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    public static (string? processName, string? windowTitle) GetForegroundBrowserInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (null, null);

            GetWindowThreadProcessId(hwnd, out int pid);
            string processName;
            try
            {
                using var proc = Process.GetProcessById(pid);
                processName = proc.ProcessName;
            }
            catch { return (null, null); }

            if (!processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) &&
                !processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                return (null, null);

            var chars = new char[1024];
            var len = GetWindowText(hwnd, chars, chars.Length);
            if (len <= 0) return (null, null);

            return (processName, new string(chars, 0, len));
        }
        catch { return (null, null); }
    }

    public static string? TryGetBrowserUrl()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            string? url = null;
            EnumChildWindows(hwnd, (child, _) =>
            {
                var cls = new char[256];
                var clsLen = GetClassName(child, cls, cls.Length);
                if (clsLen <= 0) return true;
                var className = new string(cls, 0, clsLen);
                if (className == "Chrome_OmniboxView" || className == "Chrome_AutocompleteEditView")
                {
                    var buf = new char[2048];
                    var n = (int)SendMessage(child, WM_GETTEXT, (IntPtr)buf.Length, buf);
                    if (n <= 0) n = GetWindowText(child, buf, buf.Length);
                    if (n > 0) url = new string(buf, 0, n);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch { return null; }
    }
}
