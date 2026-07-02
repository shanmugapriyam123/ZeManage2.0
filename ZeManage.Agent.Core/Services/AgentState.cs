using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Services;

public sealed class AgentState
{
    public AttendanceSession? CurrentAttendance { get; set; }
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
