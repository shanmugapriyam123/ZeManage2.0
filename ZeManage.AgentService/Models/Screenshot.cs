namespace ZeManage.AgentService.Models;

public sealed class Screenshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CapturedAt      { get; set; }
    public string TriggerEvent      { get; set; } = "Timer";
    public string? ApplicationId    { get; set; }
    public string? ProcessName      { get; set; }
    public required string FilePath { get; set; }
    public long FileSizeBytes       { get; set; }
    public bool Synced              { get; set; }
    public DateTime CreatedAt       { get; set; }
    public DateTime UpdatedAt       { get; set; }
}
