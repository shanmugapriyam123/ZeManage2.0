using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeManage.Agent.Core.Interop;

internal static class Win32Window
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, char[] lParam);

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

            // Legacy approach: older Chrome/Edge versions expose Chrome_OmniboxView HWND
            var url = TryGetUrlFromChildHwnd(hwnd);
            if (url != null) return url;

            // Modern Edge/Chrome renders address bar on GPU — use UIAutomation accessibility tree
            return TryGetUrlViaUiAutomation(hwnd);
        }
        catch { return null; }
    }

    private static string? TryGetUrlFromChildHwnd(IntPtr hwnd)
    {
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

    private static string? TryGetUrlViaUiAutomation(IntPtr hwnd)
    {
        try
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            var window = automation.FromHandle(hwnd);

            var edits = window.FindAllDescendants(
                cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));

            foreach (var edit in edits)
            {
                try
                {
                    var pattern = edit.Patterns.Value.PatternOrDefault;
                    if (pattern == null) continue;
                    var val = pattern.Value.Value;
                    if (!string.IsNullOrEmpty(val) &&
                        (val.StartsWith("http://",   StringComparison.OrdinalIgnoreCase) ||
                         val.StartsWith("https://",  StringComparison.OrdinalIgnoreCase) ||
                         val.StartsWith("edge://",   StringComparison.OrdinalIgnoreCase) ||
                         val.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                         val.StartsWith("file:///",  StringComparison.OrdinalIgnoreCase)))
                        return val;
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }
}
