namespace ZeManage.Agent.Core.Models;

public sealed class NetworkSnapshot
{
    public long Id { get; set; }
    public required string MachineName { get; set; }
    public DateTime CapturedAt { get; set; }
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public double LatencyMs { get; set; }
    public double PacketLossPercent { get; set; }
    public bool VpnConnected { get; set; }
    public string? ActiveAdapter { get; set; }
    public int HealthScore { get; set; }
    public bool Synced { get; set; }
}
