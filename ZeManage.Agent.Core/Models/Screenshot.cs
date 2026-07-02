namespace ZeManage.Agent.Core.Models;

public sealed class Screenshot
{
    public long Id { get; set; }
    public required string UserName    { get; set; }
    public required string MachineName { get; set; }
    public string? WindowsSid         { get; set; }
    public DateTime CapturedAt        { get; set; }
    public string TriggerEvent        { get; set; } = "Timer";
    public required string FilePath   { get; set; }
    public long FileSizeBytes         { get; set; }
    public bool Synced                { get; set; }
}
