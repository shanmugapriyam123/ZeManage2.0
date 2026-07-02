using System.Runtime.InteropServices;

namespace ZeManage.Agent.Core.Interop;

internal static partial class Win32Idle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan GetIdleDuration()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        var idleMs = (long)(Environment.TickCount64 - lii.dwTime);
        if (idleMs < 0) idleMs = 0;
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
