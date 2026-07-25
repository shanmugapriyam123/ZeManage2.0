using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeManage.Agent.Core.Interop;

/// <summary>
/// Fires ForegroundChanged instantly via SetWinEventHook(EVENT_SYSTEM_FOREGROUND).
/// Runs its own STA message-pump thread — required for WinEvent delivery.
/// </summary>
internal sealed class Win32ForegroundWatcher : IDisposable
{
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgMin, uint wMsgMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    pt_x;
        public int    pt_y;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_QUIT                 = 0x0012;

    /// <summary>Raised on the watcher's STA thread whenever the foreground process changes.</summary>
    public event Action<string>? ForegroundChanged;

    private Thread?          _thread;
    private uint             _threadId;
    private WinEventDelegate? _delegate; // field reference keeps the delegate alive — do not remove

    public void Start()
    {
        _thread = new Thread(MessagePumpThread)
        {
            IsBackground = true,
            Name         = "ForegroundWatcher"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void MessagePumpThread()
    {
        _threadId = GetCurrentThreadId();

        // Store delegate in field so GC won't collect it while the hook is live
        _delegate = OnWinEvent;

        var hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _delegate,
            0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (hook != IntPtr.Zero)
            UnhookWinEvent(hook);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            if (!string.IsNullOrEmpty(name))
                ForegroundChanged?.Invoke(name);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
