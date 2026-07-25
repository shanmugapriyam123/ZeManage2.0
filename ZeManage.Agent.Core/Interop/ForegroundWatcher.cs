using System.Runtime.InteropServices;

namespace ZeManage.Agent.Core.Interop;

/// <summary>
/// Fires a callback the INSTANT the foreground window changes, via
/// SetWinEventHook(EVENT_SYSTEM_FOREGROUND) — the same mechanism commercial
/// trackers (Insightful et al.) use for sub-second live status. This removes
/// the up-to-1-second detection latency of the polling scan: the OS notifies
/// us on every focus change, and ProcessMonitor's tick loop is woken
/// immediately instead of waiting out its Task.Delay.
///
/// WINEVENT_OUTOFCONTEXT hooks require the installing thread to pump Windows
/// messages, so the hook lives on its own dedicated background thread running
/// a classic GetMessage loop. Dispose posts WM_QUIT to end the pump and
/// unhook. Rapid-fire focus bursts are naturally coalesced by the consumer
/// (a max-count-1 semaphore) — the tick that wakes reads whatever is
/// CURRENTLY foreground, so the final app of a burst always wins.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private readonly Action _onForegroundChanged;
    private readonly Thread _thread;
    // Kept as a field so the GC can never collect the delegate the native hook
    // still points at (classic SetWinEventHook crash if held only as a local).
    private WinEventDelegate? _callback;
    private IntPtr _hook;
    private volatile uint _threadId;

    public ForegroundWatcher(Action onForegroundChanged)
    {
        _onForegroundChanged = onForegroundChanged;
        _thread = new Thread(Run) { IsBackground = true, Name = "ForegroundWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _callback = (_, _, _, _, _, _, _) =>
        {
            try { _onForegroundChanged(); } catch { /* consumer must never kill the pump */ }
        };
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Message pump — required for WINEVENT_OUTOFCONTEXT delivery. Runs
        // until Dispose posts WM_QUIT (GetMessage then returns 0).
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        var tid = _threadId;
        if (tid != 0)
            PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
