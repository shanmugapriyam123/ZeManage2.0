using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Services;

public sealed class AgentState
{
    public HardwareSnapshot? LatestHardware { get; set; }
    public NetworkSnapshot? LatestNetwork { get; set; }

    // Browser tracking
    public string? CurrentBrowserTitle { get; set; }
    public string? CurrentBrowserName  { get; set; }
    public DateTime? BrowserActivityAt { get; set; }

    // V2: SignalR hub
    public bool IsHubConnected        { get; set; }
    public string HubConnectionState  { get; set; } = "Disconnected";

    // V2: Server commands
    public bool ForceLogoutRequested  { get; set; }
    public bool ShutdownRequested     { get; set; }
    // Set by AgentHubConnection's "CaptureScreenshotNow" handler, consumed by ScreenshotMonitor's
    // WatchOnDemandCaptureAsync loop — routed through AgentState rather than injecting
    // ScreenshotMonitor directly into AgentHubConnection, since ScreenshotMonitor already depends
    // on AgentHubConnection (for its own post-upload ReportScreenshot notify), and the reverse
    // dependency would be circular.
    public bool CaptureScreenshotNowRequested { get; set; }

    // V2: Screenshots
    public DateTime? LatestScreenshotAt { get; set; }
    public int ScreenshotCount          { get; set; }

    // Admin-controlled employee active/inactive kill-switch. Default true (fail-open at process
    // start) — LocalStore's cached last-known state is loaded into this before any monitor's
    // ExecuteAsync runs (see AgentBootstrap), so a device that's actually been deactivated stays
    // deactivated across a restart even before its first successful poll (fail-closed once a
    // state has actually been confirmed at least once). Set from TokenProvider's
    // register-or-validate-device/refresh/register responses (HTTP polling fallback) and from
    // AgentHubConnection's EmployeeActiveStatusChanged handler (SignalR fast path). Every
    // monitor's tick loop checks this before doing any capture work.
    public bool IsCaptureEnabled { get; set; } = true;

    // Server-configured idle threshold (from /api/v1/agentdb/company-settings)
    public bool IsIdleTrackingEnabled { get; set; } = true;
    public int IdleThresholdMinutes { get; set; } = 5;

    // Server-configured screenshot schedule (from /api/v1/agentdb/company-settings)
    public bool IsScreenshotEnabled { get; set; }
    public int ScreenshotIntervalMinutes { get; set; } = 10;
    public TimeSpan ScreenshotStartTime { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan ScreenshotEndTime { get; set; } = TimeSpan.FromHours(20);
    // Whether ScreenshotStartTime/EndTime above should actually be enforced. Was previously
    // fetched from the server but never read client-side, so turning "Active time window" off in
    // the web portal had no effect — the agent kept restricting capture to the last-saved hours.
    // Defaults true to preserve that same always-restricted behavior for any device that hasn't
    // synced this field yet (matches the unconditional enforcement this replaces).
    public bool IsActiveWindowEnabled { get; set; } = true;
    public bool ScreenshotMonday { get; set; } = true;
    public bool ScreenshotTuesday { get; set; } = true;
    public bool ScreenshotWednesday { get; set; } = true;
    public bool ScreenshotThursday { get; set; } = true;
    public bool ScreenshotFriday { get; set; } = true;
    public bool ScreenshotSaturday { get; set; }
    public bool ScreenshotSunday { get; set; }

    // Server-configured Break Time Setup (from the SAME /api/v1/agentdb/company-settings idle
    // payload as IdleThresholdMinutes above — breakStartTime/breakEndTime/breakSeconds/
    // flexibleBreak, see SyncService.TryFetchCompanySettingsAsync). Only the Fixed-window shape
    // (flexibleBreak=false, both times set) can be evaluated purely from time-of-day; Flexible
    // allowance is a total daily budget with no fixed clock window, so it's deliberately not
    // treated as "on break right now" here — that classification would need actual usage
    // tracking, out of scope for this (Idle-time-inside-a-fixed-window → "Break") feature.
    public TimeSpan? BreakStartTime { get; set; }
    public TimeSpan? BreakEndTime { get; set; }
    public bool IsFlexibleBreak { get; set; }
    // Whether Break Time Setup is toggled on at all for this group. Defaults true so a device
    // that hasn't synced this field yet keeps the pre-existing always-on behavior; once synced,
    // an admin turning the feature off must stop the fixed-window classification below even
    // though BreakStartTime/EndTime themselves are still populated from the last fetch.
    public bool IsBreakTimeEnabled { get; set; } = true;

    // India Standard Time has no DST, so a fixed UTC+5:30 offset is all that's needed — and using
    // a fixed offset instead of the agent machine's own OS timezone setting is the point: the
    // server stores BreakStartTime/BreakEndTime as genuine UTC instants derived from the admin's
    // company-local (IST) wall clock (see GroupWorktimeSetup/LocalDateRangeHelper on the backend,
    // and the web portal's own toLocalHourMin doing the same fixed +5.5h shift for the matching
    // WorkWindowStartTime/EndTime fields). Previously this compared `now.ToLocalTime()` against a
    // BreakStartTime/EndTime that SyncService also derived via `.ToLocalTime()` — self-consistent
    // only on a machine whose OS timezone happens to be IST; any agent host set to a different
    // zone (UTC VMs/kiosks, a restored image, a re-imaged machine) silently shifted the break
    // window off real IST wall-clock time, misclassifying genuine Idle time as "Break" (or vice
    // versa) by the difference between that machine's zone and IST.
    private static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    public static TimeSpan ToIstTimeOfDay(DateTime utcInstant) =>
        (DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc) + IstOffset).TimeOfDay;

    public bool IsInActiveBreakWindow(DateTime utcNow)
    {
        if (!IsBreakTimeEnabled || IsFlexibleBreak || BreakStartTime is not { } start || BreakEndTime is not { } end)
            return false;

        var tod = ToIstTimeOfDay(utcNow);
        return start <= end
            ? (tod >= start && tod < end)
            : (tod >= start || tod < end); // window crosses midnight
    }

    // Sync
    public DateTime? LastSyncAt   { get; set; }
    public string LastSyncStatus  { get; set; } = "Never";
    public int UnsyncedCount      { get; set; }
    public List<string> RecentEvents { get; } = new();

    // UI-triggered immediate sync (user clicks "Not Running" banner)
    public SemaphoreSlim SyncTrigger { get; } = new SemaphoreSlim(0, 1);
    public void RequestImmediateSync() { if (SyncTrigger.CurrentCount == 0) SyncTrigger.Release(); }

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();

    public void PushEvent(string msg)
    {
        lock (RecentEvents)
        {
            RecentEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
            if (RecentEvents.Count > 50) RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }
        NotifyChanged();
    }
}
