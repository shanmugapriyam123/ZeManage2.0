using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeManage.Agent.Core.Interop;

internal static class Win32Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

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
}
