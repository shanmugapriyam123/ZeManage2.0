namespace ZeManage.Agent.Core.Models;

public sealed class HardwareSnapshot
{
    public long Id { get; set; }
    public required string MachineName { get; set; }
    public DateTime CapturedAt { get; set; }
    public double CpuUsagePercent { get; set; }
    public double RamUsagePercent { get; set; }
    public double RamUsedGB { get; set; }
    public double RamTotalGB { get; set; }
    public double GpuUsagePercent { get; set; }
    public double DiskUsagePercent { get; set; }
    public double DiskFreeGB { get; set; }
    public double DiskTotalGB { get; set; }
    public int? BatteryPercent { get; set; }
    public string? BatteryStatus { get; set; }
    public bool Synced { get; set; }
}
