namespace ZeManage.Agent.Core.Models;

public sealed class NetworkSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CapturedAt { get; set; }
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public double LatencyMs { get; set; }
    public double PacketLossPercent { get; set; }
    public bool VpnConnected { get; set; }
    public string? ActiveAdapter { get; set; }
    public string? ConnectionType { get; set; }
    public int HealthScore { get; set; }
    public bool Synced { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
