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

    // V2: Screenshots
    public DateTime? LatestScreenshotAt { get; set; }
    public int ScreenshotCount          { get; set; }

    // Server-configured idle threshold (from /api/v1/agentdb/company-settings)
    public bool IsIdleTrackingEnabled { get; set; } = true;
    public int IdleThresholdMinutes { get; set; } = 5;

    // Server-configured screenshot schedule (from /api/v1/agentdb/company-settings)
    public bool IsScreenshotEnabled { get; set; }
    public int ScreenshotIntervalMinutes { get; set; } = 10;
    public TimeSpan ScreenshotStartTime { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan ScreenshotEndTime { get; set; } = TimeSpan.FromHours(20);
    public bool ScreenshotMonday { get; set; } = true;
    public bool ScreenshotTuesday { get; set; } = true;
    public bool ScreenshotWednesday { get; set; } = true;
    public bool ScreenshotThursday { get; set; } = true;
    public bool ScreenshotFriday { get; set; } = true;
    public bool ScreenshotSaturday { get; set; }
    public bool ScreenshotSunday { get; set; }

    // Sync
    public DateTime? LastSyncAt   { get; set; }
    public string LastSyncStatus  { get; set; } = "Never";
    public int UnsyncedCount      { get; set; }
    public List<string> RecentEvents { get; } = new();

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
