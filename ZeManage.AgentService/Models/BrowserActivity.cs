namespace ZeManage.AgentService.Models;

public sealed class BrowserActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Browser   { get; set; }
    public required string PageTitle { get; set; }
    public string? ApplicationId     { get; set; }
    public string? Url               { get; set; }
    public DateTime StartTime        { get; set; }
    public DateTime EndTime          { get; set; }
    public long DurationSeconds      { get; set; }
    public bool Synced               { get; set; }
    public DateTime CreatedAt        { get; set; }
    public DateTime UpdatedAt        { get; set; }
}
