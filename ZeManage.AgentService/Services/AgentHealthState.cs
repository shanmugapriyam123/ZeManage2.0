namespace ZeManage.AgentService.Services;

/// <summary>In-memory health/status snapshot for this service instance — the "Agent heartbeat/
/// health status" requirement. Updated by SyncService every cycle and logged periodically; also
/// what a future health-check endpoint or `Get-Service`-adjacent diagnostic could read.</summary>
public sealed class AgentHealthState
{
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? LastSyncAtUtc { get; set; }
    public string LastSyncStatus { get; set; } = "Never";
    public int UnsyncedCount { get; set; }
    public DateTime? LastHeartbeatAtUtc { get; set; }
    public bool IsBackendReachable { get; set; }

    public TimeSpan Uptime => DateTime.UtcNow - StartedAtUtc;
}
